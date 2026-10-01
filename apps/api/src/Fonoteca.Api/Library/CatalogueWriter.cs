using System.Text.Json;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// Writes one recording's graph into the catalogue, creating only what is missing.
/// </summary>
/// <remarks>
/// Every upsert is keyed on the MBID, which carries a unique filtered index
/// on all four entity types — so a rerun over an unchanged library converges
/// on the same rows instead of duplicating them, and two files that resolve
/// to the same recording share it, which is the whole point of the
/// <c>Recording ↔ MediaFile</c> split.
///
/// It reads through the scope's <c>DbContext</c> rather than keeping its own
/// identity map, so rows created for an earlier file in the same
/// <c>SaveChanges</c> are found by the later one.
///
/// <b>Two callers, and that is why it is a file of its own.</b> The enrichment
/// pass writes this graph for every file it links, and a person answering an
/// open question writes exactly the same graph for one file — the difference
/// between the two is which evidence chose the recording, not what a chosen
/// recording turns into. A second copy of these upserts would be a second
/// chance to get the credit split or the MBID keying wrong.
/// </remarks>
/// <param name="artistsTouched">
/// Collects the MBID of every artist this writer credited. The pass reports the
/// count in its summary; a single decision has nothing to report and passes a
/// set it discards. A parameter rather than a field on the writer because the
/// lifetime differs — the pass accumulates across a whole run.
/// </param>
/// <param name="clock">Stamps the journal entry a merge leaves.</param>
internal sealed class CatalogueWriter(FonotecaDbContext db, ICollection<Mbid> artistsTouched, IClock clock)
{
    /// <summary>The journal entry a merged recording leaves, subject the MBID that went away.</summary>
    public const string MergedEvent = "catalogue.recording.merged";

    /// <param name="asked">
    /// The MBID the caller asked MusicBrainz about, where it had one. WS/2 answers
    /// a merged MBID with a redirect to the recording it was merged into, so
    /// <paramref name="source"/> can carry a different id — and when it does, the
    /// catalogue's row for the asked one is stale and is folded into this one.
    /// </param>
    public async Task<RecordingId> UpsertAsync(
        MusicBrainzRecording source,
        MusicBrainzWork? work,
        CancellationToken cancellationToken,
        Mbid? asked = null)
    {
        var workRow = work is null
            ? null
            : await UpsertWorkAsync(work, cancellationToken).ConfigureAwait(false);

        var recording = await db.Recordings
            .FirstOrDefaultAsync(r => r.Mbid == source.Id, cancellationToken)
            .ConfigureAwait(false);

        if (recording is null)
        {
            recording = new Recording
            {
                Id = RecordingId.New(),
                Title = source.Title,
                Mbid = source.Id,
            };

            db.Recordings.Add(recording);
        }
        else
        {
            recording.Title = source.Title;
        }

        recording.Duration = source.Length;
        recording.WorkId = workRow?.Id;

        var credits = PrimaryCredits.From(source, work);

        foreach (var credit in credits) artistsTouched.Add(credit.ArtistId);

        await ApplyCreditsAsync(recording, workRow, credits, cancellationToken).ConfigureAwait(false);

        if (asked is { } old && old != source.Id)
        {
            await AbsorbAsync(old, recording, cancellationToken).ConfigureAwait(false);
        }

        return recording.Id;
    }

    /// <summary>
    /// Moves everything that hangs off the row for a merged MBID onto the
    /// recording it was merged into, and removes that row.
    /// </summary>
    /// <remarks>
    /// <b>A merge renames an answer; it does not change it.</b> Whoever linked a
    /// file — the rule, a person, an agent — said "this audio is recording X",
    /// and MusicBrainz merging X into Y says X and Y were always one recording.
    /// So no outcome and no decided stamp is touched: a person's answer stays a
    /// person's. The journal entry, by the system, is what records that the id
    /// moved and which files it moved.
    ///
    /// <b>The old row cannot simply take the new MBID.</b> The survivor usually
    /// exists already — a stored edition printed it — and <c>IX_Recordings_Mbid</c>
    /// is unique, so the references move and the old row goes.
    ///
    /// <b>Tracks move in place, never delete-and-add.</b> <c>MediaFiles.TrackId</c>
    /// is <c>ON DELETE SET NULL</c>, so a fresh track id would silently strip the
    /// position off every file seated on it. <c>Tracks.RecordingId</c> is also
    /// <c>ON DELETE CASCADE</c>, which is why the tracks are repointed before the
    /// old row is removed rather than after.
    ///
    /// The old row's credits and relationships are dropped rather than moved: the
    /// survivor's were just rebuilt from MusicBrainz, and moving them would bill
    /// every artist twice.
    ///
    /// Moved files the attribution worklist would take — the rule's, and those
    /// whose album alone a person or agent named — lose <c>ReleaseLookupUtc</c>,
    /// which puts their folder back through attribution: a file whose recording
    /// was merged away is on no stored edition's track list and could not prove a
    /// pressing. A file a person seated keeps its stamp; its track moved with it.
    /// </remarks>
    private async Task AbsorbAsync(Mbid old, Recording survivor, CancellationToken cancellationToken)
    {
        var stale = await db.Recordings
            .FirstOrDefaultAsync(r => r.Mbid == old, cancellationToken)
            .ConfigureAwait(false);

        if (stale is null || stale.Id == survivor.Id) return;

        var files = await db.MediaFiles
            .Where(f => f.RecordingId == stale.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var file in files)
        {
            file.RecordingId = survivor.Id;

            if (file.ReleaseDecidedUtc is null
                || file.AttributionOutcome is ReleaseAttributionOutcome.AlbumByPerson or ReleaseAttributionOutcome.AlbumByAgent)
            {
                file.ReleaseLookupUtc = null;
            }
        }

        var tracks = await db.Tracks
            .Where(t => t.RecordingId == stale.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var track in tracks)
        {
            db.Entry(track).Property(t => t.RecordingId).CurrentValue = survivor.Id;
        }

        db.ArtistCredits.RemoveRange(await db.ArtistCredits
            .Where(c => c.RecordingId == stale.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        db.Relationships.RemoveRange(await db.Relationships
            .Where(r => r.RecordingId == stale.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        db.Recordings.Remove(stale);

        var payload = JsonSerializer.Serialize(new
        {
            from = old.Value,
            to = survivor.Mbid?.Value,
            files = files.Select(f => f.Id.Value).ToList(),
            tracks = tracks.Count,
        });

        await new EventLog(db)
            .AppendAsync(
                DomainEvent.Create(
                    MergedEvent,
                    RelationshipTargets.Recording,
                    old.Value.ToString(),
                    SystemCallerContext.SystemId,
                    StoreTime.ToStorePrecision(clock.UtcNow),
                    payload),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Work> UpsertWorkAsync(
        MusicBrainzWork source,
        CancellationToken cancellationToken)
    {
        var work = await db.Works
            .FirstOrDefaultAsync(w => w.Mbid == source.Id, cancellationToken)
            .ConfigureAwait(false);

        if (work is null)
        {
            work = new Work
            {
                Id = WorkId.New(),
                Title = source.Title,
                Mbid = source.Id,
            };

            db.Works.Add(work);
        }
        else
        {
            work.Title = source.Title;
        }

        work.Type = source.Type;
        return work;
    }

    /// <summary>
    /// Billed credits become <c>ArtistCredit</c>; everything else becomes a
    /// <c>Relationship</c>.
    /// </summary>
    /// <remarks>
    /// The split is not cosmetic. <see cref="ArtistCredit"/>'s
    /// <c>Position</c> and <c>JoinPhrase</c> describe a printed billing line —
    /// "Beth Hart &amp; Joe Bonamassa" — and inserting a conductor into that
    /// sequence corrupts the meaning for every consumer that reads it back as
    /// a credit line. Conductors and ensembles are typed links to the
    /// recording; writers are typed links to the <i>work</i>, which is where
    /// MusicBrainz puts them and what makes "everything this composer wrote"
    /// answerable across performances.
    ///
    /// Existing rows for this recording are replaced rather than merged. A
    /// second pass over unchanged data produces the identical set, and a pass
    /// after a MusicBrainz correction produces the corrected one — whereas
    /// merging would accumulate every credit the recording ever had.
    /// </remarks>
    private async Task ApplyCreditsAsync(
        Recording recording,
        Work? work,
        IReadOnlyList<PrimaryCredit> credits,
        CancellationToken cancellationToken)
    {
        var stale = await db.ArtistCredits
            .Where(c => c.RecordingId == recording.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        db.ArtistCredits.RemoveRange(stale);

        var staleLinks = await db.Relationships
            .Where(r => r.RecordingId == recording.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (work is not null)
        {
            staleLinks.AddRange(await db.Relationships
                .Where(r => r.WorkId == work.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        db.Relationships.RemoveRange(staleLinks);

        foreach (var credit in credits)
        {
            var artist = await UpsertArtistAsync(credit, cancellationToken).ConfigureAwait(false);

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

            // A writer is a fact about the composition, not about this
            // performance of it — so it hangs off the work when there is one.
            // Without a work there is nowhere else to put it, and the
            // recording is the honest second choice.
            var toWork = credit.Role == CreditRole.Writer && work is not null;

            db.Relationships.Add(new Relationship
            {
                Id = Guid.CreateVersion7(),
                SourceType = RelationshipTargets.Artist,
                SourceId = artist.Id.Value,
                TargetType = toWork ? RelationshipTargets.Work : RelationshipTargets.Recording,
                TargetId = toWork ? work!.Id.Value : recording.Id.Value,
                Type = RoleName(credit.Role),
                Attribute = credit.ArtistType,
                ArtistId = artist.Id,
                WorkId = toWork ? work!.Id : null,
                RecordingId = toWork ? null : recording.Id,
            });
        }
    }

    private async Task<Artist> UpsertArtistAsync(
        PrimaryCredit credit,
        CancellationToken cancellationToken)
    {
        var artist = await db.Artists
            .FirstOrDefaultAsync(a => a.Mbid == credit.ArtistId, cancellationToken)
            .ConfigureAwait(false);

        if (artist is null)
        {
            artist = new Artist
            {
                Id = ArtistId.New(),
                Name = credit.Name,
                Mbid = credit.ArtistId,
            };

            db.Artists.Add(artist);
        }

        // The sort name is what the artist list orders by, and a credit that
        // carries one is better evidence than the last one that did not.
        // The display name is left alone once set: a credit line prints what
        // that release printed, and overwriting the canonical name with it
        // would rename "David Bowie" to "Bowie" on a sleeve's say-so.
        artist.SortName ??= credit.SortName;
        artist.Type ??= credit.ArtistType;
        artist.Disambiguation ??= credit.Disambiguation;

        return artist;
    }

    /// <summary>
    /// The relationship type as stored, which is the role rather than
    /// MusicBrainz's own relation name.
    /// </summary>
    /// <remarks>
    /// Deliberate narrowing. MusicBrainz distinguishes "performing orchestra"
    /// from a "performer" relation on an artist typed Orchestra, and
    /// <see cref="PrimaryCredits"/> has already decided those mean the same
    /// thing; storing the raw name would make the browse query re-derive that
    /// decision in SQL, in a second place, where it would drift.
    /// </remarks>
    private static string RoleName(CreditRole role) => role switch
    {
        CreditRole.Conductor => "conductor",
        CreditRole.Ensemble => "ensemble",
        CreditRole.Writer => "composer",
        _ => throw new InvalidOperationException($"Role {role} is not a relationship."),
    };
}
