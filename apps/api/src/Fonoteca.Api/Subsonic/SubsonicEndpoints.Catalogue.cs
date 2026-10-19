using System.Globalization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// The catalogue, read into the protocol's three shapes.
/// </summary>
/// <remarks>
/// Projections, not rules. Everything that decides something — the billing
/// line, a person's corrections, which media type a file is served as — is
/// called out to the code that already owns it; what is here is the joins and
/// the arithmetic between a row and an attribute.
///
/// <b>Whole lists are loaded and paged in memory, deliberately.</b>
/// <c>getArtists</c> has no paging in the protocol at all, and an album's
/// billing line is a nested collection that cannot be ordered in SQL without
/// costing more than the rows do. This is a library of hundreds of albums, not
/// a streaming service's catalogue; the day that stops being true, the shape to
/// change is this one.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>
    /// The artists the album list is billed to.
    /// </summary>
    /// <remarks>
    /// Takes the albums rather than fetching them, because every caller already
    /// has them: an artist here is defined as somebody an album is billed to, so
    /// deriving one from the other twice would be the same expensive query run
    /// for the same answer.
    /// </remarks>
    private static async Task<IReadOnlyList<SubsonicArtist>> ArtistsOfAsync(
        FonotecaDbContext db,
        IReadOnlyList<Album> albums,
        CancellationToken cancellationToken)
    {
        var billed = albums
            .Where(album => album.ArtistId is not null)
            .Select(album => album.ArtistId!.Value)
            .Distinct()
            .ToList();

        if (billed.Count == 0) return [];

        var described = await db.Artists
            .AsNoTracking()
            .Where(artist => billed.Contains(artist.Id))
            .Select(artist => new
            {
                artist.Id,
                artist.Name,
                artist.LatinName,
                artist.SortName,
                artist.Mbid,
                artist.PortraitUrl,
                artist.EditsJson,
                Uploaded = db.ArtistImages.Any(image =>
                    image.ArtistId == artist.Id && image.Kind == ArtistImageKind.Portrait.Name),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = described.ToDictionary(artist => artist.Id);

        return albums
            .Where(album => album.ArtistId is not null)
            .GroupBy(album => album.ArtistId!.Value)
            .Where(group => byId.ContainsKey(group.Key))
            .Select(group =>
            {
                var artist = byId[group.Key];

                return new SubsonicArtist(
                    artist.Id,
                    artist.LatinName ?? artist.Name,
                    artist.SortName,
                    artist.Mbid,
                    artist.PortraitUrl,
                    group.Count(),
                    Pictured(artist.Uploaded, artist.EditsJson, artist.PortraitUrl));
            })
            .OrderBy(artist => artist.SortName ?? artist.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artist => artist.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether an upload or a provider's picture stands for an artist: what getCoverArt serves for their id.</summary>
    private static bool Pictured(bool uploaded, string? edits, string? portrait) =>
        uploaded || PersonEdits.Apply(PersonEdits.Read(edits), ArtistImageKind.Portrait.Name, portrait) is { Length: > 0 };

    /// <summary>
    /// Every album the library holds files for.
    /// </summary>
    /// <remarks>
    /// <b>An album is a release group</b>, whether or not the pressing its files
    /// are is known, so an album known only as an album is still an album a
    /// client can browse and play. Its name and billing line come from the
    /// edition that stands for it, as on the album page.
    ///
    /// <b>Files, not tracks.</b> An album the catalogue knows about but owns
    /// nothing of is a gap on the Acquire screen; offering it to a client that
    /// would try to play it is the wrong answer to a different question.
    /// </remarks>
    private static async Task<IReadOnlyList<Album>> AlbumsAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken,
        ReleaseGroupId? group = null)
    {
        var query = db.ReleaseGroups.AsNoTracking().Where(row => row.Files.Any());

        if (group is { } only) query = query.Where(row => row.Id == only);

        // Split: the files' lengths, their genres and the credits are three
        // collections, and in one query every file's length is joined to every
        // file's genres and every credit.
        var rows = await query
            .AsSplitQuery()
            .Select(row => new
            {
                row.Id,
                row.Title,
                row.Mbid,
                row.FirstReleaseYear,
                SongCount = row.Files.Count,

                // Each file's length in the order its song takes it, so the
                // album lasts what its songs add up to.
                Lengths = row.Files
                    .Select(file => file.Quality!.Duration
                        ?? (file.Track != null ? file.Track.Length : null)
                        ?? (file.Recording != null ? file.Recording.Duration : null))
                    .ToList(),

                // Ordered rather than aggregated: PostgreSQL has comparison
                // operators for uuid but no max() over it, and the query fails
                // with 42883. The ids are UUIDv7, so the largest is the newest.
                Added = row.Files
                    .OrderByDescending(file => file.Id)
                    .Select(file => file.Id)
                    .FirstOrDefault(),

                Created = row.Files.Min(file => file.LastModifiedUtc),

                row.EditsJson,
                row.Genres,
                RecordingGenres = row.Files
                    .Select(file => file.Recording != null ? file.Recording.Genres : null)
                    .ToList(),

                Credits = row.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.ArtistId,
                        credit.CreditedAs,
                        credit.JoinPhrase,
                        Name = credit.Artist!.LatinName ?? credit.Artist!.Name,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var editions = await CatalogueEndpoints
            .EditionFactsAsync(db, [.. rows.Select(row => row.Id)], cancellationToken)
            .ConfigureAwait(false);

        var albums = rows
            .Select(row =>
            {
                var display = CatalogueEndpoints.DisplayEdition(editions[row.Id]);
                var edits = PersonEdits.Combine(row.EditsJson, display?.EditsJson);

                var billed = display is { Artists.Count: > 0 }
                    ? display.Artists.Select(credit => (credit.ArtistId, Name: credit.CreditedAs ?? credit.Name, credit.JoinPhrase)).ToList()
                    : row.Credits.Select(credit => (credit.ArtistId, Name: credit.CreditedAs ?? credit.Name, credit.JoinPhrase)).ToList();

                var line = CatalogueEndpoints.CreditLine(billed.Select(credit => (credit.Name, credit.JoinPhrase)));

                return new Album(
                    row.Id,
                    PersonEdits.Apply(edits, "title", row.Title) ?? row.Title,
                    PersonEdits.Apply(edits, "credit", line),
                    billed.Count == 0 ? null : billed[0].ArtistId,
                    Year(edits, "firstReleaseYear", CatalogueEndpoints.AlbumYear(row.FirstReleaseYear, editions[row.Id])),
                    row.Mbid,
                    row.SongCount,
                    row.Lengths.Sum(length => length is { } measured ? (int)Math.Round(measured.TotalSeconds) : 0),
                    row.Added.Value,
                    Minted(row.Added.Value) ?? row.Created,
                    FileGenres.OfAlbum(row.Genres, row.RecordingGenres));
            })
            .ToList();

        return artist is { } billedTo
            ? albums.Where(album => album.ArtistId == billedTo).ToList()
            : albums;
    }

    /// <summary>
    /// When a UUIDv7 was made: its first 48 bits are Unix milliseconds. Null
    /// for any other kind of id.
    /// </summary>
    private static DateTimeOffset? Minted(Guid id)
    {
        if (id.Version != 7)
        {
            return null;
        }

        var bytes = id.ToByteArray(bigEndian: true);
        var milliseconds = 0L;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static int? Year(IReadOnlyDictionary<string, string?> edits, string field, int? stated)
    {
        var edited = PersonEdits.Apply(
            edits,
            field,
            stated?.ToString(CultureInfo.InvariantCulture));

        return int.TryParse(edited, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static Task<IReadOnlyList<Song>> SongsAsync(
        FonotecaDbContext db,
        ReleaseGroupId? album,
        CancellationToken cancellationToken,
        MediaFileId? file = null)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (album is { } group) query = query.Where(row => row.ReleaseGroupId == group);
        if (file is { } one) query = query.Where(row => row.Id == one);

        return SongsAsync(db, query, cancellationToken);
    }

    private static async Task<IReadOnlyList<Song>> SearchSongsAsync(
        FonotecaDbContext db,
        string? pattern,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (pattern is not null)
        {
            query = query.Where(row =>
                EF.Functions.ILike(row.Path, pattern, "\\")
                || (row.Recording != null
                    && EF.Functions.ILike(row.Recording.Title, pattern, "\\")));
        }

        var page = query.OrderBy(row => row.Path).Skip(offset).Take(count);

        return await SongsAsync(db, page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Song>> SongsAsync(
        FonotecaDbContext db,
        IQueryable<MediaFile> files,
        CancellationToken cancellationToken)
    {
        var rows = await files
            .Select(file => new SongRow(
                file.Id,
                file.Path,
                file.SizeBytes,
                file.LastModifiedUtc,
                file.Quality,
                file.ReleaseGroupId,
                file.Recording != null ? file.Recording.Title : null,
                file.Recording != null ? file.Recording.Duration : null,
                file.Recording != null ? file.Recording.Mbid : null,
                file.Track != null ? file.Track.Title : null,
                file.Track != null ? (int?)file.Track.Position : null,
                file.Track != null ? (int?)file.Track.DiscNumber : null,
                file.Track != null ? file.Track.Length : null,
                file.FolderPosition,
                file.TagDiscNumber,
                file.TagTrackNumber,
                file.ReleaseGroup != null ? file.ReleaseGroup.Title : null,
                file.ReleaseGroup != null ? file.ReleaseGroup.FirstReleaseYear : null,
                file.Release != null ? file.Release.ReleasedYear : null,
                file.ReleaseGroup != null ? file.ReleaseGroup.EditsJson : null,
                file.Release != null ? file.Release.EditsJson : null,
                file.ReleaseGroup != null ? file.ReleaseGroup.Genres : null,
                file.Recording != null ? file.Recording.Genres : null,
                file.Measurement,
                file.RecordingId,
                file.Recording != null ? file.Recording.WorkId : null,
                // No ternary on the navigation: a null one yields an empty
                // collection through the join EF writes, where `new List<>()`
                // is not something it can translate at all.
                file.Release!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditRow(
                        credit.ArtistId,
                        credit.CreditedAs ?? (credit.Artist!.LatinName ?? credit.Artist!.Name),
                        credit.JoinPhrase))
                    .ToList(),
                file.ReleaseGroup!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditRow(
                        credit.ArtistId,
                        credit.CreditedAs ?? (credit.Artist!.LatinName ?? credit.Artist!.Name),
                        credit.JoinPhrase))
                    .ToList(),
                file.Recording!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditRow(
                        credit.ArtistId,
                        credit.CreditedAs ?? (credit.Artist!.LatinName ?? credit.Artist!.Name),
                        credit.JoinPhrase))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var albums = await AlbumLoudness.ForAsync(db, rows.Select(row => row.Path), cancellationToken).ConfigureAwait(false);

        var works = await SongWorksAsync(
                db,
                rows.Where(row => row.Work is not null).Select(row => row.Work!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        var performances = await SongPerformancesAsync(db, rows, cancellationToken).ConfigureAwait(false);

        // The album folder first: two rips of one album are one album here, and
        // interleaving them would play every track twice in a row.
        return rows
            .OrderBy(row => AlbumFolder.Of(row.Path), StringComparer.Ordinal)
            .ThenBy(row => row.DiscNumber ?? 1)
            .ThenBy(row => row.Position ?? int.MaxValue)
            .ThenBy(row => row.FolderPosition ?? int.MaxValue)
            .ThenBy(row => AlbumFolder.SortKey(row.Path), StringComparer.Ordinal)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .Select(row => Describe(
                row,
                albums.GetValueOrDefault(AlbumFolder.Of(row.Path))?.ReplayGain,
                row.Work is { } work ? works.GetValueOrDefault(work) : null,
                row.Recording is { } recording ? performances.GetValueOrDefault(recording) : default))
            .ToList();
    }

    /// <summary>Who played on each recording and wrote what it plays, and when and where it was played.</summary>
    /// <remarks>
    /// <b>The credits are the tag write's</b> (<see cref="CreditTags.Role"/>),
    /// the recording's links and then its work's, each role, part and artist
    /// once: a session over five days is five conductor links to one man. An
    /// artist is the catalogue's where it has a row with that MBID, named as
    /// the catalogue names them; otherwise as the link credits them, with no
    /// id, since a contribution mints no artist (the owner's choice: a client
    /// draws an empty id as text, where an id of nothing opens an error).
    /// </remarks>
    private static async Task<Dictionary<RecordingId, (IReadOnlyList<SongContributor> Contributors, SongRecorded? Recorded)>> SongPerformancesAsync(
        FonotecaDbContext db,
        IReadOnlyList<SongRow> rows,
        CancellationToken cancellationToken)
    {
        var recordings = rows.Where(row => row.Recording is not null).Select(row => row.Recording!.Value).Distinct().ToList();

        if (recordings.Count == 0) return [];

        var workOf = rows
            .Where(row => row.Recording is not null && row.Work is not null)
            .DistinctBy(row => row.Recording)
            .ToDictionary(row => row.Recording!.Value, row => row.Work!.Value);
        var works = workOf.Values.Distinct().ToList();

        var links = await db.Contributions
            .AsNoTracking()
            .Where(link => (link.RecordingId != null && recordings.Contains(link.RecordingId.Value))
                || (link.WorkId != null && works.Contains(link.WorkId.Value)))
            .OrderBy(link => link.Position)
            .Select(link => new { link.RecordingId, link.WorkId, link.Type, link.Attributes, link.ArtistMbid, link.Name, link.Begin, link.End })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessions = (await db.RecordingSessions
                .AsNoTracking()
                .Where(session => recordings.Contains(session.RecordingId))
                .OrderBy(session => session.Position)
                .Select(session => new { session.RecordingId, session.Type, session.Name, session.Begin, session.End })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(session => session.RecordingId);

        var mbids = links.Where(link => link.ArtistMbid is not null).Select(link => link.ArtistMbid!.Value).Distinct().ToList();

        var artists = (await db.Artists
                .AsNoTracking()
                .Where(artist => artist.Mbid != null && mbids.Contains(artist.Mbid.Value))
                .Select(artist => new { Mbid = artist.Mbid!.Value, artist.Id, Name = artist.LatinName ?? artist.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .DistinctBy(artist => artist.Mbid)
            .ToDictionary(artist => artist.Mbid);

        var onRecording = links.Where(link => link.RecordingId is not null).ToLookup(link => link.RecordingId!.Value);
        var onWork = links.Where(link => link.WorkId is not null).ToLookup(link => link.WorkId!.Value);

        return recordings.ToDictionary(recording => recording, recording =>
        {
            var own = onRecording[recording].ToList();
            var all = workOf.TryGetValue(recording, out var work) ? own.Concat(onWork[work]) : own;

            IReadOnlyList<SongContributor> contributors =
            [
                .. all
                    .Select(link => (Link: link, Role: CreditTags.Role(link.Type, link.Attributes)))
                    .Where(credit => credit.Role is not null)
                    .Where(credit => credit.Link.ArtistMbid is not { } mbid || !CatalogueEndpoints.Nobody.Contains(mbid))
                    .Select(credit => credit.Link.ArtistMbid is { } mbid && artists.TryGetValue(mbid, out var artist)
                        ? new SongContributor(credit.Role!.Value.Tag.ToLowerInvariant(), credit.Role.Value.Part, artist.Id, artist.Name, mbid)
                        : new SongContributor(credit.Role!.Value.Tag.ToLowerInvariant(), credit.Role.Value.Part, null, credit.Link.Name, credit.Link.ArtistMbid))
                    .DistinctBy(contributor => (contributor.Role, contributor.SubRole, contributor.Mbid?.Value.ToString() ?? contributor.Name)),
            ];

            var (span, places) = Performance.Recorded(
                sessions[recording].Select(session => (session.Type, session.Name, session.Begin, session.End)),
                own.Select(link => (link.Type, link.Attributes, link.Begin, link.End)));

            return (contributors, span is null && places.Count == 0 ? null : new SongRecorded(span, places));
        });
    }

    /// <summary>Who composed each work the songs perform, and the piece and movement of a classical one.</summary>
    /// <remarks>
    /// <b>A piece is named only for a classical work</b>, by the Composers
    /// page's rule (<see cref="ClassicalWorks"/>) over every held performance,
    /// so a client heading runs with pieces leaves a pop album alone. The type
    /// asked is the piece's: MusicBrainz types a song cycle's parts <c>Song</c>.
    /// The piece is the top of the chain, as the album page heads a run with.
    ///
    /// <b>A movement's number and count only where its parent is the piece</b>:
    /// deeper down, a number is its place in an act, not in the opera.
    ///
    /// <b>The composers are the work's, else the piece's</b>, since a parent
    /// minted from a stub has no writers until it is described. Named as the
    /// catalogue names them where it has a row, so in Latin script.
    /// </remarks>
    private static async Task<Dictionary<WorkId, SongWork>> SongWorksAsync(
        FonotecaDbContext db,
        IEnumerable<WorkId> performed,
        CancellationToken cancellationToken)
    {
        var ids = performed.Distinct().ToList();

        if (ids.Count == 0) return [];

        var parents = await CatalogueEndpoints.WorkParentsAsync(db, cancellationToken).ConfigureAwait(false);
        var pieces = ids.ToDictionary(
            id => id,
            id => parents.TryGetValue(id, out var parent) ? WorkTree.Top(parent, parents) : id);
        var wanted = ids
            .Concat(pieces.Values)
            .Concat(ids.Where(parents.ContainsKey).Select(id => parents[id]))
            .Distinct()
            .ToList();

        var works = await db.Works
            .AsNoTracking()
            .Where(work => wanted.Contains(work.Id))
            .Select(work => new WorkFacts(work.Id, work.Title, work.Mbid, work.Type, work.Position, work.PartCount, work.ComposedBegin, work.ComposedEnd))
            .ToDictionaryAsync(work => work.Id, cancellationToken)
            .ConfigureAwait(false);

        var performances = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.WorkId != null && ids.Contains(recording.WorkId.Value) && recording.Files.Any())
            .Select(recording => new
            {
                Work = recording.WorkId!.Value,
                recording.Genres,
                Albums = recording.Files
                    .Select(file => file.ReleaseGroup == null ? null : file.ReleaseGroup.Genres)
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var classical = performances
            .GroupBy(performance => performance.Work)
            .Where(work => ClassicalWorks.Is(
                works[pieces[work.Key]].Type,
                work.SelectMany(performance => performance.Albums.Prepend(performance.Genres))))
            .Select(work => work.Key)
            .ToHashSet();

        var credited = await db.Contributions
            .AsNoTracking()
            .Where(credit => credit.Type == CatalogueEndpoints.ComposerLink
                && credit.WorkId != null
                && wanted.Contains(credit.WorkId.Value))
            .OrderBy(credit => credit.Position)
            .Select(credit => new { Work = credit.WorkId!.Value, credit.ArtistMbid, credit.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var mbids = credited
            .Where(credit => credit.ArtistMbid is not null)
            .Select(credit => credit.ArtistMbid!.Value)
            .Distinct()
            .ToList();

        var names = (await db.Artists
                .AsNoTracking()
                .Where(artist => artist.Mbid != null && mbids.Contains(artist.Mbid.Value))
                .Select(artist => new { Mbid = artist.Mbid!.Value, Name = artist.LatinName ?? artist.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .DistinctBy(artist => artist.Mbid)
            .ToDictionary(artist => artist.Mbid, artist => artist.Name);

        var composers = credited
            .Where(credit => credit.ArtistMbid is not { } mbid || !CatalogueEndpoints.Nobody.Contains(mbid))
            .ToLookup(
                credit => credit.Work,
                credit => credit.ArtistMbid is { } mbid && names.TryGetValue(mbid, out var name) ? name : credit.Name);

        string? ComposersOf(WorkId work) =>
            composers[work].Distinct().ToList() is { Count: > 0 } named ? string.Join(", ", named) : null;

        return ids.ToDictionary(id => id, id =>
        {
            var piece = pieces[id];
            var composer = ComposersOf(id) ?? ComposersOf(piece);

            if (!classical.Contains(id)) return new SongWork(composer, null, null);

            var whole = works[piece];

            return new SongWork(
                composer,
                PieceOf(whole),
                piece == id ? null : MovementOf(works[id], works[parents[id]], whole));
        });
    }

    private static SongPiece PieceOf(WorkFacts whole) =>
        new(whole.Title, whole.Mbid, Performance.Span([(whole.ComposedBegin, whole.ComposedEnd)]));

    /// <summary>A part of the piece, named without the piece in front; numbered only where the piece is its parent.</summary>
    private static SongMovement MovementOf(WorkFacts part, WorkFacts parent, WorkFacts whole)
    {
        var direct = parent.Id == whole.Id;

        return new SongMovement(
            Without(part.Title, whole.Title) ?? Without(part.Title, parent.Title) ?? part.Title,
            direct ? part.Position : null,
            direct ? parent.PartCount : null);
    }

    /// <summary>A part's title with the piece's in front taken off: "I. Allegro con brio".</summary>
    private static string? Without(string title, string whole) =>
        title.Length > whole.Length + 2 && title.StartsWith(whole + ": ", StringComparison.Ordinal)
            ? title[(whole.Length + 2)..]
            : null;

    /// <summary>
    /// One file, as a song.
    /// </summary>
    /// <remarks>
    /// <b>A duration is taken from whatever measured it, in order:</b> the probe
    /// pass, then the track length MusicBrainz prints, then the recording's.
    /// Only the first is about this file — the other two are about the music —
    /// but a client with no duration has no progress bar and no seeking, and a
    /// printed length is a far better answer than none.
    ///
    /// <b>The media type comes from the same allowlist the file endpoints
    /// use</b>, never from the extension itself, so what a client is told a song
    /// is and what the bytes are served as cannot drift apart.
    /// </remarks>
    private static Song Describe(
        SongRow row,
        (double Gain, double Peak)? album,
        SongWork? work,
        (IReadOnlyList<SongContributor> Contributors, SongRecorded? Recorded) performance)
    {
        var edits = PersonEdits.Combine(row.AlbumEdits, row.PressingEdits);

        // The pressing's billing line where one is claimed, the album's own
        // otherwise.
        var albumCredits = row.AlbumCredits.Count > 0 ? row.AlbumCredits : row.GroupCredits;

        var albumLine = CatalogueEndpoints.CreditLine(
            albumCredits.Select(credit => (credit.Name, credit.JoinPhrase)));

        var ownLine = CatalogueEndpoints.CreditLine(
            row.RecordingCredits.Select(credit => (credit.Name, credit.JoinPhrase)));

        var duration = row.Quality?.Duration ?? row.TrackLength ?? row.RecordingDuration;
        var preview = FilePreview.Of(row.Path);

        return new Song(
            SubsonicIds.Song(row.Id),
            row.TrackTitle ?? row.RecordingTitle ?? System.IO.Path.GetFileNameWithoutExtension(row.Path),
            row.Path,
            row.AlbumTitle is null
                ? null
                : PersonEdits.Apply(edits, "title", row.AlbumTitle) ?? row.AlbumTitle,
            row.AlbumId,
            ownLine ?? PersonEdits.Apply(edits, "credit", albumLine),
            (row.RecordingCredits.Count > 0 ? row.RecordingCredits : albumCredits)
                .Select(credit => (ArtistId?)credit.ArtistId)
                .FirstOrDefault(),
            // The pressing's numbers where one is claimed, otherwise the file's
            // own: what the file says about itself, never a number made up.
            row.Position ?? row.TagTrack,
            row.DiscNumber ?? row.TagDisc,
            row.AlbumYear is { } first
                ? Year(edits, "firstReleaseYear", first)
                : Year(edits, "releasedYear", row.PressingYear),
            row.RecordingMbid,
            row.SizeBytes,
            preview.MediaType,
            System.IO.Path.GetExtension(row.Path).TrimStart('.').ToLowerInvariant(),
            duration is { } measured ? (int)Math.Round(measured.TotalSeconds) : null,
            row.Quality is { } quality ? (int)(quality.BitrateBps / 1000) : null,
            row.Quality?.SampleRateHz,
            row.Quality?.Channels,

            // The depth the audio really uses, as the upgrade list trusts it.
            AudioAnalysis.Decided(row.Quality, row.Measurement)?.BitDepth,
            row.LastModifiedUtc,
            FileGenres.Of(row.AlbumGenres, row.RecordingGenres),
            row.Measurement is null && album is null
                ? null
                : new SongReplayGain(
                    AudioAnalysis.TrackGain(row.Measurement),
                    AudioAnalysis.TrackPeak(row.Measurement),
                    album?.Gain,
                    album?.Peak),
            work,
            performance.Contributors ?? [],
            performance.Recorded);
    }

    /// <summary>One media file and everything around it a song element needs.</summary>
    private sealed record SongRow(
        MediaFileId Id,
        string Path,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc,
        AudioQuality? Quality,
        ReleaseGroupId? AlbumId,
        string? RecordingTitle,
        TimeSpan? RecordingDuration,
        Mbid? RecordingMbid,
        string? TrackTitle,
        int? Position,
        int? DiscNumber,
        TimeSpan? TrackLength,
        int? FolderPosition,
        int? TagDisc,
        int? TagTrack,
        string? AlbumTitle,
        int? AlbumYear,
        int? PressingYear,
        string? AlbumEdits,
        string? PressingEdits,
        string? AlbumGenres,
        string? RecordingGenres,
        AudioMeasurement? Measurement,
        RecordingId? Recording,
        WorkId? Work,
        List<CreditRow> AlbumCredits,
        List<CreditRow> GroupCredits,
        List<CreditRow> RecordingCredits);

    /// <summary>What a song's piece and movement are worded from.</summary>
    private sealed record WorkFacts(
        WorkId Id,
        string Title,
        Mbid? Mbid,
        string? Type,
        int? Position,
        int? PartCount,
        string? ComposedBegin,
        string? ComposedEnd);

    /// <summary>One line of a billing, already resolved to what it prints.</summary>
    private sealed record CreditRow(ArtistId ArtistId, string Name, string? JoinPhrase);
}
