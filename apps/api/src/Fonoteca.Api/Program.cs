using System.Text.Json.Serialization;
using Fonoteca.Api.Acquisition;
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

// The third tool, and the only thing here qualified to measure audio: a tag
// library asked about a VBR MP3 answers with the first frame's bitrate, and
// asked about a truncated FLAC answers off an intact header. See IAudioProbe.
builder.Services.AddSingleton<IAudioProbe>(sp => new FfprobeAudioProbe(
    sp.GetRequiredService<FileSystemAudioFileStore>(),
    sp.GetRequiredService<IOptions<FonotecaOptions>>().Value.FfprobePath));

builder.Services.AddSingleton<TagReader>();

// The read that answers "what is this file", as opposed to the read that
// authorises a write. Separate from TagReader because its contract is the
// opposite one: it never throws, it truncates, and nothing is decided from it —
// it is what the manual matching screen puts in front of a person.
builder.Services.AddSingleton<AudioFileDescriber>();

// The one switch that decides whether this process may modify a file at all.
// It is read here, once, so Fonoteca.Tagging never learns what FonotecaOptions
// is — and so there is exactly one place to look for the answer.
builder.Services.AddSingleton(new TagWriterOptions
{
    AllowFileMutation = fonoteca.AllowFileMutation,
});

builder.Services.AddScoped<IEventLog, EventLog>();
builder.Services.AddScoped<AcoustIdTagWriter>();

// The same verified write path, told what to write rather than knowing. Scoped
// beside the one above because both need a request's IEventLog.
builder.Services.AddScoped<TagWriter>();

// One at a time across the whole library: a scan clears the very columns an
// identification pass is filling in, so the two must not overlap.
builder.Services.AddSingleton<LibraryWorkGate>();
builder.Services.AddSingleton<IdentificationService>();
builder.Services.AddSingleton<EnrichmentService>();
builder.Services.AddSingleton<ReleaseAttributionService>();
builder.Services.AddSingleton<ProbeService>();

// The one pass that rewrites audio files, and the only one nothing may start on
// its own — see TagWriteService. It is here because it is a pass; it is reached
// only from a button because of what it does.
builder.Services.AddSingleton<TagWriteService>();
builder.Services.AddScoped<AlbumReplacementService>();
builder.Services.AddScoped<FileManagerService>();

// Registered as a hosted service as well as a singleton, so shutdown cancels a
// running pass and waits for it rather than severing it mid-write.
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdentificationService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EnrichmentService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleaseAttributionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbeService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TagWriteService>());

// The worklist's answers, built before somebody clicks rather than while they
// wait. Hosted only — nothing else holds a reference to it.
builder.Services.AddHostedService<CandidateWarmService>();

// ---------------------------------------------------------------------------
// External services. Both are registered unconditionally, including when the
// key or the contact is missing: the failure then names the setting at the
// point of use, which is far more useful than a resolution error saying
// IAcoustIdLookup is not registered.
// ---------------------------------------------------------------------------

builder.Services.AddAcoustId(options =>
{
    options.ApiKey = fonoteca.AcoustIdApiKey;
    options.UserKey = fonoteca.AcoustIdUserKey;
});

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

// Pictures of artists, which no other provider here has. The contact is
// MusicBrainz's, not a second setting: Wikimedia's user-agent policy asks the
// same question, and making an operator answer it twice is how one of the two
// ends up blank.
builder.Services.AddWikidata(options =>
{
    options.Contact = fonoteca.MusicBrainzContact;
});

// A third picture of artists, and the one that is looked up by MusicBrainz id
// rather than searched by name — so it is the only source that can reach an
// artist whose name is not spelled in Latin script. It needs no credential
// (they ship a public test key), so it works on a fresh install, which is what
// lets it sit in the chain rather than behind a flag.
builder.Services.AddAudioDbPortraits(options =>
{
    var audioDb = fonoteca.Providers.AudioDb;

    // Empty means "use their published test key", which is the default on the
    // options class. Assigning the empty string over it would turn a blank line
    // in a .env file into a source that refuses every lookup.
    if (!string.IsNullOrWhiteSpace(audioDb.ApiKey)) options.ApiKey = audioDb.ApiKey;
});

// Manual acquisition. Registered unconditionally like the other two, so an
// unconfigured instance answers "not configured" from the endpoint rather than
// failing to resolve a service. Nothing here runs on its own: no hosted
// service, no worklist, no schedule.
builder.Services.AddQobuz(options =>
{
    var qobuz = fonoteca.Providers.Qobuz;

    options.AppId = qobuz.AppId;
    options.AppSecret = qobuz.AppSecret;
    options.UserAuthToken = qobuz.UserAuthToken;
    options.FormatId = qobuz.FormatId;

    // Clamped here rather than annotated on the options class: these settings
    // are nested, and ValidateDataAnnotations does not recurse into complex
    // properties — so a [Range] on them never runs. A negative value would
    // otherwise reach RequestGate's own guard and throw at first resolve,
    // which is a stack trace where a slow default belongs.
    options.MinimumRequestInterval =
        TimeSpan.FromMilliseconds(Math.Clamp(qobuz.MinRequestIntervalMs, 0, 60_000));
});

// Singleton, because the one-at-a-time semaphore in it has to be one semaphore.
builder.Services.AddSingleton<QobuzDownloadService>();

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
app.MapFileEndpoints();
app.MapCatalogueEndpoints();
app.MapQobuzEndpoints();
app.MapHub<JobsHub>(JobsHub.Route);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration tests can drive the real host with WebApplicationFactory.</summary>
public partial class Program;
