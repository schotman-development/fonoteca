using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;

namespace Fonoteca.Providers.MusicBrainz;

/// <summary>
/// MusicBrainz's web service entities, flattened to what the catalogue needs.
/// </summary>
/// <remarks>
/// The whole reason <c>MetaBrainz.MusicBrainz</c> does not appear anywhere else
/// in this solution. Its interfaces are a faithful model of WS/2 — forty-odd
/// entity types, every relationship in both directions — and letting them past
/// this file would mean the catalogue's shape was decided by an HTTP API's
/// serialisation format.
///
/// Everything here is a pure function of its input, so it is testable against a
/// recorded response with no network at all, which is what
/// <c>MusicBrainzCatalogueTests</c> does.
/// </remarks>
internal static class MusicBrainzMapper
{
    public static MusicBrainzRecording ToRecording(IRecording source)
    {
        // MusicBrainz allows a recording to be a performance of more than one
        // work — a medley, a segue. The first is taken and the rest dropped,
        // because the catalogue's Recording.WorkId is a single link and inventing
        // a second one here would be modelling the exception before anything
        // asks about it. Visible in the title when it happens.
        var performance = FirstWorkLink(source.Relationships);

        return new MusicBrainzRecording(
            Id: new Mbid(source.Id),
            Title: source.Title ?? string.Empty,
            Disambiguation: NullIfBlank(source.Disambiguation),
            Length: source.Length,
            Credits: ToCredits(source.ArtistCredit),
            Isrcs: source.Isrcs ?? [],
            Appearances: ToAppearances(source),
            Relations: ToRelations(source.Relationships),
            WorkId: performance is null ? null : new Mbid(performance.Id),
            WorkTitle: NullIfBlank(performance?.Title));
    }

    public static MusicBrainzWork ToWork(IWork source) =>
        new(
            Id: new Mbid(source.Id),
            Title: source.Title ?? string.Empty,
            Type: NullIfBlank(source.Type),
            Relations: ToRelations(source.Relationships));

    public static MusicBrainzRelease ToRelease(IRelease source)
    {
        var tracks = new List<MusicBrainzTrack>();

        foreach (var medium in source.Media ?? [])
        {
            foreach (var track in medium.Tracks ?? [])
            {
                tracks.Add(new MusicBrainzTrack(
                    DiscNumber: medium.Position,
                    // Position is nullable on the wire and never absent in
                    // practice; 0 marks the one case where it was, rather than
                    // silently renumbering the disc.
                    Position: track.Position ?? 0,
                    Number: NullIfBlank(track.Number),
                    Title: track.Title ?? string.Empty,
                    Length: track.Length,
                    RecordingId: track.Recording is { } recording ? new Mbid(recording.Id) : null,
                    Credits: ToCredits(track.ArtistCredit)));
            }
        }

        tracks.Sort(static (left, right) => left.DiscNumber != right.DiscNumber
            ? left.DiscNumber.CompareTo(right.DiscNumber)
            : left.Position.CompareTo(right.Position));

        var group = source.ReleaseGroup;

        return new MusicBrainzRelease(
            Id: new Mbid(source.Id),
            Title: source.Title ?? string.Empty,
            ReleasedOn: ToReleaseDate(source.Date),
            Country: NullIfBlank(source.Country),
            Status: NullIfBlank(source.Status),
            Barcode: NullIfBlank(source.Barcode),
            Labels: ToLabels(source.LabelInfo),
            ReleaseGroupId: group is null ? null : new Mbid(group.Id),
            ReleaseGroupTitle: NullIfBlank(group?.Title),
            PrimaryType: NullIfBlank(group?.PrimaryType),
            SecondaryTypes: group?.SecondaryTypes ?? [],
            Credits: ToCredits(source.ArtistCredit),
            Tracks: tracks);
    }

    /// <summary>A browse result: a release, counted but not listed.</summary>
    /// <remarks>
    /// The media are mapped for their <c>TrackCount</c> alone — a browse carries
    /// no tracks, and asking for them is what makes the browse start dropping
    /// releases (see <c>MusicBrainzCatalogue.BrowseIncludes</c>).
    /// </remarks>
    public static MusicBrainzReleaseCandidate ToReleaseCandidate(IRelease source)
    {
        var media = new List<MusicBrainzMediumSummary>(source.Media?.Count ?? 0);

        foreach (var medium in source.Media ?? [])
        {
            media.Add(new MusicBrainzMediumSummary(
                Position: medium.Position,
                Format: NullIfBlank(medium.Format),
                TrackCount: medium.TrackCount));
        }

        var group = source.ReleaseGroup;

        return new MusicBrainzReleaseCandidate(
            Id: new Mbid(source.Id),
            Title: source.Title ?? string.Empty,
            ReleasedOn: ToReleaseDate(source.Date),
            Country: NullIfBlank(source.Country),
            Status: NullIfBlank(source.Status),
            Barcode: NullIfBlank(source.Barcode),
            ReleaseGroupId: group is null ? null : new Mbid(group.Id),
            ReleaseGroupTitle: NullIfBlank(group?.Title),
            PrimaryType: NullIfBlank(group?.PrimaryType),
            SecondaryTypes: group?.SecondaryTypes ?? [],
            Media: media);
    }

    /// <summary>Every release the recording appears on, and where on it.</summary>
    /// <remarks>
    /// A recording lookup that includes media gets back only the medium and the
    /// track that hold this recording, not the release's whole contents — which
    /// is why <see cref="IMusicBrainzCatalogue.GetReleaseAsync"/> exists as a
    /// separate call for when the whole track list is the question.
    ///
    /// The first medium is taken rather than searched for, because the tracks
    /// in this response carry no recording reference to search <i>by</i>. The
    /// case it gets wrong is a recording appearing twice on one release — a
    /// hidden reprise, a bonus-disc repeat — where the second appearance is
    /// dropped. Rare, visible in the track number when it happens, and not
    /// worth a second round trip per release to fix.
    /// </remarks>
    private static List<MusicBrainzAppearance> ToAppearances(IRecording source)
    {
        var releases = source.Releases;
        if (releases is null || releases.Count == 0) return [];

        var appearances = new List<MusicBrainzAppearance>(releases.Count);

        foreach (var release in releases)
        {
            var medium = release.Media?.Count > 0 ? release.Media[0] : null;
            var track = medium?.Tracks?.Count > 0 ? medium.Tracks[0] : null;
            var group = release.ReleaseGroup;

            appearances.Add(new MusicBrainzAppearance(
                ReleaseId: new Mbid(release.Id),
                ReleaseTitle: release.Title ?? string.Empty,
                ReleasedOn: ToReleaseDate(release.Date),
                Country: NullIfBlank(release.Country),
                Status: NullIfBlank(release.Status),
                ReleaseGroupId: group is null ? null : new Mbid(group.Id),
                ReleaseGroupTitle: NullIfBlank(group?.Title),
                PrimaryType: NullIfBlank(group?.PrimaryType),
                DiscNumber: medium?.Position,
                TrackPosition: track?.Position,
                TrackNumber: NullIfBlank(track?.Number),
                TrackCount: medium?.TrackCount));
        }

        return appearances;
    }

    /// <summary>The work this recording is a performance of, if MusicBrainz links one.</summary>
    private static IWork? FirstWorkLink(IReadOnlyList<IRelationship>? relationships)
    {
        foreach (var relationship in relationships ?? [])
        {
            if (relationship.Work is { } work) return work;
        }

        return null;
    }

    /// <summary>
    /// Every relationship that points at an artist, flattened to that artist.
    /// </summary>
    /// <remarks>
    /// Relationships to works, releases, places and URLs are dropped here rather
    /// than filtered later: this file exists so that MusicBrainz's shape stops at
    /// its boundary, and a relation with no artist has nothing the catalogue can
    /// store. The work link is read separately by <see cref="FirstWorkLink"/>.
    ///
    /// Only the first attribute is kept. MusicBrainz allows several — "guitar"
    /// and "guest" on one performer link — and the catalogue's
    /// <c>Relationship.Attribute</c> is a single column; the rest are recoverable
    /// from MusicBrainz and nothing reads them yet.
    /// </remarks>
    private static List<MusicBrainzRelation> ToRelations(IReadOnlyList<IRelationship>? relationships)
    {
        if (relationships is null || relationships.Count == 0) return [];

        var mapped = new List<MusicBrainzRelation>(relationships.Count);

        foreach (var relationship in relationships)
        {
            if (relationship.Artist is not { } artist) continue;

            mapped.Add(new MusicBrainzRelation(
                Type: relationship.Type ?? string.Empty,
                Attribute: relationship.Attributes is { Count: > 0 } attributes
                    ? NullIfBlank(attributes[0])
                    : null,
                ArtistId: new Mbid(artist.Id),
                // The target credit is how this relationship printed the name —
                // an orchestra billed under a former name on a 1962 pressing.
                // Absent far more often than an artist credit's, so the artist's
                // own name is the ordinary answer rather than the fallback.
                Name: NullIfBlank(relationship.TargetCredit) ?? artist.Name ?? string.Empty,
                SortName: NullIfBlank(artist.SortName),
                ArtistType: NullIfBlank(artist.Type),
                Disambiguation: NullIfBlank(artist.Disambiguation)));
        }

        return mapped;
    }

    private static List<MusicBrainzCredit> ToCredits(IReadOnlyList<INameCredit>? credits)
    {
        if (credits is null || credits.Count == 0) return [];

        var mapped = new List<MusicBrainzCredit>(credits.Count);

        foreach (var credit in credits)
        {
            var artist = credit.Artist;

            mapped.Add(new MusicBrainzCredit(
                ArtistId: artist is null ? null : new Mbid(artist.Id),
                // The credit's own name is what is printed on this release; the
                // artist's is their canonical one. Preferring the former is what
                // keeps "Bowie" from becoming "David Bowie" on a sleeve that
                // never said so.
                Name: NullIfBlank(credit.Name) ?? artist?.Name ?? string.Empty,
                SortName: NullIfBlank(artist?.SortName),
                JoinPhrase: NullIfBlank(credit.JoinPhrase),
                Disambiguation: NullIfBlank(artist?.Disambiguation),
                ArtistType: NullIfBlank(artist?.Type)));
        }

        return mapped;
    }

    private static List<MusicBrainzLabel> ToLabels(IReadOnlyList<ILabelInfo>? labels)
    {
        if (labels is null || labels.Count == 0) return [];

        var mapped = new List<MusicBrainzLabel>(labels.Count);

        foreach (var info in labels)
        {
            mapped.Add(new MusicBrainzLabel(
                Id: info.Label is { } label ? new Mbid(label.Id) : null,
                Name: NullIfBlank(info.Label?.Name),
                CatalogNumber: NullIfBlank(info.CatalogNumber)));
        }

        return mapped;
    }

    /// <summary>
    /// A MusicBrainz partial date, kept partial.
    /// </summary>
    /// <remarks>
    /// <c>MetaBrainz.MusicBrainz.PartialDate</c> offers <c>NearestDate</c>,
    /// which fills the unknown parts in with January the 1st. Using it would
    /// turn "released in 1969" into "released on 1 January 1969" — a claim
    /// nobody made, indistinguishable afterwards from one that was, and exactly
    /// the value that makes a reissue outrank an original when editions are
    /// sorted by date.
    /// </remarks>
    private static ReleaseDate? ToReleaseDate(PartialDate? date) =>
        date is { IsEmpty: false, Year: { } year } ? new ReleaseDate(year, date.Month, date.Day) : null;

    /// <summary>
    /// MusicBrainz sends <c>""</c> for absent text, not null. Both mean the same
    /// thing and only one of them survives a <c>?.</c> chain, so they are made
    /// the same thing here.
    /// </summary>
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
