using System.Collections.Frozen;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Tagging;

/// <summary>
/// Where each catalogue fact lives, per container.
/// </summary>
/// <remarks>
/// <see cref="AcoustIdTagField"/>'s problem, one field at a time, with the same
/// answer and the same measurement behind it: there is no single spelling, and
/// getting one wrong is silent — a tag written under the wrong key is a perfectly
/// valid tag that Picard, beets and Lidarr do not look at.
///
/// <b>Two spellings per fact, and the container decides which.</b> Measured
/// against ATL 7.16 with <c>ffprobe</c> and TagLib#'s native MusicBrainz
/// accessors reading back:
///
/// <list type="bullet">
/// <item>FLAC and Ogg take <c>MUSICBRAINZ_TRACKID</c>. Handing them the
/// title-case spelling produces a Vorbis comment called
/// <c>MUSICBRAINZ TRACK ID</c> — ATL uppercases the key and keeps the spaces —
/// which TagLib# then cannot find. Exactly the failure
/// <see cref="AcoustIdTagField"/> was written to avoid.</item>
/// <item>MP3 and M4A take <c>MusicBrainz Track Id</c>, and the uppercase
/// spelling is invisible to TagLib#'s accessors in both.</item>
/// </list>
///
/// <b>The container's style is not decided here.</b> It comes from
/// <see cref="AcoustIdTagField.For(LibraryPath)"/>, which already holds the
/// extension table — including which containers cannot be tagged at all. Two
/// copies of that list would drift, and the symptom would be a DSD file this
/// class agreed to write to.
///
/// <b>Where MP3 falls short, and it is not fixable from here.</b> Picard puts
/// the <i>recording</i> id in an ID3 <c>UFID</c> frame, and TagLib#'s
/// <c>MusicBrainzTrackId</c> reads it from there — so on an MP3 the
/// <c>TXXX:MusicBrainz Track Id</c> written here is readable by most tools and
/// is not where Picard would have put it. ATL exposes no way to write a
/// <c>UFID</c>. Every other id round-trips through TagLib#'s native accessors
/// on every container.
/// ponytail: TXXX only on ID3. Revisit if a player is measured to miss it.
/// </remarks>
public static class CatalogueTagFields
{
    /// <summary>
    /// The fields ATL owns a typed property for, which therefore have no
    /// per-container spelling: the library maps them itself.
    /// </summary>
    /// <remarks>
    /// These are also the names <see cref="TagReader.Describe"/> reports them
    /// under, which is what lets one dictionary serve as both the desired value
    /// and the thing the "before" reading is diffed against.
    /// </remarks>
    private static readonly FrozenSet<string> Standard = new[]
    {
        CatalogueTags.Title,
        CatalogueTags.Artist,
        CatalogueTags.Album,
        CatalogueTags.AlbumArtist,
        CatalogueTags.TrackNumber,
        CatalogueTags.TrackTotal,
        CatalogueTags.DiscNumber,
        CatalogueTags.DiscTotal,
        CatalogueTags.Year,
        CatalogueTags.Genre,
        CatalogueTags.Composer,
        CatalogueTags.Comment,
        CatalogueTags.Isrc,
        CatalogueTags.Lyricist,
        CatalogueTags.Conductor,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The credits ATL has no typed property for, which every container takes
    /// under Picard's name as is: a Vorbis comment, an ID3v2 <c>TXXX</c>, an MP4
    /// freeform atom.
    /// </summary>
    /// <remarks>
    /// ponytail: <c>TXXX</c> on ID3, where Picard writes <c>TIPL</c> and
    /// <c>TMCL</c>. TagLib-based readers — Navidrome among them — read a
    /// <c>TXXX:PRODUCER</c> as <c>PRODUCER</c> all the same; ATL's
    /// <c>InvolvedPeople</c> writes <c>TIPL</c> and is the upgrade if a player is
    /// measured to need it, with a TagLib# reading of that frame to verify it.
    /// </remarks>
    private static readonly FrozenSet<string> Credits = new[]
    {
        CatalogueTags.Arranger,
        CatalogueTags.Producer,
        CatalogueTags.Engineer,
        CatalogueTags.Mixer,
        CatalogueTags.Performer,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Typed fields ATL puts somewhere in an MP4 that neither TagLib# nor ffprobe
    /// looks: measured on ATL 7.16, the ISRC and the conductor. Left out of MP4s
    /// rather than written where nothing reads them.
    /// </summary>
    private static readonly FrozenSet<string> NotInMp4 = new[]
    {
        CatalogueTags.Isrc,
        CatalogueTags.Conductor,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Mp4 = new[] { "m4a", "m4b", "aac", "alac" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsMp4(LibraryPath path) =>
        Mp4.Contains(System.IO.Path.GetExtension(path.Value).TrimStart('.'));

    /// <summary>
    /// A catalogue value as this container takes it.
    /// </summary>
    /// <remarks>
    /// Several values are one field's several values everywhere but MP4, where
    /// ATL 7.16 runs them together with nothing between — "jazzvocal jazz", as
    /// TagLib# and ffprobe both read it back. There they are one value joined
    /// with "; ", which is what players split an MP4's genre on.
    /// </remarks>
    public static string Render(LibraryPath path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return IsMp4(path) ? value.Replace(CatalogueTags.ValueSeparator.ToString(), "; ", StringComparison.Ordinal) : value;
    }

    /// <summary>
    /// Canonical name — which is also the Vorbis and APEv2 spelling — to the
    /// ID3v2 <c>TXXX</c> description and MP4 freeform atom name.
    /// </summary>
    /// <remarks>
    /// Picard's names, so the 227 files in the target library it tagged years ago
    /// read as already correct rather than being rewritten under a second
    /// spelling beside the first.
    /// </remarks>
    private static readonly FrozenDictionary<string, string> TitleCased =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CatalogueTags.RecordingId] = "MusicBrainz Track Id",
            [CatalogueTags.ReleaseId] = "MusicBrainz Album Id",
            [CatalogueTags.ReleaseGroupId] = "MusicBrainz Release Group Id",
            [CatalogueTags.ArtistId] = "MusicBrainz Artist Id",
            [CatalogueTags.AlbumArtistId] = "MusicBrainz Album Artist Id",
            [CatalogueTags.WorkId] = "MusicBrainz Work Id",
            [CatalogueTags.AcoustIdField] = AcoustIdTagField.TitleCase,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Whether ATL has a typed property for this fact.</summary>
    public static bool IsStandard(string canonical) => Standard.Contains(canonical);

    /// <summary>
    /// How this container spells a field a person named, or null when it cannot carry one.
    /// </summary>
    /// <remarks>
    /// As named: a Vorbis comment and an APE item take the name itself, ID3v2 a
    /// <c>TXXX</c> described by it, MP4 a freeform atom called it — which is
    /// what ATL makes of a name it does not know, and what the verifier reads.
    /// </remarks>
    public static string? Custom(LibraryPath path, string name) =>
        AcoustIdTagField.For(path) is null || !PersonTags.IsCustom(name) ? null : name;

    /// <summary>Whether a field, as a container spells it, is a person's own rather than the catalogue's.</summary>
    public static bool IsCustom(string field) =>
        !Standard.Contains(field)
        && !TitleCased.ContainsKey(field)
        && !TitleCased.Values.Contains(field, StringComparer.OrdinalIgnoreCase)
        && PersonTags.IsCustom(field);

    /// <summary>
    /// How this container spells <paramref name="canonical"/>, or null when it
    /// cannot carry the fact at all.
    /// </summary>
    /// <remarks>
    /// Null for two different reasons, and the caller treats them the same way —
    /// leave the field alone. Either the container is one nothing here can tag
    /// (DSD), or the fact is one this class has no spelling for, which is a
    /// programming error the day somebody adds a constant to
    /// <see cref="CatalogueTags"/> and not to the table above. Skipping is the
    /// right answer to both: a write that throws part-way through a library is
    /// worse than one that writes six fields instead of seven.
    /// </remarks>
    public static string? Spell(LibraryPath path, string canonical)
    {
        var style = AcoustIdTagField.For(path);

        if (style is null) return null;

        if (NotInMp4.Contains(canonical) && IsMp4(path)) return null;

        // ATL resolves these itself, and it does it per container — TRACKNUMBER
        // is a Vorbis comment, a TRCK frame and a `trkn` atom, and none of that
        // is ours to spell.
        if (IsStandard(canonical)) return canonical;

        if (Credits.Contains(canonical)) return canonical;

        if (!TitleCased.TryGetValue(canonical, out var titled)) return null;

        return style switch
        {
            AcoustIdTagField.Uppercase => canonical,
            AcoustIdTagField.TitleCase => titled,

            // ASF namespaces a custom field with a slash and then spells the rest
            // as usual: "Acoustid/Id", "MusicBrainz/Album Artist Id". So it is
            // the *first* space that becomes a slash, not every space — derived
            // rather than tabled, because a third column of the same seven names
            // is a third thing to keep in step. Untested: there is no WMA in the
            // corpus and none in the target library.
            _ => Namespaced(titled),
        };
    }

    /// <summary>"MusicBrainz Track Id" -> "MusicBrainz/Track Id".</summary>
    private static string Namespaced(string titled)
    {
        var space = titled.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? titled : string.Concat(titled.AsSpan(0, space), "/", titled.AsSpan(space + 1));
    }
}
