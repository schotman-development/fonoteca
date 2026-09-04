using System.Collections.Frozen;
using System.Globalization;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Which files in the library are worth replacing with a better encoding.
/// </summary>
/// <remarks>
/// Two questions, and they are answerable from different evidence — which is the
/// whole shape of this rule.
///
/// <b>"Is this lossy?"</b> the catalogue can answer from a filename. What Qobuz
/// sells is FLAC, so a lossy file is a candidate whatever else is true of it,
/// and <see cref="AudioQuality"/>'s tier ordering already says lossless beats
/// lossy at any bitrate.
///
/// <b>"Is this lossless file below what exists?"</b> it cannot. 16/44.1 against
/// a 24/96 master is a real upgrade and the biggest one in a mostly-FLAC
/// library — measured on the target library, <b>434 album folders are entirely
/// CD-quality FLAC against 87 that are lossy</b> — and neither depth nor rate is
/// in the catalogue until something opens the file. So that half is answered
/// only where <c>MediaFile.Quality</c> has been filled in, which is what
/// <c>ProbeService</c> exists to do; an unprobed lossless file reports
/// <see cref="UpgradeReason.None"/> rather than a guess.
///
/// Guessing was considered and is worse than silence. Implied bitrate separates
/// a 24/96 FLAC from a 16/44.1 one on paper, and on this library it does not: a
/// mono 1930s Louis Armstrong reissue and a quiet Shostakovich symphony both
/// compress to 300-500 kbps as genuine CD audio, which is where a transcode
/// would sit too. The number that settles it comes from a decoder.
/// </remarks>
public static class UpgradeScan
{
    /// <summary>
    /// Extensions whose contents are lossless, without leading dots.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="AudioFormats"/>, split the way the probe splits
    /// codecs — and <c>m4a</c> is deliberately absent from both sides of that
    /// split, because it is ALAC about as often as it is AAC and the extension
    /// cannot say which. It therefore reads as lossy here, which is the error
    /// worth making: an ALAC album on the list costs one wasted click, while an
    /// AAC album missing from it is an upgrade nobody is ever offered.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex Brackets =
        new(@"\([^)]*\)|\[[^\]]*\]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static readonly System.Text.RegularExpressions.Regex Whitespace =
        new(@"\s+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static readonly FrozenSet<string> Lossless = new[]
    {
        "flac", "wav", "aiff", "aif", "alac", "ape", "wv", "tta", "dsf", "dff",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lossless extensions, for the test that holds this list against
    /// <see cref="AudioFormats"/>'s.
    /// </summary>
    /// <remarks>
    /// Two hand-maintained lists with no compiler relationship, and drift
    /// between them is silent in the worse direction — an extension this rule
    /// has never heard of files every such album as an upgrade forever.
    /// </remarks>
    public static IReadOnlySet<string> LosslessExtensions => Lossless;

    /// <summary>
    /// True when nothing better than this file can be bought — as far as the
    /// catalogue can tell without opening it.
    /// </summary>
    /// <remarks>
    /// <paramref name="measured"/> wins whenever it exists, because it came from
    /// a decoder rather than from a filename. That is not a nicety: the forty
    /// ID3-prefixed FLACs in the target library and every mis-extended <c>.m4a</c>
    /// are exactly the files a name gets wrong, and they are disproportionately
    /// the ones a person ends up looking at.
    /// </remarks>
    public static bool IsLossless(string path, AudioQuality? measured) =>
        measured?.IsLossless ?? Lossless.Contains(Extension(path));

    /// <summary>Why this file is worth replacing, or that it is not.</summary>
    /// <remarks>
    /// <b>Silence and "no" are the same answer here, deliberately.</b> An
    /// unprobed FLAC and a probed 24/192 one both come back
    /// <see cref="UpgradeReason.None"/>, so a library nobody has probed reports
    /// only its lossy half rather than reporting every FLAC as a maybe. The
    /// pass is what turns the first case into an answer; a screen full of
    /// question marks is not an answer and cannot be acted on.
    ///
    /// The hi-res line itself is <see cref="AudioQuality.Tier"/>'s, not a second
    /// copy of it — <see cref="QualityTier.LosslessCd"/> already means "lossless
    /// and better than this exists", counting depth and rate as separate axes so
    /// that 24/44.1 and 16/96 are each above CD.
    /// </remarks>
    public static UpgradeReason Assess(string path, AudioQuality? measured)
    {
        if (!IsLossless(path, measured)) return UpgradeReason.Lossy;

        if (measured is null) return UpgradeReason.None;

        return measured.Tier == QualityTier.LosslessCd
            ? UpgradeReason.BelowHiRes
            : UpgradeReason.None;
    }

    /// <summary>
    /// What a file is, in the words a person reads on a row — <c>MP3</c>,
    /// <c>FLAC 16/44.1</c>.
    /// </summary>
    /// <remarks>
    /// The container comes from the extension rather than from
    /// <see cref="AudioQuality.Codec"/>, which is whatever wrote it: the current
    /// probe says <c>mp3</c> and the rows the TagLib# path left behind say
    /// <c>MPEG Version 1 Audio, Layer 3</c>, and a column mixing the two reads as
    /// two different problems.
    ///
    /// The depth and rate beside it are numbers rather than names, so they carry
    /// no such spelling problem — and they are the entire reason a row is on the
    /// list at all when the container is already FLAC. They are printed only
    /// when something measured them.
    /// </remarks>
    public static string Format(string path, AudioQuality? measured)
    {
        var container = Extension(path).ToUpperInvariant();

        if (measured is null || !measured.IsLossless || container.Length == 0) return container;

        var rate = (measured.SampleRateHz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

        return measured.BitDepth is { } depth
            ? $"{container} {depth}/{rate}"
            : $"{container} {rate}kHz";
    }

    /// <summary>
    /// The <c>Artist/Album</c> a file sits in, for a file no pass has filed.
    /// </summary>
    /// <remarks>
    /// At most two segments of the file's <i>directory</i>, which is the depth
    /// the by-hand matching screen already cuts at — so <c>CD1</c> and <c>CD2</c>
    /// of a set collapse onto one row rather than becoming two albums to buy.
    /// The directory rather than the path, or a file one level down becomes an
    /// album folder named after itself and every loose file is its own album. A
    /// file at the library root has no directory and groups under its own name,
    /// which is honest and rare.
    ///
    /// The cut itself is <see cref="Catalogue.AlbumFolder"/>, which the
    /// attribution pass now groups its components by as well. Only the root-file
    /// case differs, and it differs on purpose — see below.
    /// </remarks>
    public static string AlbumFolder(string path)
    {
        var folder = Catalogue.AlbumFolder.Of(path);

        // A file at the library root has no folder to cut. The attribution pass
        // reads that emptiness as "its own component"; here it has to be a row
        // heading somebody can read, so it groups under its own name.
        return folder.Length == 0 ? path.Replace('\\', '/') : folder;
    }

    /// <summary>
    /// A folder name with what rippers put in brackets taken out, for a search
    /// box.
    /// </summary>
    /// <remarks>
    /// <c>Katelyn Tarver/Quitter (2023)</c> is a fine row heading and a poor
    /// query: MusicBrainz and Qobuz both index album titles, and
    /// <c>[FLAC 24-192]</c> or a year is two or three words of noise against
    /// one. Only the query is cleaned — the heading keeps the folder's own name,
    /// or the row stops matching the directory a person is looking at.
    ///
    /// <c>web/src/pages/seating.ts</c> holds the same rule for the by-hand
    /// matching screen. Two copies across two toolchains, like every other rule
    /// that has to run in both.
    /// </remarks>
    public static string Searchable(string name)
    {
        var cleaned = Brackets.Replace(name, " ");

        return Whitespace.Replace(cleaned, " ").Trim();
    }

    private static string Extension(string path)
    {
        var dot = path.LastIndexOf('.');
        var separator = path.LastIndexOfAny(['/', '\\']);

        // A dot in a directory name is not this file's extension, and a file
        // with no dot at all has none — both would otherwise slice the path.
        return dot <= separator + 1 ? string.Empty : path[(dot + 1)..];
    }
}

/// <summary>
/// Why an album is on the upgrade list. Ordered by how much better the
/// replacement is, so an album's reason is the worst of its files'.
/// </summary>
public enum UpgradeReason
{
    /// <summary>Nothing better can be bought, or nothing has measured it yet.</summary>
    None = 0,

    /// <summary>Lossless, and CD rather than hi-res. Needs a measurement to know.</summary>
    BelowHiRes = 1,

    /// <summary>Lossy, whatever the bitrate. Knowable from the container alone.</summary>
    Lossy = 2,
}
