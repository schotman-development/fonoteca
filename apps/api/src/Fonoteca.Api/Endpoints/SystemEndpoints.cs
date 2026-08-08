using Fonoteca.Api.Configuration;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
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

        return app;
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

internal static class ThisAssembly
{
    public static string Version =>
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
