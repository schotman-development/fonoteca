using System.Security.Cryptography;

namespace Fonoteca.Tagging;

/// <summary>
/// What a file's tags said at one moment, as both libraries saw them.
/// </summary>
/// <remarks>
/// Three jobs. It is the "before" half of the dry-run diff; it is what the
/// verification step compares the staged file against, so a write that silently
/// dropped an unrelated field is caught; and it is the undo journal's payload.
///
/// <b>Artwork is hashed, never carried.</b> Three megabytes of cover art times
/// 7,735 rows is about 23 GB of JSONB for an operation that changes one
/// 36-character string. The count and the digest prove the pictures survived,
/// which is all the verification needs — and if they had not survived, the write
/// aborts before the commit, so the journal never has to restore any.
/// </remarks>
public sealed record TagSnapshot
{
    /// <summary>The AcoustID the file claims, or null if it carries none.</summary>
    public required string? AcoustId { get; init; }

    /// <summary>
    /// Every other tag, so a write can be proved not to have disturbed them.
    /// </summary>
    /// <remarks>
    /// Ordinal-keyed and sorted, because two readings are compared for equality
    /// and dictionary order is not a promise either library makes.
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Fields { get; init; }

    public required int PictureCount { get; init; }

    /// <summary>SHA-256 of each embedded picture, in the order they appear.</summary>
    public required IReadOnlyList<string> PictureDigests { get; init; }

    /// <summary>Seconds, as the container reports them. A rewrite that lost the audio changes this.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Average bitrate. The other half of "is the audio still there".</summary>
    public required int BitrateKbps { get; init; }

    /// <summary>
    /// Whether this reading and another agree about everything that matters.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>==</c> on the record. Two libraries reading the same
    /// file legitimately disagree about tag-mapping conventions — ADR 0002 says
    /// as much — so equality of the whole <see cref="Fields"/> map across
    /// libraries is not a meaningful test. What must agree is the AcoustID, the
    /// artwork and the audio.
    /// </remarks>
    public bool AgreesWith(TagSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(AcoustId, other.AcoustId, StringComparison.OrdinalIgnoreCase)
            && PictureCount == other.PictureCount
            && PictureDigests.SequenceEqual(other.PictureDigests, StringComparer.Ordinal)

            // A second is generous, and it has to be: the two libraries compute
            // duration from different headers and round differently. What this
            // catches is a container rewrite that dropped the stream, which does
            // not miss by a second.
            && Math.Abs(DurationSeconds - other.DurationSeconds) <= 1.0;
    }

    /// <summary>
    /// Every field this reading has that <paramref name="after"/> lost or changed.
    /// </summary>
    /// <remarks>
    /// One-directional on purpose: a write that <i>adds</i> the AcoustID is the
    /// whole point, so gained fields are expected. A field that vanished or
    /// changed value is not.
    /// </remarks>
    public IReadOnlyList<string> FieldsLostIn(TagSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);

        var lost = new List<string>();

        foreach (var (key, value) in Fields)
        {
            if (!after.Fields.TryGetValue(key, out var now) || !string.Equals(now, value, StringComparison.Ordinal))
            {
                lost.Add(key);
            }
        }

        return lost;
    }

    internal static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
