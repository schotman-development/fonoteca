using System.Text.Json.Serialization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Api.Realtime;
using Fonoteca.Api.Startup;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Events;
using Fonoteca.Ingest;
using Fonoteca.Providers;
using Fonoteca.Tagging;
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
// Identification: fingerprint, look up, tag.
// ---------------------------------------------------------------------------

// fpcalc takes a path and opens the file itself, so the fingerprinter needs the
// store rather than a stream (ADR 0001). Plain constructor values rather than
// IOptions, because Fonoteca.Ingest carries no NuGet references and keeping it
// that way is worth a lambda here.
builder.Services.AddSingleton<IAudioFingerprinter>(sp => new FpcalcFingerprinter(
    sp.GetRequiredService<FileSystemAudioFileStore>(),
    sp.GetRequiredService<IOptions<FonotecaOptions>>().Value.FpcalcPath));

builder.Services.AddSingleton<TagReader>();

// The one switch that decides whether this process may modify a file at all.
// It is read here, once, so Fonoteca.Tagging never learns what FonotecaOptions
// is — and so there is exactly one place to look for the answer.
builder.Services.AddSingleton(new TagWriterOptions
{
    AllowFileMutation = fonoteca.AllowFileMutation,
});

builder.Services.AddScoped<IEventLog, EventLog>();
builder.Services.AddScoped<AcoustIdTagWriter>();

// One at a time across the whole library: a scan clears the very columns an
// identification pass is filling in, so the two must not overlap.
builder.Services.AddSingleton<LibraryWorkGate>();
builder.Services.AddSingleton<IdentificationService>();
builder.Services.AddSingleton<EnrichmentService>();

// Registered as a hosted service as well as a singleton, so shutdown cancels a
// running pass and waits for it rather than severing it mid-write.
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdentificationService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EnrichmentService>());

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

// Numbers are numbers.
//
// ASP.NET's web JSON defaults set NumberHandling to AllowReadingFromString, and
// .NET 10's OpenAPI generator reports that honestly: every int in the document
// comes out as `["integer", "string"]`, so the generated TypeScript types every
// numeric field as `string | number`. Arithmetic on a count then fails to
// compile, and the workaround everyone reaches for — hand-declaring the
// response shape in the component — quietly abandons the generated contract
// that the OpenAPI seam exists to enforce.
//
// Strict costs nothing here: no endpoint accepts a JSON request body, and the
// responses were always going to be written as numbers. SignalR is unaffected;
// it carries its own protocol options.
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

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
app.MapCatalogueEndpoints();
app.MapHub<JobsHub>(JobsHub.Route);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host with WebApplicationFactory.</summary>
public partial class Program;
