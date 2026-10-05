using System.Globalization;
using System.Text.Json;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Providers.Qobuz;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// Files a download into the catalogue as the shop described it, at the one
/// moment its description can be believed (ADR 0011).
/// </summary>
/// <remarks>
/// <b>The shop is an authority about the album, not about the audio.</b> So a
/// filed file is linked to its recording, its release and its slot, with
/// <see cref="EnrichmentOutcome.LinkedByProvider"/> and
/// <see cref="ReleaseAttributionOutcome.AttributedByProvider"/>, and its
/// recording and release are settled (<c>IdentityDecidedUtc</c>,
/// <c>ReleaseDecidedUtc</c>) — but <c>AcoustIdDecidedUtc</c> and every lookup
/// stamp are left alone: the audio is still fingerprinted, and nobody claims
/// MusicBrainz or AcoustID was asked.
///
/// <b>A release is keyed on its barcode.</b> One minted from an earlier
/// download of the same barcode is found again rather than minted twice, its
/// slots by disc and position. An album with no barcode is minted afresh each
/// time, which is the honest failure.
///
/// <b>An artist is linked only on a unique name match</b> — the owner's
/// choice: this library holds two Sonny Boy Williamsons — else minted with no
/// MBID. Only the four roles the catalogue keeps are written; see
/// <see cref="ProviderCredits"/>.
///
/// <b>The download is the scan for the files it wrote</b>, so the row records
/// the size and mtime as they are on disk now, and a scan that raced it to the
/// same path is met by finding the row again rather than colliding with it.
/// </remarks>
public sealed class DownloadFiling(
    IServiceScopeFactory scopeFactory,
    IAudioFileStore store,
    IClock clock)
{
    /// <summary>One journal entry per filed album, subject the release.</summary>
    public const string FiledEvent = "acquire.album.filed";

    /// <summary>
    /// Whether most of an album folder's files are filed under an album
    /// MusicBrainz knows — a replaced album the passes are left to re-file.
    /// </summary>
    public async Task<bool> KnownToMusicBrainzAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var prefix = folder.Trim('/') + "/";
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var files = await db.MediaFiles
                .Where(file => file.Path.StartsWith(prefix))
                .Select(file => file.ReleaseGroup != null && file.ReleaseGroup.Mbid != null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return files.Count(known => known) * 2 > files.Count;
        }
    }

    /// <summary>Files every track of a download that is on disk, under the shop's album.</summary>
    /// <param name="landed">Each delivered track and the library-relative path it is at now.</param>
    /// <param name="known">
    /// The MusicBrainz release the shop's barcode and track list name exactly,
    /// where there is one (<see cref="MusicBrainzCatchUp.FindAsync"/>): the files
    /// are filed under it rather than under a release minted from the shop.
    /// </param>
    public async Task<DownloadFiled> FileAsync(
        QobuzAlbum album,
        IReadOnlyList<(QobuzTrack Track, string Path)> landed,
        string actorId,
        MusicBrainzRelease? known = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(landed);

        if (landed.Count == 0) return DownloadFiled.Nothing("No track arrived, so there was nothing to file.");

        // Once more on a path collision: a scan, which is on no gate, can add a
        // row for a file this wrote between the read below and the save.
        try
        {
            return await AttemptAsync(album, landed, actorId, known, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException cause) when (cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return await AttemptAsync(album, landed, actorId, known, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DownloadFiled> AttemptAsync(
        QobuzAlbum album,
        IReadOnlyList<(QobuzTrack Track, string Path)> landed,
        string actorId,
        MusicBrainzRelease? known,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<IEventLog>();
            var now = clock.UtcNow;

            var paths = landed.Select(item => item.Path).ToList();
            var rows = await db.MediaFiles
                .Where(file => paths.Contains(file.Path))
                .ToDictionaryAsync(file => file.Path, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            var (releaseId, groupId, seat) = known is null
                ? await ShopsAsync(db, album, cancellationToken).ConfigureAwait(false)
                : await MusicBrainzsAsync(db, known, cancellationToken).ConfigureAwait(false);

            var filed = new List<MediaFileId>();
            var kept = 0;

            foreach (var (track, path) in landed)
            {
                // A person's answer stands (rule 4); the shop's own, or none, is replaced.
                if (rows.TryGetValue(path, out var existing)
                    && existing.ReleaseDecidedUtc is not null
                    && existing.AttributionOutcome != ReleaseAttributionOutcome.AttributedByProvider)
                {
                    kept++;
                    continue;
                }

                if (await store.StatAsync(new LibraryPath(path), cancellationToken).ConfigureAwait(false) is not { } facts
                    || seat(track) is not { } slot)
                {
                    kept++;
                    continue;
                }

                // Rule 2: the bytes as they are now, in the same save.
                var row = existing ?? new MediaFile
                {
                    Id = MediaFileId.New(),
                    Path = path,
                    SizeBytes = facts.SizeBytes,
                    LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc),
                };

                if (existing is null) db.MediaFiles.Add(row);

                row.SizeBytes = facts.SizeBytes;
                row.LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc);

                row.RecordingId = slot.Recording;
                row.ReleaseId = releaseId;
                row.ReleaseGroupId = groupId;
                row.TrackId = slot.Track;
                row.EditionAlternatives = 0;

                row.EnrichmentOutcome = EnrichmentOutcome.LinkedByProvider;
                row.AttributionOutcome = ReleaseAttributionOutcome.AttributedByProvider;
                row.IdentityDecidedUtc = now;
                row.ReleaseDecidedUtc = now;

                filed.Add(row.Id);
            }

            var correlation = Guid.CreateVersion7().ToString("N")[..12];

            await events.AppendAsync(
                    DomainEvent.Create(
                        FiledEvent,
                        "release",
                        releaseId.ToString(),
                        actorId,
                        now,
                        JsonSerializer.Serialize(new FiledPayload(
                            album.Id, album.Upc, known?.Id.Value, [.. filed.Select(id => id.Value)], kept)),
                        correlation),
                    cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new DownloadFiled(groupId?.Value, [.. filed.Select(id => id.Value)], kept, null);
        }
    }

    /// <summary>Where a track goes: a slot and its recording, or nowhere.</summary>
    private delegate (TrackId Track, RecordingId Recording)? Seat(QobuzTrack track);

    /// <summary>The MusicBrainz release, written as attribution writes one, and its slots by disc and position.</summary>
    private static async Task<(ReleaseId, ReleaseGroupId?, Seat)> MusicBrainzsAsync(
        FonotecaDbContext db,
        MusicBrainzRelease known,
        CancellationToken cancellationToken)
    {
        var writer = new ReleaseAttributionService.ReleaseWriter(db);
        var (release, group) = await writer.UpsertAsync(known, null, cancellationToken).ConfigureAwait(false);

        return (release, group, track =>
            writer.TrackIdAt(release, track.DiscNumber, track.TrackNumber) is { } slot
            && writer.RecordingIdAt(release, track.DiscNumber, track.TrackNumber) is { } recording
                ? (slot, recording)
                : null);
    }

    /// <summary>The shop's release — found again by its barcode, or minted — and its slots, minted as tracks need them.</summary>
    private static async Task<(ReleaseId, ReleaseGroupId?, Seat)> ShopsAsync(
        FonotecaDbContext db,
        QobuzAlbum album,
        CancellationToken cancellationToken)
    {
        var release = await ReleaseAsync(db, album, cancellationToken).ConfigureAwait(false);
        var minted = db.Entry(release).State == EntityState.Added;

        List<Track> slots = minted
            ? []
            : await db.Tracks
                .Where(track => track.ReleaseId == release.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var artists = await ArtistsAsync(db, cancellationToken).ConfigureAwait(false);

        if (minted)
        {
            foreach (var credit in ProviderCredits.Parse(null, album.Artist, names: album.Artists).Where(credit => credit.Role == CreditRole.Billed))
            {
                var artist = artists.For(credit);

                db.ArtistCredits.Add(new ArtistCredit
                {
                    Id = Guid.CreateVersion7(),
                    ArtistId = artist.Id,
                    ReleaseId = release.Id,
                    Position = credit.Position,
                    JoinPhrase = credit.JoinPhrase,
                    CreditedAs = LatinNames.CreditedAs(credit.Name, artist.Name),
                });
            }
        }

        return (release.Id, release.ReleaseGroupId, track =>
        {
            var slot = slots.FirstOrDefault(candidate =>
                candidate.DiscNumber == track.DiscNumber && candidate.Position == track.TrackNumber);

            if (slot is null)
            {
                var recording = new Recording
                {
                    Id = RecordingId.New(),
                    Title = track.Title,
                    Duration = track.Duration,
                    Isrc = track.Isrc,
                };

                db.Recordings.Add(recording);
                Credit(db, artists, recording, ProviderCredits.Parse(track.Performers, track.Performer ?? album.Artist, track.Composer, album.Artists));

                slot = new Track
                {
                    Id = TrackId.New(),
                    ReleaseId = release.Id,
                    RecordingId = recording.Id,
                    DiscNumber = track.DiscNumber,
                    Position = track.TrackNumber,
                    Number = track.TrackNumber.ToString(CultureInfo.InvariantCulture),
                    Title = track.Title,
                    Length = track.Duration,
                };

                db.Tracks.Add(slot);
                slots.Add(slot);
            }

            return (slot.Id, slot.RecordingId);
        });
    }

    /// <summary>The release minted from an earlier download of this barcode, or a new one.</summary>
    private static async Task<Release> ReleaseAsync(FonotecaDbContext db, QobuzAlbum album, CancellationToken cancellationToken)
    {
        if (album.Upc is { } barcode
            && await db.Releases
                .Where(release => release.Mbid == null && release.Barcode == barcode && release.ReleaseGroupId != null)
                .OrderBy(release => release.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) is { } held)
        {
            return held;
        }

        var date = Dated(album.ReleaseDate);

        var group = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = album.Title,
            PrimaryType = "Album",
            FirstReleaseYear = date.Year,
        };

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = album.Title,
            ReleaseGroupId = group.Id,
            ReleasedYear = date.Year,
            ReleasedMonth = date.Month,
            ReleasedDay = date.Day,
            Label = album.Label,
            Barcode = album.Upc,
            TrackCount = album.TrackCount,
            DiscCount = album.DiscCount,
        };

        db.ReleaseGroups.Add(group);
        db.Releases.Add(release);

        return release;
    }

    /// <summary>"1977-02-04", "1977-02" or "1977", as far as it goes.</summary>
    private static (int? Year, int? Month, int? Day) Dated(string? date)
    {
        var parts = (date ?? string.Empty).Split('-');

        int? Part(int index, int most) =>
            parts.Length > index
            && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= 1 && value <= most
                ? value
                : null;

        var year = Part(0, 9999);
        var month = year is null ? null : Part(1, 12);

        return (year, month, month is null ? null : Part(2, 31));
    }

    /// <summary>A recording's billing line and its conductor, ensemble and writers.</summary>
    private static void Credit(FonotecaDbContext db, Artists artists, Recording recording, IReadOnlyList<ProviderCredit> credits)
    {
        foreach (var credit in credits)
        {
            var artist = artists.For(credit);

            if (credit.Role == CreditRole.Billed)
            {
                db.ArtistCredits.Add(new ArtistCredit
                {
                    Id = Guid.CreateVersion7(),
                    ArtistId = artist.Id,
                    RecordingId = recording.Id,
                    Position = credit.Position,
                    JoinPhrase = credit.JoinPhrase,
                    CreditedAs = LatinNames.CreditedAs(credit.Name, artist.Name),
                });

                continue;
            }

            // No work to hang a writer on, so the recording, as CatalogueWriter
            // does when MusicBrainz names none.
            db.Relationships.Add(new Relationship
            {
                Id = Guid.CreateVersion7(),
                SourceType = RelationshipTargets.Artist,
                SourceId = artist.Id.Value,
                TargetType = RelationshipTargets.Recording,
                TargetId = recording.Id.Value,
                Type = CatalogueWriter.RoleName(credit.Role),
                Attribute = artist.Type,
                ArtistId = artist.Id,
                RecordingId = recording.Id,
            });
        }
    }

    /// <summary>Every artist by normalised name, so a credit is linked only where exactly one answers to it.</summary>
    private static async Task<Artists> ArtistsAsync(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var known = await db.Artists
            .AsNoTracking()
            .Select(artist => new { artist.Id, artist.Name, artist.LatinName, artist.Type })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byName = known
            .SelectMany(artist => new[] { artist.Name, artist.LatinName }
                .OfType<string>()
                .Select(ArtistNameMatch.Normalise)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(name => (Name: name, Artist: new Artist { Id = artist.Id, Name = artist.Name, Type = artist.Type })))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Artist).ToList(), StringComparer.Ordinal);

        return new Artists(db, byName);
    }

    /// <summary>The run's artists: linked on a unique name, else minted once and reused for the album.</summary>
    private sealed class Artists(FonotecaDbContext db, Dictionary<string, List<Artist>> byName)
    {
        private readonly Dictionary<string, Artist> _minted = new(StringComparer.Ordinal);

        public Artist For(ProviderCredit credit)
        {
            var name = ArtistNameMatch.Normalise(credit.Name);

            if (_minted.TryGetValue(name, out var mine)) return mine;

            if (name.Length > 0 && byName.TryGetValue(name, out var found) && found is [var only]) return only;

            var artist = new Artist
            {
                Id = ArtistId.New(),
                Name = credit.Name,
                Type = credit.Role == CreditRole.Ensemble ? EnsembleType(credit.Name) : null,
            };

            db.Artists.Add(artist);
            _minted[name] = artist;

            return artist;
        }

        private static string EnsembleType(string name) =>
            name.Contains("choir", StringComparison.OrdinalIgnoreCase) || name.Contains("chor", StringComparison.OrdinalIgnoreCase)
                ? "Choir"
                : "Orchestra";
    }

    private sealed record FiledPayload(string Qobuz, string? Barcode, Guid? MusicBrainz, IReadOnlyList<Guid> Files, int Kept);
}

/// <summary>What filing a download into the catalogue did.</summary>
/// <param name="AlbumId">The album the files were filed under, or null where none was.</param>
/// <param name="Files">The files filed under it.</param>
/// <param name="Kept">Files left as they were: a person had answered them, or they were gone from disk.</param>
/// <param name="Why">Why nothing was filed, where nothing was.</param>
public sealed record DownloadFiled(Guid? AlbumId, IReadOnlyList<Guid> Files, int Kept, string? Why)
{
    internal static DownloadFiled Nothing(string why) => new(null, [], 0, why);
}
