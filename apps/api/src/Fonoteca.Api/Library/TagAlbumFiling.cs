using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// Gives every album folder nobody could identify an album made from its
/// files' own tags (<see cref="TagAlbums"/>), and keeps it following them.
/// </summary>
/// <remarks>
/// <b>The owner's choice, for client issue #65</b>: a song with no album,
/// artist or title is unplayable by artist and unscrobblable, so the files'
/// own words are better than none. What this writes is catalogue, shaped as a
/// download's is (ADR 0011): a recording, a track, one release and one album
/// per folder, billed to artists linked only on a stated MBID or a unique name
/// (<see cref="ArtistsByName"/>).
///
/// <b>It answers nothing.</b> No outcome and no stamp is touched, so every
/// pass and the Identify screen see the file as they did: a file AcoustID
/// does not know is still that, and a person's "never released" stays theirs
/// (rule 4). Any later filing — a person's, an agent's, enrichment's link to a
/// MusicBrainz recording, attribution — replaces the tag album's columns, and
/// the next run leaves that file alone.
///
/// <b>Only a settled file joins one</b>: one identification has answered and
/// enrichment is not still waiting to ask about, with no recording and no
/// album, in a folder no other album holds. A file waiting on a pass would be
/// filed by its tags a moment before MusicBrainz answered for it.
///
/// <b>Rebuilt from the tags on every run</b>, in place — the album, its
/// release and each file's recording keep their ids, so hearts, a sleeve and
/// a person's album corrections stay — because the files are leading and
/// another program may have retagged them since. A folder any of whose files
/// cannot be read is left exactly as it was: an unmounted volume reads as
/// "no tags at all", and rebuilding from that would rename every song after
/// its file.
///
/// <b>Never written back.</b> The tag write treats these files as unanswered
/// (<see cref="TagWriteService.Answered"/>): the catalogue's answer is the
/// files' own, and writing "Covers" over six files' own album tags would lose
/// the only place each single's name was kept.
/// </remarks>
public sealed class TagAlbumFiling(
    IServiceScopeFactory scopeFactory,
    TagReader tagReader,
    ILogger<TagAlbumFiling> logger)
{
    /// <summary>The primary type an album from tags is given; nothing in a tag says otherwise.</summary>
    private const string AlbumType = "Album";

    /// <summary>Makes or rebuilds every folder's album from tags.</summary>
    /// <returns>How many folders were made or rebuilt.</returns>
    public async Task<int> FileAsync(CancellationToken cancellationToken)
    {
        var folders = await FoldersAsync(cancellationToken).ConfigureAwait(false);
        var (filed, files) = (0, 0);

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = await FileFolderAsync(folder, cancellationToken).ConfigureAwait(false);

            if (count is null) continue;

            filed++;
            files += count.Value;
        }

        Log.TagAlbumsFiled(logger, filed, files);

        return filed;
    }

    /// <summary>A file no pass will answer for any sooner, and that nothing has filed.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<MediaFile, bool>> Settled = file =>
        file.RecordingId == null
        && file.ReleaseGroupId == null
        && (file.AcoustIdCheckedUtc != null || file.AcoustIdDecidedUtc != null)
        && !(file.AcoustId != null && file.RecordingLookupUtc == null && file.IdentityDecidedUtc == null);

    private static readonly Func<MediaFile, bool> IsSettled = Settled.Compile();

    /// <summary>Folders holding a settled file, and every folder an album from tags is held in.</summary>
    private async Task<List<string>> FoldersAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var paths = await db.MediaFiles
                .AsNoTracking()
                .Where(Settled)
                .Select(file => file.Path)
                .Concat(db.MediaFiles
                    .Where(file => file.ReleaseGroup != null && file.ReleaseGroup.TagFolder != null)
                    .Select(file => file.Path))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // A file loose under an artist has no album folder, and naming one
            // after the artist would put it beside the artist's real albums.
            return [.. paths
                .Select(AlbumFolder.Of)
                .Where(folder => folder.Contains('/', StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>One folder's album, made or rebuilt; null where nothing was written.</summary>
    private async Task<int?> FileFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var prefix = folder + "/";

            var inFolder = (await db.MediaFiles
                    .Where(file => file.Path.StartsWith(prefix))
                    .Include(file => file.ReleaseGroup)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Where(file => AlbumFolder.Of(file.Path) == folder)
                .ToList();

            // Found by its folder, or by its files where the folder was renamed.
            var group = await db.ReleaseGroups.FirstOrDefaultAsync(row => row.TagFolder == folder, cancellationToken).ConfigureAwait(false)
                ?? inFolder.Select(file => file.ReleaseGroup).FirstOrDefault(row => row?.TagFolder is not null);

            var release = group is null
                ? null
                : await db.Releases.Include(row => row.Tracks).FirstOrDefaultAsync(row => row.ReleaseGroupId == group.Id, cancellationToken).ConfigureAwait(false);

            var ownRecordings = release?.Tracks.Select(track => track.RecordingId).ToHashSet() ?? [];

            var held = inFolder
                .Where(file => group is not null && file.ReleaseGroupId == group.Id && file.RecordingId is { } recording && ownRecordings.Contains(recording))
                .ToList();

            // One album per folder: a folder another album holds is that album's.
            var anotherAlbum = inFolder.Any(file => file.ReleaseGroupId is not null && file.ReleaseGroupId != group?.Id);
            var members = anotherAlbum ? held : [.. held, .. inFolder.Where(IsSettled)];

            if (members.Count == 0)
            {
                if (release is not null) db.Tracks.RemoveRange(release.Tracks);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var readings = await ReadAsync(folder, members, cancellationToken).ConfigureAwait(false);
            if (readings is null) return null;

            var album = TagAlbums.Of(folder, readings);
            var artists = await ArtistsByName.LoadAsync(db, cancellationToken).ConfigureAwait(false);

            group ??= Minted(db, folder);
            group.TagFolder = folder;
            group.Title = album.Title;
            group.FirstReleaseYear = album.Year;

            release ??= Minted(db, group);
            release.Title = album.Title;
            release.ReleasedYear = album.Year;
            release.TrackCount = album.Songs.Count;
            release.DiscCount = album.DiscCount;

            await BillAsync(db, artists, album.Billing, credit => credit.ReleaseId = release.Id, row => row.ReleaseId == release.Id, cancellationToken)
                .ConfigureAwait(false);

            var byFile = members.ToDictionary(file => file.Id);
            var kept = new HashSet<TrackId>();

            foreach (var song in album.Songs)
            {
                var file = byFile[song.Id];
                var track = release.Tracks.FirstOrDefault(row => row.RecordingId == file.RecordingId);
                var recording = track is null
                    ? null
                    : await db.Recordings.FirstOrDefaultAsync(row => row.Id == track.RecordingId, cancellationToken).ConfigureAwait(false);

                if (recording is null)
                {
                    recording = new Recording { Id = RecordingId.New(), Title = song.Title };
                    db.Recordings.Add(recording);
                }

                if (track is null)
                {
                    track = new Track { Id = TrackId.New(), ReleaseId = release.Id, RecordingId = recording.Id, Position = song.Position };
                    db.Tracks.Add(track);
                }

                recording.Title = song.Title;
                recording.Duration = file.Quality?.Duration;
                recording.Genres = song.Genres.Count == 0 ? null : string.Join(", ", song.Genres);

                track.Title = song.Title;
                track.DiscNumber = song.Disc;
                track.Position = song.Position;
                track.Number = song.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                track.Length = file.Quality?.Duration;
                kept.Add(track.Id);

                await BillAsync(db, artists, song.Billing, credit => credit.RecordingId = recording.Id, row => row.RecordingId == recording.Id, cancellationToken)
                    .ConfigureAwait(false);

                file.RecordingId = recording.Id;
                file.ReleaseId = release.Id;
                file.ReleaseGroupId = group.Id;
                file.TrackId = track.Id;
            }

            db.Tracks.RemoveRange(release.Tracks.Where(track => !kept.Contains(track.Id)));

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return members.Count;
        }
    }

    /// <summary>Every member's tags, or null where one could not be read.</summary>
    private async Task<List<TagAlbumFile>?> ReadAsync(string folder, IReadOnlyList<MediaFile> members, CancellationToken cancellationToken)
    {
        var read = new List<TagAlbumFile>();

        foreach (var file in members)
        {
            var path = new LibraryPath(file.Path);

            try
            {
                var tags = await tagReader.ReadAsync(path, null, cancellationToken).ConfigureAwait(false);

                string? Field(string canonical) =>
                    (CatalogueTagFields.Spell(path, canonical) is { } spelled ? tags.Find(spelled) : null) ?? tags.Find(canonical);

                read.Add(new TagAlbumFile(file.Id, file.Path, new TagReading(
                    Field(CatalogueTags.Title),
                    Field(CatalogueTags.Artist),
                    Field(CatalogueTags.ArtistId),
                    Field(CatalogueTags.AlbumArtist),
                    Field(CatalogueTags.AlbumArtistId),
                    Field(CatalogueTags.Album),
                    Field(CatalogueTags.TrackNumber),
                    Field(CatalogueTags.DiscNumber),
                    Field(CatalogueTags.Year),
                    Field(CatalogueTags.Genre))));
            }
            catch (Exception cause) when (cause is TagReadFailedException or IOException or UnauthorizedAccessException)
            {
                Log.TagAlbumNotRead(logger, folder, file.Path, cause.Message);
                return null;
            }
        }

        return read;
    }

    /// <summary>Replaces a billing line: the credits <paramref name="owned"/> selects go, and one per name is written.</summary>
    private static async Task BillAsync(
        FonotecaDbContext db,
        ArtistsByName artists,
        IReadOnlyList<TagCredit> billing,
        Action<ArtistCredit> owner,
        System.Linq.Expressions.Expression<Func<ArtistCredit, bool>> owned,
        CancellationToken cancellationToken)
    {
        db.ArtistCredits.RemoveRange(await db.ArtistCredits.Where(owned).ToListAsync(cancellationToken).ConfigureAwait(false));

        foreach (var credit in billing)
        {
            var artist = artists.For(new ProviderCredit(credit.Name, CreditRole.Billed, credit.Position, credit.JoinPhrase), credit.Mbid);

            var row = new ArtistCredit
            {
                Id = Guid.CreateVersion7(),
                ArtistId = artist.Id,
                Position = credit.Position,
                JoinPhrase = credit.JoinPhrase,
                CreditedAs = LatinNames.CreditedAs(credit.Name, artist.Name),
            };

            owner(row);
            db.ArtistCredits.Add(row);
        }
    }

    private static ReleaseGroup Minted(FonotecaDbContext db, string folder)
    {
        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = folder, PrimaryType = AlbumType, TagFolder = folder };
        db.ReleaseGroups.Add(group);
        return group;
    }

    private static Release Minted(FonotecaDbContext db, ReleaseGroup group)
    {
        var release = new Release { Id = ReleaseId.New(), Title = group.Title, ReleaseGroupId = group.Id };
        db.Releases.Add(release);
        return release;
    }
}
