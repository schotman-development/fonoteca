using System.Collections.Frozen;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Tagging;

/// <summary>
/// Where an AcoustID lives, per container.
/// </summary>
/// <remarks>
/// There is no single spelling, and getting it wrong is silent. A tag written
/// under the wrong key is still a valid tag — every library will happily store
/// and show it — it is simply not the one Picard, beets or Lidarr look for. The
/// file appears tagged and is not.
///
/// These are Picard's names, which is why the 227 already-tagged FLACs in the
/// target library read <c>ACOUSTID_ID</c>, and why the casing differs by
/// container rather than being one constant.
///
/// <b>The values here were measured against ATL 7.16.0, not assumed</b>, and two
/// of them are not what the documentation would lead you to write:
///
/// - Vorbis comments are <b>uppercased by ATL</b> on the way in. Handing it
///   <c>Acoustid Id</c> for a FLAC produces a comment called <c>ACOUSTID ID</c>
///   — plausible, wrong, and invisible without reading the file back.
/// - MP4 takes the <b>bare</b> name. ATL adds the
///   <c>----:com.apple.iTunes:</c> prefix itself, and reads the field back under
///   the short name. Writing the full atom path also works, but then what goes
///   in never equals what comes out, and the verification step compares them.
///
/// <see cref="AcoustIdTagWriterTests"/> is the authority: it writes with ATL and
/// reads back with TagLib#'s native accessors, so a future version of either
/// library changing its mind fails a test rather than a user's library.
/// </remarks>
public static class AcoustIdTagField
{
    /// <summary>Vorbis comments (FLAC, Ogg, Opus) and APEv2.</summary>
    public const string Uppercase = "ACOUSTID_ID";

    /// <summary>ID3v2 <c>TXXX</c> description, and the MP4 freeform atom name.</summary>
    public const string TitleCase = "Acoustid Id";

    /// <summary>ASF/WMA, which spells its custom fields with a slash.</summary>
    public const string Windows = "Acoustid/Id";

    /// <summary>
    /// Extension to field name. Keyed by extension rather than by ATL's format
    /// id, because the extension is the domain's own vocabulary — the same one
    /// <c>AudioFormats</c> already decides audio with — and ATL's numeric format
    /// ids are an internal registry with no stable public meaning.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Vorbis comments.
            ["flac"] = Uppercase,
            ["ogg"] = Uppercase,
            ["oga"] = Uppercase,
            ["opus"] = Uppercase,

            // APEv2.
            ["ape"] = Uppercase,
            ["wv"] = Uppercase,
            ["mpc"] = Uppercase,
            ["tta"] = Uppercase,

            // ID3v2, including the two containers that carry it unexpectedly.
            ["mp3"] = TitleCase,
            ["aiff"] = TitleCase,
            ["aif"] = TitleCase,
            ["wav"] = TitleCase,

            // MP4 freeform atoms. ATL supplies "----:com.apple.iTunes:" itself.
            ["m4a"] = TitleCase,
            ["m4b"] = TitleCase,
            ["aac"] = TitleCase,
            ["alac"] = TitleCase,

            ["wma"] = Windows,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The field name for this file, or null when the container cannot carry one.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw or a guess. DSD files (<c>.dsf</c>, <c>.dff</c>)
    /// are in the catalogue and are not reliably taggable by any library; a pass
    /// over the whole collection must report those as "not supported here" and
    /// keep going, which is a different outcome from a failure.
    /// </remarks>
    public static string? For(LibraryPath path) => For(path.Value);

    /// <inheritdoc cref="For(LibraryPath)"/>
    public static string? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return null;

        return ByExtension.GetValueOrDefault(path[(dot + 1)..]);
    }

    /// <summary>Whether this file's container can carry an AcoustID at all.</summary>
    public static bool IsSupported(LibraryPath path) => For(path) is not null;
}
