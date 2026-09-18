using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
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
internal sealed class CatalogueWriter(FonotecaDbContext db, ICollection<Mbid> artistsTouched)
{
    public async Task<RecordingId> UpsertAsync(
        MusicBrainzRecording source,
        MusicBrainzWork? work,
        CancellationToken cancellationToken)
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

        return recording.Id;
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
