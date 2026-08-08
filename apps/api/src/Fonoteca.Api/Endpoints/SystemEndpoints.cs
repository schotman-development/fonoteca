using System.Text.Json.Serialization;
using Fonoteca.Api.Configuration;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// System and diagnostics endpoints.
/// </summary>
/// <remarks>
/// Grouped by capability rather than by HTTP verb. Feature endpoints will
/// follow the same shape: one static class per capability, one
/// <c>MapXEndpoints</c> extension, registered from Program.
/// </remarks>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").WithTags("System");

        group.MapGet("/info", GetSystemInfo)
            .WithName("GetSystemInfo")
            .WithSummary("Runtime configuration and catalogue counts.");

        group.MapGet("/musicbrainz", GetMusicBrainzHealth)
            .WithName("GetMusicBrainzHealth")
            .WithSummary("Which MusicBrainz server is in use, and whether it is answering.");

        return app;
    }

    /// <remarks>
    /// Separate from <c>/health</c> on purpose: this reports on a dependency the
    /// application is designed to survive the loss of, and readiness must not
    /// hinge on it. See <see cref="MusicBrainzHealthProbe"/>.
    ///
    /// Safe to poll — the probe caches, so the response can be served without
    /// spending a turn at the rate gate that identification work queues behind.
    /// </remarks>
    private static async Task<MusicBrainzHealthResponse> GetMusicBrainzHealth(
        MusicBrainzHealthProbe probe,
        CancellationToken cancellationToken)
    {
        var health = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);

        return new MusicBrainzHealthResponse(
            Server: health.Server.ToString(),
            IsOfficialServer: health.IsOfficialServer,
            MinimumRequestIntervalMs: (int)health.MinimumRequestInterval.TotalMilliseconds,
            ContactConfigured: health.ContactConfigured,
            Status: health.Reachability,
            LatencyMs: health.Latency is { } latency ? (int)latency.TotalMilliseconds : null,
            Detail: health.Detail,
            CheckedAtUtc: health.CheckedAt);
    }

    private static async Task<SystemInfoResponse> GetSystemInfo(
        FonotecaDbContext db,
        IOptions<FonotecaOptions> options,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var config = options.Value;

        return new SystemInfoResponse(
            Version: ThisAssembly.Version,
            ServerTimeUtc: clock.UtcNow,
            LibraryPath: config.LibraryPath,
            // Surfaced deliberately: the UI shows a banner when writes are off,
            // so "why did nothing change?" is answerable at a glance.
            FileMutationAllowed: config.AllowFileMutation,
            Counts: new CatalogueCounts(
                Files: await db.MediaFiles.CountAsync(cancellationToken).ConfigureAwait(false),
                Recordings: await db.Recordings.CountAsync(cancellationToken).ConfigureAwait(false),
                Releases: await db.Releases.CountAsync(cancellationToken).ConfigureAwait(false),
                Artists: await db.Artists.CountAsync(cancellationToken).ConfigureAwait(false)));
    }
}

public sealed record SystemInfoResponse(
    string Version,
    DateTimeOffset ServerTimeUtc,
    string LibraryPath,
    bool FileMutationAllowed,
    CatalogueCounts Counts);

public sealed record CatalogueCounts(int Files, int Recordings, int Releases, int Artists);

/// <param name="Server">The configured server, so the UI can say which one is in use.</param>
/// <param name="IsOfficialServer">Whether that is musicbrainz.org, where the rate limit is theirs to enforce.</param>
/// <param name="MinimumRequestIntervalMs">The gate's interval. Zero means ungated, which only a mirror earns.</param>
/// <param name="ContactConfigured">Whether lookups can be sent at all.</param>
/// <param name="Status">What the last probe found.</param>
/// <param name="LatencyMs">Round trip including time queued at the gate; null when nothing was sent.</param>
/// <param name="Detail">Why, when the status is not <c>Reachable</c>.</param>
/// <param name="CheckedAtUtc">
/// When the reading was taken. Not when it was requested — readings are cached
/// for half a minute, and a display that implies otherwise is lying about how
/// current it is.
/// </param>
public sealed record MusicBrainzHealthResponse(
    string Server,
    bool IsOfficialServer,
    int MinimumRequestIntervalMs,
    bool ContactConfigured,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MusicBrainzReachability>))]
    MusicBrainzReachability Status,
    int? LatencyMs,
    string? Detail,
    DateTimeOffset CheckedAtUtc);

internal static class ThisAssembly
{
    public static string Version =>
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
