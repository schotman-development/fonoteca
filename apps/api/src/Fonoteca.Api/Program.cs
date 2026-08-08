using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Api.Realtime;
using Fonoteca.Api.Startup;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Ingest;
using Fonoteca.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration — validated before the host starts.
// ---------------------------------------------------------------------------

builder.Services
    .AddOptions<FonotecaOptions>()
    .Bind(builder.Configuration.GetSection(FonotecaOptions.SectionName))
    .ValidateDataAnnotations()
    // Fail at startup rather than at first use. A misconfigured library path
    // discovered three hours into a scan is a much worse failure.
    .ValidateOnStart();

// The same settings again, read eagerly, because service registration happens
// before there is a container to resolve IOptions from. It is a copy used only
// for wiring: the validated instance above is still what the application reads
// at runtime, and a value bad enough to matter stops the host from starting
// either way.
var fonoteca = builder.Configuration
    .GetSection(FonotecaOptions.SectionName)
    .Get<FonotecaOptions>() ?? new FonotecaOptions();

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("Fonoteca")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Fonoteca is not configured. See compose.yaml and .env.example.");

builder.Services.AddDbContext<FonotecaDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.MigrationsAssembly(typeof(FonotecaDbContext).Assembly.FullName);
        npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null);
    });

    if (builder.Environment.IsDevelopment())
    {
        options.EnableDetailedErrors();
        options.EnableSensitiveDataLogging();
    }
});

// ---------------------------------------------------------------------------
// Domain seams. Each has exactly one production implementation; tests
// substitute their own. See the "Designed for, not built" section of the plan
// for why ICallerContext exists in a single-user application.
// ---------------------------------------------------------------------------

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<ICallerContext, SingleUserCallerContext>();

// The library root is configuration, so the store is built from it rather than
// resolved by convention. Registered twice on purpose: as the interface for
// anything that only needs to read bytes, and as itself for the scan service,
// which has to ask whether the root is mounted at all — a question about this
// particular backing store, not about file stores in general.
builder.Services.AddSingleton(sp => new FileSystemAudioFileStore(
    sp.GetRequiredService<IOptions<FonotecaOptions>>().Value.LibraryPath));
builder.Services.AddSingleton<IAudioFileStore>(
    sp => sp.GetRequiredService<FileSystemAudioFileStore>());

builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<LibraryScanService>();

// ---------------------------------------------------------------------------
// External services. Both are registered unconditionally, including when the
// key or the contact is missing: the failure then names the setting at the
// point of use, which is far more useful than a resolution error saying
// IAcoustIdLookup is not registered.
// ---------------------------------------------------------------------------

builder.Services.AddAcoustId(options => options.ApiKey = fonoteca.AcoustIdApiKey);

builder.Services.AddMusicBrainz(options =>
{
    options.Server = new Uri(fonoteca.MusicBrainzServer);
    options.Contact = fonoteca.MusicBrainzContact;
    options.MinimumRequestInterval =
        TimeSpan.FromMilliseconds(fonoteca.MusicBrainzRequestIntervalMs);

    // The real version, so a request MusicBrainz has to ask about can be traced
    // to a build. An honest User-Agent is the whole basis of their rate policy.
    options.ApplicationVersion = ThisAssembly.Version;
});

// ---------------------------------------------------------------------------
// Web
// ---------------------------------------------------------------------------

builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// Migrations and configuration checks. In a hosted service rather than inline
// after Build(), because tools that construct the host without running it —
// the build-time OpenAPI generator, `dotnet ef` — would otherwise execute them
// as a side effect of `dotnet build`. See StartupTasks.
builder.Services.AddHostedService<StartupTasks>();
builder.Services.AddHostedService<HeartbeatService>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<FonotecaDbContext>(
        name: "database",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

const string CorsPolicy = "fonoteca-web";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    var origins = builder.Configuration
        .GetSection($"{FonotecaOptions.SectionName}:CorsOrigins")
        .Get<string[]>() ?? [];

    policy.WithOrigins(origins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Required for SignalR: the negotiate handshake sends credentials.
        .AllowCredentials();
}));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicy);

app.MapHealthChecks("/health");
app.MapSystemEndpoints();
app.MapLibraryEndpoints();
app.MapHub<JobsHub>(JobsHub.Route);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host with WebApplicationFactory.</summary>
public partial class Program;
