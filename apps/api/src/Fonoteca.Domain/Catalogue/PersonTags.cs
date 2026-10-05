using System.Globalization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The tags a person may set on a file, and what makes a value one a file can carry.
/// </summary>
/// <remarks>
/// <b>Rule 4.</b> A person's tags are kept beside the catalogue's answer
/// (<c>MediaFile.TagEditsJson</c>, read with <see cref="PersonEdits"/>), not
/// over it, and the tag write lays them over what the catalogue says: they win.
/// A value of null is an answer too — "this file carries none" — and is how a
/// field is taken out of a file.
///
/// <b>The album's three, where there is an album.</b> The album title and its
/// artist are corrections to the album when the folder is one, kept on the
/// album like the album page's own (<c>ReleaseGroup.EditsJson</c>), so every
/// page and every file of it agrees. Its year is set for the whole folder and
/// kept on each of its files, so it stays with them whatever pressing they are
/// filed under later. A folder the catalogue has no album for carries all
/// three per file.
///
/// Nothing that names an identity is here — no MusicBrainz id, no AcoustID:
/// those are answered on the Identify screen, where the evidence is.
/// </remarks>
public static class PersonTags
{
    /// <summary>Every field the editor sets, in the order it shows them.</summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        CatalogueTags.Title,
        CatalogueTags.Artist,
        CatalogueTags.Album,
        CatalogueTags.AlbumArtist,
        CatalogueTags.Year,
        CatalogueTags.TrackNumber,
        CatalogueTags.TrackTotal,
        CatalogueTags.DiscNumber,
        CatalogueTags.DiscTotal,
        CatalogueTags.Genre,
        CatalogueTags.Composer,
        CatalogueTags.Comment,
    ];

    /// <summary>The album's own, corrected on the album where the folder is one.</summary>
    public static IReadOnlyList<string> AlbumFields { get; } =
    [
        CatalogueTags.Album,
        CatalogueTags.AlbumArtist,
        CatalogueTags.Year,
    ];

    /// <summary>The longest value a person may give one field.</summary>
    public const int MaxLength = 2000;

    /// <summary>The longest name a person may give a field of their own.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Whether a name is one a person may give a field of their own — any, so
    /// long as every container can carry it under that name and nothing reads
    /// it as something else.
    /// </summary>
    /// <remarks>
    /// Letters, digits, spaces and <c>_ - . ( )</c>, from a letter: a Vorbis key
    /// cannot hold <c>=</c>, and a name ATL splits on <c>:</c> would land under
    /// another. Not one of the editor's own fields, not an identity — no
    /// MusicBrainz id, no AcoustID — and not shaped like an ID3 frame id
    /// (<c>TKEY</c>, <c>TT2</c>) or an MP4 atom: ATL writes any four-character
    /// name as that frame or atom rather than as a field of its own. The write
    /// is verified by a second library either way; this keeps a doomed save from
    /// being offered.
    /// </remarks>
    public static string? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length is 0 or > MaxNameLength) return $"A field's name is 1 to {MaxNameLength} characters.";

        if (!char.IsAsciiLetter(name[0]) || name[^1] == ' '
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '(' or ')'))
        {
            return $"'{name}' is not a field name: letters, digits, spaces and _ - . ( ), from a letter.";
        }

        if (Fields.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"'{name}' is one of the editor's own fields.";

        var upper = name.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

        if (upper.StartsWith("MUSICBRAINZ", StringComparison.Ordinal) || upper.StartsWith("ACOUSTID", StringComparison.Ordinal) || upper == "UFID")
        {
            return $"'{name}' names an identity, which the Identify screen answers.";
        }

        if (name.Length == 4 || (name.Length == 3 && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))))
        {
            return $"'{name}' is shaped like an ID3 frame or an MP4 atom, and would be written as one.";
        }

        if (TagLibraryOwn.Contains(name) || name.StartsWith("info.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("bext.", StringComparison.OrdinalIgnoreCase))
        {
            return $"'{name}' is a field the tag library writes as one of its own in some files.";
        }

        return null;
    }

    /// <summary>
    /// The names ATL 7.16 maps to a field of its own in a container the tag write
    /// supports — Vorbis comments, APEv2, MP4 and ID3 — read off its frame maps,
    /// and the name it reports a Vorbis vendor string under.
    /// It upper-cases a Vorbis key, so a "Publisher" written to a FLAC comes back
    /// as its publisher, and the write is refused. Shorter ones are refused by
    /// shape already. Kept in step with <c>TAG_LIBRARY_OWN</c> in the web's
    /// <c>tagEdits.ts</c>.
    /// </summary>
    private static readonly HashSet<string> TagLibraryOwn = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALBUM ARTIST", "ALBUMARTISTSORT", "ARTISTSORT", "BPM", "CATALOGNUMBER", "CONDUCTOR", "COPYRIGHT",
        "DESCRIPTION", "ENCODED-BY", "ENCODEDBY", "ENCODER", "LABELNO", "LANGUAGE", "LYRICIST", "LYRICS",
        "ORIGINALDATE", "PREFERENCE", "PRODUCTNUMBER", "PUBLISHER", "RATING", "TOTALDISCS", "TOTALTRACKS", "TRACK",
        "VORBIS-VENDOR",
    };

    /// <summary>Whether a field is a person's own rather than one of the editor's.</summary>
    public static bool IsCustom(string field) =>
        !Fields.Contains(field, StringComparer.Ordinal) && NameProblem(field) is null;

    /// <summary>What is wrong with giving <paramref name="field"/> this value, or null when nothing is.</summary>
    /// <param name="value">Null means "this file carries none", which is always a value.</param>
    public static string? Problem(string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (!Fields.Contains(field, StringComparer.Ordinal) && NameProblem(field) is { } name) return name;

        if (value is null) return null;

        if (string.IsNullOrWhiteSpace(value)) return $"{field} is blank; to take it out of the file, say it has none.";

        if (value.Length > MaxLength) return $"{field} is longer than {MaxLength} characters.";

        // A NUL is refused by the journal's jsonb, and U+001F is ATL's separator
        // between the values of one field. Line breaks only in a comment.
        if (value.Any(c => c == '\0' || c == '\u001F' || (char.IsControl(c) && !(field == CatalogueTags.Comment && c is '\n' or '\r' or '\t'))))
        {
            return $"{field} holds a control character.";
        }

        if (Number(field) is { } most
            && !(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= most))
        {
            return $"{field} is a whole number from 1 to {most}.";
        }

        return null;
    }

    /// <summary>The largest a numeric field may be, or null when the field is text.</summary>
    private static int? Number(string field) => field switch
    {
        CatalogueTags.Year => 9999,
        CatalogueTags.TrackNumber or CatalogueTags.TrackTotal or CatalogueTags.DiscNumber or CatalogueTags.DiscTotal => 999,
        _ => null,
    };
}
