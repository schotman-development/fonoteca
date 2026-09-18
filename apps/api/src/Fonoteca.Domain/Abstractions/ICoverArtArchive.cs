using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// The Cover Art Archive: every image MusicBrainz holds against a release.
/// </summary>
/// <remarks>
/// <b>The archive's own "front" is a suggestion, not an answer.</b> It is the
/// first image typed <c>Front</c> in the release's order, and a release can
/// carry several — <i>Back to Tennessee</i> has a square front and a 2:1
/// fold-out spread, and the spread comes first. Hence a list to choose from
/// rather than a single picture.
/// </remarks>
public interface ICoverArtArchive
{
    /// <summary>Every image held against this release, in the archive's order.</summary>
    /// <returns>Empty when the archive holds nothing for it — an answer, not a failure.</returns>
    /// <exception cref="ProviderUnavailableException">The archive could not be reached.</exception>
    Task<IReadOnlyList<CoverArtImage>> ListAsync(
        Mbid release,
        CancellationToken cancellationToken = default);

    /// <summary>One image, at the archive's 500px rendition.</summary>
    /// <exception cref="ProviderUnavailableException">The archive could not be reached.</exception>
    Task<CoverArtBytes> DownloadAsync(
        Mbid release,
        long imageId,
        CancellationToken cancellationToken = default);
}

/// <param name="Front">Whether this is the one the archive itself calls the front.</param>
public sealed record CoverArtImage(
    long Id,
    bool Front,
    IReadOnlyList<string> Types,
    string? Comment);

public sealed record CoverArtBytes(byte[] Bytes, string MediaType);
