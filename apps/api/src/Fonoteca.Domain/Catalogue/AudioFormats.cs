using System.Collections.Frozen;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which files in a library are audio.
/// </summary>
/// <remarks>
/// An allowlist, deliberately. A music library is full of things that are not
/// music — artwork, cue sheets, logs, playlists, <c>.DS_Store</c>, the odd PDF
/// booklet — and a denylist would have to anticipate all of them. An allowlist
/// gets the failure mode the right way round: an unrecognised extension is
/// silently ignored rather than catalogued as a track that can never be probed.
///
/// The list is limited to containers this application can actually read
/// metadata from and rank the quality of. Adding an extension here is a claim
/// that ffprobe and the tag libraries understand it.
///
/// A rule, not I/O — which is why it lives in the domain and is testable with a
/// string.
/// </remarks>
public static class AudioFormats
{
    /// <summary>Extensions without the leading dot, matched case-insensitively.</summary>
    private static readonly FrozenSet<string> KnownExtensions = new[]
    {
        // Lossless
        "flac", "wav", "aiff", "aif", "alac", "ape", "wv", "tta",

        // Lossless, DSD. Present in hi-res purchases; probe-able, not tag-able
        // by every library, which is a problem for later rather than now.
        "dsf", "dff",

        // Lossy
        "mp3", "m4a", "m4b", "aac", "ogg", "oga", "opus", "wma", "mpc",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Span-keyed view of <see cref="KnownExtensions"/>, so testing a path does
    /// not allocate a string for its extension. At 100k files per scan that is
    /// the difference between a hundred thousand short-lived allocations and none.
    /// </summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup =
        KnownExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The recognised extensions, without leading dots. For diagnostics and tests.</summary>
    public static IReadOnlySet<string> Extensions => KnownExtensions;

    public static bool IsAudioFile(LibraryPath path) => IsAudioFile(path.Value);

    /// <summary>True when <paramref name="path"/> ends in a recognised audio extension.</summary>
    public static bool IsAudioFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var name = path.AsSpan();

        // Only the last segment matters: a directory called "album.flac" must
        // not make every file inside it look like audio.
        var separator = name.LastIndexOfAny('/', '\\');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        var dot = name.LastIndexOf('.');

        // `dot == 0` is a dotfile — ".flac" is a hidden file named for the
        // format, not a track — and macOS resource forks ("._track.flac") are
        // caught by the same reasoning one level up.
        if (dot <= 0 || name.StartsWith("._", StringComparison.Ordinal)) return false;

        return Lookup.Contains(name[(dot + 1)..]);
    }
}
