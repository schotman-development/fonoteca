using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Tagging;

/// <summary>
/// Everything a file can be told to say about itself, for a person to read.
/// </summary>
/// <remarks>
/// <b>Not a <see cref="TagSnapshot"/>, and the difference is what it is for.</b>
/// A snapshot is evidence in a write: it is compared field-for-field against a
/// second reading, it is the undo journal's payload, and every value in it has
/// to be exact or the write aborts. This is the opposite kind of object — it is
/// read once, printed, and nothing is decided from it. So it is allowed to be
/// partial, it truncates long values, and a library that throws contributes
/// nothing rather than failing the read.
///
/// It exists because the manual matching screen was asking a person to choose
/// between recordings while showing them a filename. Length, bitrate, sample
/// rate and whatever the file's own tags claim are the facts that settle which
/// of two near-identical candidates a file actually holds — a 3:58 track is not
/// a 4:21 one, and a file carrying <c>MUSICBRAINZ_TRACKID</c> has answered the
/// question outright.
/// </remarks>
public sealed record AudioFileReading
{
    /// <summary>
    /// What the decoder measured, or null when it would not read the file.
    /// </summary>
    /// <remarks>
    /// Null is a real answer here and reaches the screen as one: the library
    /// volume may not be mounted, the file may have been moved since the last
    /// scan, and a file the decoder refuses is exactly the kind that ends up
    /// needing a person in the first place. <see cref="Note"/> then says which.
    ///
    /// It is never a tag library's opinion. See
    /// <see cref="AudioFileDescriber"/> for what that costs.
    /// </remarks>
    public required AudioQuality? Quality { get; init; }

    /// <summary>How long the audio runs, as the decoder measured it just now.</summary>
    /// <remarks>
    /// <b>Not the same reading as the fingerprint's, and both are worth having.</b>
    /// <c>fpcalc</c> measured a file once, when the identification pass reached
    /// it; this is measured now. They agree on a healthy file, and a gap between
    /// them means the bytes changed under the catalogue — which is worth seeing
    /// on a screen whose whole job is files something went wrong with.
    ///
    /// It also stands alone, which matters more than the comparison: a third of
    /// the files on the target library's worklist have no fingerprint duration
    /// at all, because their AcoustID was adopted from an existing tag and
    /// <c>fpcalc</c> never ran. For those this is the <i>only</i> length
    /// anything knows, and the request that prompted all of this asked for the
    /// length.
    /// </remarks>
    public required TimeSpan? Duration { get; init; }

    /// <summary>
    /// Whether the decoder read the whole stream without remark.
    /// </summary>
    /// <remarks>
    /// <b>The flag that decides whether <see cref="Quality"/> may be written
    /// down.</b> A measurement the decoder objected to is worth showing — it is
    /// often the most useful thing on the screen, since a file nothing could
    /// identify and a file nothing can fully decode are frequently the same file
    /// — and it must not become an input to a rule that ranks files by quality.
    /// See <see cref="AudioProbeReading"/> for what a complaint costs and how
    /// often one happens.
    ///
    /// False when nothing was measured at all, which is the same answer for the
    /// same reason.
    /// </remarks>
    public required bool DecodedCleanly { get; init; }

    /// <summary>Whatever tags survived, in the order a person would read them.</summary>
    public required IReadOnlyList<TagValue> Tags { get; init; }

    /// <summary>Why something is missing, when something is. Null when nothing is.</summary>
    public required string? Note { get; init; }

}

/// <summary>One tag, flattened to a printable pair.</summary>
/// <remarks>
/// A list rather than a dictionary because the order is the information: the
/// fields a person looks for first are the ones a tagger writes first, and an
/// alphabetical sort puts <c>ALBUM</c> above <c>TITLE</c> and
/// <c>ACOUSTID_ID</c> above both.
/// </remarks>
public sealed record TagValue(string Name, string Value);

/// <summary>One picture out of a file's tags, ready to be served as it was stored.</summary>
/// <remarks>
/// The bytes are handed on untouched. Nothing here scales, re-encodes or crops:
/// that would mean an image library this project does not have, on a request one
/// person made about one file. The browser is already a competent image scaler.
/// </remarks>
public sealed record EmbeddedArtwork(byte[] Bytes, string MimeType);
