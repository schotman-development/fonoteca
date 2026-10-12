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

    /// <summary>The genres the file carries — see <see cref="FileGenres"/>.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Every ISRC MusicBrainz lists for the recording; which one this file's audio is, nobody knows.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>The credits, as <see cref="CreditTags"/> reads them off MusicBrainz's links.</summary>
    public CreditTags Credits { get; init; } = CreditTags.None;

    /// <summary>ReplayGain 2.0 track gain, dB, from the analysis pass's loudness.</summary>
    public double? TrackGain { get; init; }

    /// <summary>The track's sample peak, linear.</summary>
    public double? TrackPeak { get; init; }

    /// <summary>The album folder's gain, dB — only where every file in it is analysed.</summary>
    public double? AlbumGain { get; init; }

    /// <summary>The album folder's highest sample peak, linear.</summary>
    public double? AlbumPeak { get; init; }

    /// <summary>The track's DR figure, from the analysis pass.</summary>
    public int? DynamicRange { get; init; }

    /// <summary>The album folder's DR figure — only where every file in it has one.</summary>
    public int? AlbumDynamicRange { get; init; }
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

    // A person sets these too, and wins (PersonTags); the comment is only theirs.
    public const string Genre = "GENRE";
    public const string Composer = "COMPOSER";
    public const string Comment = "COMMENT";

    // The credits, under Picard's names.
    public const string Isrc = "ISRC";
    public const string Lyricist = "LYRICIST";
    public const string Conductor = "CONDUCTOR";
    public const string Arranger = "ARRANGER";
    public const string Producer = "PRODUCER";
    public const string Engineer = "ENGINEER";
    public const string Mixer = "MIXER";
    public const string Performer = "PERFORMER";

    // ReplayGain 2.0, under the names every player reads.
    public const string TrackGain = "REPLAYGAIN_TRACK_GAIN";
    public const string TrackPeak = "REPLAYGAIN_TRACK_PEAK";
    public const string AlbumGain = "REPLAYGAIN_ALBUM_GAIN";
    public const string AlbumPeak = "REPLAYGAIN_ALBUM_PEAK";

    // The DR figures, as a whole number, under foobar2000's DR Meter's names:
    // no standard names any, and these are the ones already in files here.
    public const string DynamicRange = "DYNAMIC RANGE";
    public const string AlbumDynamicRange = "ALBUM DYNAMIC RANGE";

    /// <summary>
    /// Between the values of a field that holds several — two genres, three producers.
    /// </summary>
    /// <remarks>
    /// ATL's own separator as this application configures it, so a joined value
    /// is written as separate values: several Vorbis comments, a null-separated
    /// ID3v2.4 frame. A person's value may not contain it (<see cref="PersonTags"/>).
    /// </remarks>
    public const char ValueSeparator = '\u001F';

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

        Add(tags, Genre, Values(source.Genres));
        Add(tags, Isrc, Values(source.Isrcs));
        Add(tags, Composer, Values(source.Credits.Composers));
        Add(tags, Lyricist, Values(source.Credits.Lyricists));
        Add(tags, Conductor, Values(source.Credits.Conductors));
        Add(tags, Arranger, Values(source.Credits.Arrangers));
        Add(tags, Producer, Values(source.Credits.Producers));
        Add(tags, Engineer, Values(source.Credits.Engineers));
        Add(tags, Mixer, Values(source.Credits.Mixers));
        Add(tags, Performer, Values(source.Credits.Performers));

        Add(tags, TrackGain, source.TrackGain is { } trackGain ? AudioAnalysis.Gain(trackGain) : null);
        Add(tags, TrackPeak, source.TrackPeak is { } trackPeak ? AudioAnalysis.Peak(trackPeak) : null);
        Add(tags, AlbumGain, source.AlbumGain is { } albumGain ? AudioAnalysis.Gain(albumGain) : null);
        Add(tags, AlbumPeak, source.AlbumPeak is { } albumPeak ? AudioAnalysis.Peak(albumPeak) : null);
        Add(tags, DynamicRange, Number(source.DynamicRange));
        Add(tags, AlbumDynamicRange, Number(source.AlbumDynamicRange));

        return tags;
    }

    /// <summary>Several values as one field holds them, each once, in order; null for none.</summary>
    private static string? Values(IReadOnlyList<string> values)
    {
        var kept = values
            .Select(value => value.Replace(ValueSeparator, ' ').Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return kept.Count == 0 ? null : string.Join(ValueSeparator, kept);
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
