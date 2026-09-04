namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What the catalogue knows about one file, in the vocabulary a tag uses.
/// </summary>
/// <remarks>
/// Flat and primitive on purpose: this crosses from the entity graph into
/// <c>Fonoteca.Tagging</c>, and handing a <see cref="MediaFile"/> across that
/// seam would put EF's navigation properties — and therefore the database — on
/// the far side of it. Everything here is read off the graph by the caller.
///
/// Every member is optional because every one of them genuinely can be missing:
/// a recording with no work, an album MusicBrainz gives no year, a release the
/// attribution pass refused. Absent means "say nothing", never "write a blank".
/// </remarks>
public sealed record CatalogueTagSource
{
    /// <summary>The title printed on the release's track list, which is not always the recording's.</summary>
    public string? TrackTitle { get; init; }

    public string? RecordingTitle { get; init; }

    public Mbid? RecordingMbid { get; init; }

    public int? TrackNumber { get; init; }

    public int? TrackTotal { get; init; }

    public int? DiscNumber { get; init; }

    public int? DiscTotal { get; init; }

    public string? AlbumTitle { get; init; }

    public Mbid? ReleaseMbid { get; init; }

    public Mbid? ReleaseGroupMbid { get; init; }

    /// <summary>
    /// The release year, and only the year.
    /// </summary>
    /// <remarks>
    /// <b>Partial dates stay partial.</b> The catalogue keeps
    /// <c>ReleasedYear</c>, <c>ReleasedMonth</c> and <c>ReleasedDay</c> apart
    /// precisely because MusicBrainz publishes "1969" as often as it publishes
    /// a day, and ATL's own date field is a <c>DateTime</c> — it cannot hold
    /// "1969" without inventing the first of January. Writing the year alone is
    /// the claim we can actually make.
    /// </remarks>
    public int? Year { get; init; }

    /// <summary>The recording's billing line, joined phrases and all.</summary>
    public string? ArtistCredit { get; init; }

    public IReadOnlyList<Mbid> ArtistMbids { get; init; } = [];

    /// <summary>The release's billing line, which is the album artist.</summary>
    public string? AlbumArtistCredit { get; init; }

    public IReadOnlyList<Mbid> AlbumArtistMbids { get; init; } = [];

    public Mbid? WorkMbid { get; init; }

    public AcoustId? AcoustId { get; init; }
}

/// <summary>
/// The rule: which tags a file's catalogue row implies, under Picard's names.
/// </summary>
/// <remarks>
/// Pure — a record in, a dictionary out — so what this application would write
/// into somebody's music can be asserted without a file, a database or a
/// provider. The keys are the <b>canonical</b> names, which are also the Vorbis
/// and APEv2 spellings; <c>CatalogueTagFields</c> turns them into whatever the
/// container in hand actually uses.
///
/// Two rules run through all of it:
///
/// <b>Nothing empty is ever offered.</b> A missing fact produces no entry, so a
/// file whose album the catalogue has not decided keeps whatever its ripper
/// wrote there. The alternative — writing a blank — is a deletion wearing an
/// edit's clothes, and it would be applied to a library at a time.
///
/// <b>The track title outranks the recording title.</b> They differ more often
/// than they look like they should: MusicBrainz prints "Song (feat. Someone)"
/// on the release and keeps the recording plain, and the release's spelling is
/// the one a person expects to see on the album they are looking at.
/// </remarks>
public static class CatalogueTags
{
    public const string Title = "TITLE";
    public const string Artist = "ARTIST";
    public const string Album = "ALBUM";
    public const string AlbumArtist = "ALBUMARTIST";
    public const string TrackNumber = "TRACKNUMBER";
    public const string TrackTotal = "TRACKTOTAL";
    public const string DiscNumber = "DISCNUMBER";
    public const string DiscTotal = "DISCTOTAL";
    public const string Year = "YEAR";

    public const string RecordingId = "MUSICBRAINZ_TRACKID";
    public const string ReleaseId = "MUSICBRAINZ_ALBUMID";
    public const string ReleaseGroupId = "MUSICBRAINZ_RELEASEGROUPID";
    public const string ArtistId = "MUSICBRAINZ_ARTISTID";
    public const string AlbumArtistId = "MUSICBRAINZ_ALBUMARTISTID";
    public const string WorkId = "MUSICBRAINZ_WORKID";
    public const string AcoustIdField = "ACOUSTID_ID";

    /// <summary>
    /// How several artist ids share one field.
    /// </summary>
    /// <remarks>
    /// Picard writes repeated tags where the container allows them; ATL's
    /// <c>AdditionalFields</c> is one string per name and cannot express that.
    /// A slash is the separator ID3v2.3 already uses for the same list, and it
    /// is what beets and Lidarr both split on.
    ///
    /// ponytail: single field, slash-joined. Only worth revisiting if something
    /// downstream is measured to mis-split a collaboration.
    /// </remarks>
    public const string ArtistIdSeparator = "/";

    /// <summary>The tags this file should carry, by canonical name.</summary>
    public static IReadOnlyDictionary<string, string> For(CatalogueTagSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        Add(tags, Title, source.TrackTitle ?? source.RecordingTitle);
        Add(tags, Artist, source.ArtistCredit);
        Add(tags, Album, source.AlbumTitle);
        Add(tags, AlbumArtist, source.AlbumArtistCredit);

        Add(tags, TrackNumber, Number(source.TrackNumber));
        Add(tags, TrackTotal, Number(source.TrackTotal));
        Add(tags, DiscNumber, Number(source.DiscNumber));
        Add(tags, DiscTotal, Number(source.DiscTotal));
        Add(tags, Year, Number(source.Year));

        Add(tags, RecordingId, source.RecordingMbid?.ToString());
        Add(tags, ReleaseId, source.ReleaseMbid?.ToString());
        Add(tags, ReleaseGroupId, source.ReleaseGroupMbid?.ToString());
        Add(tags, WorkId, source.WorkMbid?.ToString());
        Add(tags, AcoustIdField, source.AcoustId?.ToString());

        Add(tags, ArtistId, Join(source.ArtistMbids));
        Add(tags, AlbumArtistId, Join(source.AlbumArtistMbids));

        return tags;
    }

    private static void Add(Dictionary<string, string> tags, string name, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed)) tags[name] = trimmed;
    }

    /// <summary>
    /// Zero and below are not track numbers, and neither is a null.
    /// </summary>
    /// <remarks>
    /// <c>Release.TrackCount</c> and <c>DiscCount</c> are nullable and are zero
    /// on a release MusicBrainz lists no media for. "Track 3 of 0" is worse than
    /// no total at all — a player reads it as a broken album rather than as an
    /// unknown one.
    /// </remarks>
    private static string? Number(int? value) =>
        value is > 0 ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string? Join(IReadOnlyList<Mbid> ids) =>
        ids.Count == 0 ? null : string.Join(ArtistIdSeparator, ids);
}
