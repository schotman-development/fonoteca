using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Startup;

/// <summary>
/// Work that must happen once, when the application actually starts.
/// </summary>
/// <remarks>
/// This lives in a hosted service rather than inline in Program for a specific
/// reason: several tools build the host <b>without running it</b>. The
/// build-time OpenAPI generator and <c>dotnet ef</c> both use
/// <c>HostFactoryResolver</c>, which executes Program up to <c>builder.Build()</c>
/// and then takes over.
///
/// Inline startup code therefore runs during an ordinary <c>dotnet build</c> —
/// which meant this project tried to connect to PostgreSQL and apply migrations
/// as a side effect of compiling. Hosted services are only started by a real
/// host, so the work happens exactly when it should.
/// </remarks>
public sealed class StartupTasks(
    IServiceProvider services,
    IOptions<FonotecaOptions> options,
    ILogger<StartupTasks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostRuntime.IsDesignTime)
        {
            // The OpenAPI generator or `dotnet ef` is inspecting the host, not
            // running the app. Touching a database here would make `dotnet build`
            // fail without PostgreSQL — and silently migrate a real one with it.
            Log.DesignTimeStartupSkipped(logger);
            return;
        }

        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            // Migrating on boot suits a single-user, single-instance, self-hosted
            // app. It would be wrong with several replicas racing to migrate;
            // revisit if the API is ever scaled out.
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            Log.MigrationsApplied(logger);
        }

        var config = options.Value;

        if (!Directory.Exists(config.LibraryPath))
        {
            // A warning, not a failure. The mount may appear after the API does,
            // and refusing to start would make the UI unreachable at exactly the
            // moment someone needs it to explain why.
            Log.LibraryPathMissing(logger, config.LibraryPath);
        }

        if (!config.AllowFileMutation)
        {
            Log.FileMutationDisabled(logger);
        }

        // Information, not a warning, and certainly not a failure. Neither
        // credential is needed to scan, browse or serve a library, and an
        // installation that never identifies anything is a legitimate one.
        // Saying so once at boot is what makes "why did identification do
        // nothing?" answerable without reading code.
        if (string.IsNullOrWhiteSpace(config.AcoustIdApiKey))
        {
            Log.AcoustIdNotConfigured(logger);
        }

        if (string.IsNullOrWhiteSpace(config.MusicBrainzContact))
        {
            Log.MusicBrainzNotConfigured(logger);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
