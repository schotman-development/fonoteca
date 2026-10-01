using System.Collections.Frozen;
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
    /// The date as the file itself stores it, cut to its ISO prefix where it has
    /// one. Only TagLib#'s reading carries it: ATL's is a parse, and a parse is
    /// what loses a day — see <c>TagWriter</c>'s verification.
    /// </summary>
    public string? RecordedDate { get; init; }

    /// <summary>ID3v2's <c>TDOR</c> (2.3's <c>TORY</c>) as stored. TagLib#'s reading only.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>The track and disc totals as TagLib# reads them; null where none. TagLib#'s reading only.</summary>
    /// <remarks>
    /// ATL reads both as zero from a Vorbis comment holding <c>TRACKTOTAL</c>
    /// and <c>TOTALTRACKS</c> together — 69 FLACs here — and a zero is written
    /// back as nothing.
    /// </remarks>
    public int? TrackTotal { get; init; }

    /// <inheritdoc cref="TrackTotal"/>
    public int? DiscTotal { get; init; }

    /// <summary>
    /// Every ID3v2 <c>UFID</c> as <c>owner=identifier</c>, in file order.
    /// TagLib#'s reading only: it is how a write that damaged one is seen —
    /// see <c>TaggingDefaults.UfidIsBinary</c>.
    /// </summary>
    public string? FileIdentifiers { get; init; }

    /// <summary>
    /// Whether this reading and another agree about everything that matters.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>==</c> on the record. Two libraries reading the same
    /// file legitimately disagree about tag-mapping conventions — ADR 0002 says
    /// as much — so equality of the whole <see cref="Fields"/> map across
    /// libraries is not a meaningful test. What must agree is the AcoustID and the
    /// artwork; the audio's length is held to each library's own reading of the
    /// original, in <c>TagWriter</c>.
    /// </remarks>
    public bool AgreesWith(TagSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(AcoustId, other.AcoustId, StringComparison.OrdinalIgnoreCase)
            && PictureCount == other.PictureCount
            && PictureDigests.SequenceEqual(other.PictureDigests, StringComparer.Ordinal);
    }

    /// <summary>
    /// What this reading holds for a field, tolerating the case-folding each
    /// format applies.
    /// </summary>
    /// <remarks>
    /// The snapshot's half of <see cref="TagReader.Lookup"/>, and it exists for
    /// the same two reasons that one does: ATL uppercases Vorbis keys on the way
    /// in, and MP4 strips the <c>----:com.apple.iTunes:</c> prefix it added
    /// itself. A diff that compared keys exactly would decide every FLAC needed
    /// rewriting on every pass, forever.
    /// </remarks>
    public string? Find(string field)
    {
        if (Fields.TryGetValue(field, out var exact)) return exact;

        foreach (var (key, value) in Fields)
        {
            var name = key.AsSpan();
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name[(colon + 1)..];

            if (name.Equals(field, StringComparison.OrdinalIgnoreCase)) return value;
        }

        return null;
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
            // ATL writes every ID3v2 tag as 2.4, renaming a 2.2 frame as it
            // goes: TMT is TMED afterwards, the same field.
            if (!after.Fields.TryGetValue(key, out var now)
                && !(Id3v22Successors.TryGetValue(key, out var successor)
                    && after.Fields.TryGetValue(successor, out now)))
            {
                // An empty value is no value, and ATL does not write an empty atom back.
                if (value.Length > 0) lost.Add(key);
            }

            // ATL ends a URL frame with a NUL of its own whether or not the
            // file did.
            else if (!string.Equals(now.TrimEnd('\0'), value.TrimEnd('\0'), StringComparison.Ordinal))
            {
                lost.Add(key);
            }
        }

        return lost;
    }

    /// <summary>ATL's own ID3v2.2 to 2.4 frame renames (<c>frameMapping_v22_4</c>).</summary>
    private static readonly FrozenDictionary<string, string> Id3v22Successors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BUF"] = "RBUF", ["CNT"] = "PCNT", ["CRA"] = "AENC", ["ETC"] = "ETCO", ["EQU"] = "EQU2",
            ["GEO"] = "GEOB", ["IPL"] = "TIPL", ["LNK"] = "LINK", ["MCI"] = "MCDI", ["MLL"] = "MLLT",
            ["REV"] = "RVRB", ["RVA"] = "RVA2", ["SLT"] = "SYLT", ["STC"] = "SYTC", ["TBP"] = "TBPM",
            ["TDY"] = "TDLY", ["TEN"] = "TENC", ["TFT"] = "TFLT", ["TKE"] = "TKEY", ["TLA"] = "TLAN",
            ["TLE"] = "TLEN", ["TMT"] = "TMED", ["TOF"] = "TOFN", ["TOL"] = "TOLY", ["TP4"] = "TPE4",
            ["TPA"] = "TPOS", ["TRC"] = "TSRC", ["TSS"] = "TSSE", ["TXT"] = "TEXT", ["TXX"] = "TXXX",
            ["UFI"] = "UFID", ["ULT"] = "USLT", ["WAF"] = "WOAF", ["WAR"] = "WOAR", ["WAS"] = "WOAS",
            ["WCM"] = "WCOM", ["WCP"] = "WCOP", ["WPB"] = "WPUB", ["WXX"] = "WXXX",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
