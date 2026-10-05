using System.Text.Json;
using Fonoteca.Api.Library;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// Moves a download filed as the shop described it onto MusicBrainz, once
/// MusicBrainz knows it (ADR 0011).
/// </summary>
/// <remarks>
/// <b>By barcode first, and the album moves whole.</b> A release the barcode
/// and the track list name exactly (<see cref="ProviderMatch"/>) takes the
/// files slot for slot, and the release the download minted is removed: the
/// files are where they were, and only what the catalogue says they are
/// changes. Asked at download, then weekly — the owner's choice — for every
/// album still the shop's.
///
/// <b>By ISRC where the barcode finds nothing.</b> A recording MusicBrainz
/// holds under exactly one ISRC gains that MBID, or is folded into the row
/// already holding it; enrichment then fetches its credits. The album stays
/// the shop's.
///
/// <b>Rule 1:</b> <see cref="Release.ProviderCheckedUtc"/> is stamped when
/// MusicBrainz answered, nothing found included, and is asked again after
/// <see cref="AskAgainAfter"/>. An outage stamps nothing.
/// </remarks>
public sealed class MusicBrainzCatchUp(
    IMusicBrainzCatalogue musicBrainz,
    IServiceScopeFactory scopeFactory,
    LibraryWorkGate gate,
    LibraryScanService scans,
    IClock clock,
    ILogger<MusicBrainzCatchUp> logger)
{
    /// <summary>The kind the sweep takes the gate as, for each album it writes.</summary>
    public const string Kind = "acquire.catchup";

    /// <summary>One per album moved onto MusicBrainz or given MusicBrainz's recordings.</summary>
    public const string MatchedEvent = "acquire.album.matched";

    /// <summary>How long "MusicBrainz knows nothing of it" is believed.</summary>
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(7);

    /// <summary>Releases read per barcode. A barcode on more is a box set or a reuse, and no answer anyway.</summary>
    private const int MostCandidates = 5;

    /// <summary>The MusicBrainz release a barcode and a track list name exactly, or null.</summary>
    /// <exception cref="ProviderUnavailableException">MusicBrainz did not answer.</exception>
    /// <exception cref="ProviderRejectedException">It refused — a server with no search index among the reasons.</exception>
    public async Task<MusicBrainzRelease?> FindAsync(
        string? barcode,
        IReadOnlyCollection<(int Disc, int Position)> slots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (Barcodes.Normalise(barcode) is not { } wanted || slots.Count == 0) return null;

        // The shop's digits, and without their leading zeros: MusicBrainz holds
        // a barcode as it was entered, and both are entered.
        var digits = new string([.. barcode!.Where(char.IsAsciiDigit)]);
        var bare = digits.TrimStart('0');
        var query = bare.Length > 0 && bare != digits ? $"barcode:{digits} OR barcode:{bare}" : $"barcode:{digits}";

        var matches = await musicBrainz.SearchReleasesAsync(query, 25, cancellationToken).ConfigureAwait(false);
        var releases = new List<MusicBrainzRelease>();

        foreach (var candidate in matches
                     .Select(match => match.Release)
                     .Where(release => Barcodes.Normalise(release.Barcode) == wanted)
                     .DistinctBy(release => release.Id)
                     .Take(MostCandidates))
        {
            if (await musicBrainz.GetReleaseAsync(candidate.Id, cancellationToken).ConfigureAwait(false) is { } release)
            {
                releases.Add(release);
            }
        }

        return ProviderMatch.Pick(barcode, slots, releases);
    }

    /// <summary>
    /// Asks MusicBrainz about every album still the shop's that is due, and
    /// moves what it now knows.
    /// </summary>
    /// <remarks>
    /// Stops for the gate and for the scan, which is on no gate, and for an
    /// outage; what it did not reach is asked on the next sweep.
    /// </remarks>
    /// <returns>How many albums were moved onto MusicBrainz, and how many recordings gained an MBID.</returns>
    public async Task<(int Albums, int Recordings)> SweepAsync(CancellationToken cancellationToken = default)
    {
        var due = clock.UtcNow - AskAgainAfter;
        List<DueRelease> releases;

        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            releases = await db.Releases
                .AsNoTracking()
                .Where(release => release.Mbid == null
                    && (release.ProviderCheckedUtc == null || release.ProviderCheckedUtc < due)
                    && db.MediaFiles.Any(file => file.ReleaseId == release.Id
                        && file.AttributionOutcome == ReleaseAttributionOutcome.AttributedByProvider))
                .OrderBy(release => release.Id)
                .Select(release => new DueRelease(release.Id, release.Barcode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var (albums, recordings) = (0, 0);

        foreach (var release in releases)
        {
            if (gate.IsBusy || scans.IsRunning) break;

            var (slots, isrcs) = await ShopsAsync(release.Id, cancellationToken).ConfigureAwait(false);

            MusicBrainzRelease? found;
            var links = new Dictionary<RecordingId, Mbid>();

            try
            {
                try
                {
                    found = await FindAsync(release.Barcode, slots, cancellationToken).ConfigureAwait(false);
                }
                catch (ProviderRejectedException cause)
                {
                    // A server that cannot search can still look an ISRC up.
                    Log.CatchUpNotAsked(logger, release.Id.Value, cause.Message);
                    found = null;
                }

                if (found is null)
                {
                    foreach (var (recording, isrc) in isrcs)
                    {
                        if (await musicBrainz.RecordingsForIsrcAsync(isrc, cancellationToken).ConfigureAwait(false) is [var only])
                        {
                            links[recording] = only;
                        }
                    }
                }
            }
            catch (ProviderUnavailableException cause)
            {
                // Nothing is stamped: MusicBrainz was not asked, whatever it would
                // have said, and nothing more will be today.
                Log.CatchUpNotAsked(logger, release.Id.Value, cause.Message);
                break;
            }
            catch (ProviderRejectedException cause)
            {
                // This album's question refused — an ISRC MusicBrainz will not
                // take. Unstamped, and no reason the albums after it go unasked.
                Log.CatchUpNotAsked(logger, release.Id.Value, cause.Message);
                continue;
            }

            if (!gate.TryEnter(Kind, out var lease)) break;

            using (lease)
            {
                bool moved;

                try
                {
                    moved = await ApplyAsync(release.Id, found, links, cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException cause)
                {
                    // One album the catalogue would not take must not keep every
                    // album after it from being asked; it is asked again tomorrow.
                    Log.CatchUpNotAsked(logger, release.Id.Value, cause.Message);
                    continue;
                }

                if (moved) albums++;
                recordings += found is null ? links.Count : 0;
            }
        }

        return (albums, recordings);
    }

    /// <summary>The release's slots and its MBID-less recordings' ISRCs, as the download wrote them.</summary>
    private async Task<(List<(int Disc, int Position)> Slots, List<(RecordingId Recording, string Isrc)> Isrcs)> ShopsAsync(
        ReleaseId release,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var tracks = await db.Tracks
                .AsNoTracking()
                .Where(track => track.ReleaseId == release)
                .Select(track => new { track.DiscNumber, track.Position, track.RecordingId, track.Recording!.Mbid, track.Recording.Isrc })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return (
                [.. tracks.Select(track => (track.DiscNumber, track.Position))],
                [.. tracks
                    .Where(track => track.Mbid is null && track.Isrc is not null)
                    .Select(track => (track.RecordingId, track.Isrc!))
                    .DistinctBy(pair => pair.RecordingId)]);
        }
    }

    /// <summary>Writes what MusicBrainz answered for one album; true where the album moved onto it.</summary>
    private async Task<bool> ApplyAsync(
        ReleaseId id,
        MusicBrainzRelease? found,
        Dictionary<RecordingId, Mbid> links,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<IEventLog>();
            var now = clock.UtcNow;

            // Gone means a scan or a person moved every file off it meanwhile.
            if (await db.Releases.FirstOrDefaultAsync(release => release.Id == id, cancellationToken).ConfigureAwait(false) is not { } release)
            {
                return false;
            }

            if (found is not null)
            {
                var moved = await MoveAsync(db, id, found, cancellationToken).ConfigureAwait(false);

                await events.AppendAsync(Matched(id, found.Id, moved.Files, [], now), cancellationToken).ConfigureAwait(false);

                if (moved.Left > 0) release.ProviderCheckedUtc = now;

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                if (moved.Left == 0) await ForgetAsync(db, [id], cancellationToken).ConfigureAwait(false);

                return moved.Left == 0;
            }

            var folded = new List<RecordingId>();

            // Two of the album's tracks can name one recording: the second is
            // folded into the first, as into a row the catalogue already held.
            var given = new Dictionary<Mbid, RecordingId>();

            foreach (var (recording, mbid) in links)
            {
                var holder = given.TryGetValue(mbid, out var mine)
                    ? mine
                    : (await db.Recordings
                        .FirstOrDefaultAsync(candidate => candidate.Mbid == mbid, cancellationToken)
                        .ConfigureAwait(false))?.Id;

                if (holder is null)
                {
                    var minted = await db.Recordings.FirstOrDefaultAsync(candidate => candidate.Id == recording, cancellationToken).ConfigureAwait(false);

                    if (minted is not null)
                    {
                        minted.Mbid = mbid;
                        given[mbid] = recording;
                    }

                    continue;
                }

                if (holder == recording) continue;

                // Already held: the shop's row is folded into the holder, as a
                // merged MusicBrainz recording is.
                var into = holder.Value;

                await db.MediaFiles
                    .Where(file => file.RecordingId == recording)
                    .ExecuteUpdateAsync(update => update.SetProperty(file => file.RecordingId, into), cancellationToken)
                    .ConfigureAwait(false);

                await db.Tracks
                    .Where(track => track.RecordingId == recording)
                    .ExecuteUpdateAsync(update => update.SetProperty(track => track.RecordingId, into), cancellationToken)
                    .ConfigureAwait(false);

                folded.Add(recording);
            }

            release.ProviderCheckedUtc = now;

            if (links.Count > 0)
            {
                await events.AppendAsync(Matched(id, null, [], [.. links.Values], now), cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ForgetRecordingsAsync(db, folded, cancellationToken).ConfigureAwait(false);

            return false;
        }
    }

    /// <summary>
    /// Every file of a release the shop described, moved to the same disc and
    /// position on a MusicBrainz release. Saved by the caller.
    /// </summary>
    /// <returns>The files moved, and how many could not be because the release prints no such slot.</returns>
    internal static async Task<(List<Guid> Files, int Left)> MoveAsync(
        FonotecaDbContext db,
        ReleaseId shop,
        MusicBrainzRelease release,
        CancellationToken cancellationToken)
    {
        var writer = new ReleaseAttributionService.ReleaseWriter(db);
        var (written, group) = await writer.UpsertAsync(release, null, cancellationToken).ConfigureAwait(false);

        var slots = await db.Tracks
            .Where(track => track.ReleaseId == shop)
            .ToDictionaryAsync(track => track.Id, track => (track.DiscNumber, track.Position), cancellationToken)
            .ConfigureAwait(false);

        var files = await db.MediaFiles
            .Where(file => file.ReleaseId == shop)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var moved = new List<Guid>();

        foreach (var file in files)
        {
            if (file.TrackId is not { } track
                || !slots.TryGetValue(track, out var slot)
                || writer.TrackIdAt(written, slot.DiscNumber, slot.Position) is not { } seat
                || writer.RecordingIdAt(written, slot.DiscNumber, slot.Position) is not { } recording)
            {
                continue;
            }

            file.ReleaseId = written;
            file.ReleaseGroupId = group;
            file.TrackId = seat;
            file.RecordingId = recording;
            moved.Add(file.Id.Value);
        }

        return (moved, files.Count - moved.Count);
    }

    /// <summary>
    /// Removes releases a download minted that no file is filed under any more,
    /// with their slots, their credits and the recordings nothing else holds.
    /// </summary>
    internal static async Task ForgetAsync(FonotecaDbContext db, IReadOnlyCollection<ReleaseId> releases, CancellationToken cancellationToken)
    {
        var unheld = await db.Releases
            .Where(release => releases.Contains(release.Id)
                && release.Mbid == null
                && !db.MediaFiles.Any(file => file.ReleaseId == release.Id))
            .Select(release => new { release.Id, release.ReleaseGroupId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (unheld.Count == 0) return;

        var ids = unheld.Select(release => release.Id).ToList();
        var groups = unheld.Select(release => release.ReleaseGroupId).OfType<ReleaseGroupId>().ToList();

        var recordings = await db.Tracks
            .Where(track => ids.Contains(track.ReleaseId))
            .Select(track => track.RecordingId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Nullable on both sides: EF translates no member access on a typed id.
        List<ReleaseId?> credited = [.. ids.Select(id => (ReleaseId?)id)];

        await db.Tracks.Where(track => ids.Contains(track.ReleaseId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.ArtistCredits.Where(credit => credited.Contains(credit.ReleaseId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Releases.Where(release => ids.Contains(release.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await db.ReleaseGroups
            .Where(group => groups.Contains(group.Id)
                && group.Mbid == null
                && !db.Releases.Any(release => release.ReleaseGroupId == group.Id)
                && !db.MediaFiles.Any(file => file.ReleaseGroupId == group.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await ForgetRecordingsAsync(db, recordings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes MBID-less recordings no file and no track holds any more, with their credits.</summary>
    private static async Task ForgetRecordingsAsync(FonotecaDbContext db, List<RecordingId> recordings, CancellationToken cancellationToken)
    {
        if (recordings.Count == 0) return;

        var unheld = await db.Recordings
            .Where(recording => recordings.Contains(recording.Id)
                && recording.Mbid == null
                && !db.Tracks.Any(track => track.RecordingId == recording.Id)
                && !db.MediaFiles.Any(file => file.RecordingId == recording.Id))
            .Select(recording => recording.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (unheld.Count == 0) return;

        List<RecordingId?> held = [.. unheld.Select(id => (RecordingId?)id)];

        await db.ArtistCredits.Where(credit => held.Contains(credit.RecordingId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Relationships.Where(link => held.Contains(link.RecordingId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Recordings.Where(recording => unheld.Contains(recording.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DomainEvent Matched(ReleaseId shop, Mbid? release, IReadOnlyList<Guid> files, IReadOnlyList<Mbid> recordings, DateTimeOffset now) =>
        DomainEvent.Create(
            MatchedEvent,
            "release",
            shop.ToString(),
            "musicbrainz",
            now,
            JsonSerializer.Serialize(new MatchedPayload(release?.Value, files, [.. recordings.Select(mbid => mbid.Value)])));

    private sealed record DueRelease(ReleaseId Id, string? Barcode);

    private sealed record MatchedPayload(Guid? Release, IReadOnlyList<Guid> Files, IReadOnlyList<Guid> Recordings);
}
