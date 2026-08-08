using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Realtime;
using Fonoteca.Api.Startup;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
app.MapHub<JobsHub>(JobsHub.Route);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host with WebApplicationFactory.</summary>
public partial class Program;
