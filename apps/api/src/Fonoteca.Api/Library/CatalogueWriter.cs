using System.Linq.Expressions;
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
        recording.Genres = EnrichmentService.Joined(source.Genres ?? []);
        recording.Isrcs = Isrcs(source.Isrcs);
        recording.DescribedUtc = StoreTime.ToStorePrecision(clock.UtcNow);

        await ApplyContributionsAsync(recording.Id, null, source.Relations, cancellationToken).ConfigureAwait(false);
        await ApplySessionsAsync(recording.Id, source.Sessions ?? [], cancellationToken).ConfigureAwait(false);

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
    /// Puts the file's folder back through attribution, where the attribution
    /// worklist would take the file: the rule's, and those whose album alone a
    /// person or agent named. A file a person seated keeps its stamp.
    /// </summary>
    internal static void ReopenAttribution(MediaFile file)
    {
        if (file.ReleaseDecidedUtc is null
            || file.AttributionOutcome is ReleaseAttributionOutcome.AlbumByPerson or ReleaseAttributionOutcome.AlbumByAgent)
        {
            file.ReleaseLookupUtc = null;
        }
    }

    /// <summary>
    /// A heart on a song, and a listener's tag on it, go with its files to the
    /// recording row that now holds them, wherever the old row is about to go:
    /// a merge, and the rows a download minted that MusicBrainz names. Saved by
    /// the caller.
    /// </summary>
    /// <remarks>
    /// Once per account, and once per tag: where the new row has it already,
    /// the old row's cascades away with it. The change tracker is asked as
    /// well as the database, because two of an album's rows can fold into one
    /// before the save. Every account's, so past the query filters.
    /// </remarks>
    internal static async Task CarryLovesAsync(FonotecaDbContext db, RecordingId from, RecordingId to, CancellationToken cancellationToken)
    {
        if (from == to) return;

        var taken = await LovedByAsync(db, love => love.RecordingId == to, cancellationToken).ConfigureAwait(false);

        foreach (var love in await db.Loves.IgnoreQueryFilters().Where(love => love.RecordingId == from).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (taken.Add(love.AccountId)) love.RecordingId = to;
        }

        HashSet<Guid> tagged =
        [
            .. await db.SongTags.IgnoreQueryFilters().Where(tag => tag.RecordingId == to).Select(tag => tag.TagId).ToListAsync(cancellationToken).ConfigureAwait(false),
            .. db.SongTags.Local.Where(tag => tag.RecordingId == to).Select(tag => tag.TagId),
        ];

        foreach (var tag in await db.SongTags.IgnoreQueryFilters().Where(tag => tag.RecordingId == from).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (tagged.Add(tag.TagId)) tag.RecordingId = to;
        }
    }

    /// <summary>The accounts already loving what <paramref name="loves"/> names, tracked or saved.</summary>
    private static async Task<HashSet<Guid?>> LovedByAsync(
        FonotecaDbContext db,
        Expression<Func<Love, bool>> loves,
        CancellationToken cancellationToken)
    {
        var saved = await db.Loves.IgnoreQueryFilters().Where(loves).Select(love => love.AccountId).ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. saved, .. db.Loves.Local.Where(loves.Compile()).Select(love => love.AccountId)];
    }

    /// <summary>
    /// <see cref="CarryLovesAsync"/> for a file leaving a recording a download
    /// minted, the one kind its move makes go. A recording MusicBrainz knows
    /// stays, and keeps its heart for its other copies.
    /// </summary>
    internal static async Task CarryMintedLovesAsync(FonotecaDbContext db, RecordingId from, RecordingId to, CancellationToken cancellationToken)
    {
        if (await db.Recordings.AnyAsync(recording => recording.Id == from && recording.Mbid == null, cancellationToken).ConfigureAwait(false))
        {
            await CarryLovesAsync(db, from, to, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An album's heart goes with its files from a release group a download
    /// minted to the one they are filed under now, as a song's does. Saved by
    /// the caller.
    /// </summary>
    internal static async Task CarryAlbumLovesAsync(FonotecaDbContext db, ReleaseGroupId from, ReleaseGroupId to, CancellationToken cancellationToken)
    {
        if (from == to
            || !await db.ReleaseGroups.AnyAsync(group => group.Id == from && group.Mbid == null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var taken = await LovedByAsync(db, love => love.ReleaseGroupId == to, cancellationToken).ConfigureAwait(false);

        foreach (var love in await db.Loves.IgnoreQueryFilters().Where(love => love.ReleaseGroupId == from).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (taken.Add(love.AccountId)) love.ReleaseGroupId = to;
        }
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
            ReopenAttribution(file);
        }

        var tracks = await db.Tracks
            .Where(t => t.RecordingId == stale.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var track in tracks)
        {
            db.Entry(track).Property(t => t.RecordingId).CurrentValue = survivor.Id;
        }

        await CarryLovesAsync(db, stale.Id, survivor.Id, cancellationToken).ConfigureAwait(false);

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

        work.ParentId = source.ParentId is { } parent
            ? (await UpsertParentAsync(parent, source.ParentTitle, cancellationToken).ConfigureAwait(false)).Id
            : null;

        Describe(work, source, underItsParent: work.ParentId is not null);

        await ApplyContributionsAsync(null, work.Id, source.Relations, cancellationToken).ConfigureAwait(false);

        return work;
    }

    /// <summary>
    /// What a parent work is itself a part of, from its own lookup: one level of the climb.
    /// </summary>
    /// <remarks>
    /// Only the parent changes: the row stays the stub it was minted as, with no
    /// writers of its own. A work naming itself as its own parent is read as
    /// having none.
    /// </remarks>
    public async Task PartOfAsync(Work row, MusicBrainzWork source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(source);

        row.ParentId = source.ParentId is { } parent && parent != row.Mbid
            ? (await UpsertParentAsync(parent, source.ParentTitle, cancellationToken).ConfigureAwait(false)).Id
            : null;

        Describe(row, source, underItsParent: row.ParentId is not null);
    }

    /// <summary>
    /// What a work's own lookup says that only the tags read: its languages,
    /// and its place among the parts of the parent this same answer names —
    /// and its type, its key and the years its composer link states, which
    /// Subsonic serves. A parent's type too, which the climb used to drop.
    /// </summary>
    /// <remarks>
    /// Stamped wherever a work's whole document arrives, so one the recording
    /// stage or the climb has just fetched is not asked again by the stage
    /// that describes the rest.
    /// </remarks>
    public void Describe(Work row, MusicBrainzWork source, bool underItsParent)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(source);

        row.LatinTitle = LatinNames.Of(row.Title, source.Aliases);
        row.Type = source.Type;
        row.Key = source.Key;
        row.Languages = EnrichmentService.Joined(source.Languages ?? []);
        row.Position = underItsParent ? source.Position : null;
        row.PartCount = source.PartCount > 0 ? source.PartCount : null;
        row.PartsTyped = source.PartsTyped;

        var composed = Performance.Composed(source.Relations.Select(relation =>
            (relation.Type, relation.Begin?.ToString(), relation.End?.ToString())));
        row.ComposedBegin = composed?.From?.ToString();
        row.ComposedEnd = composed?.To?.ToString();
        row.DescribedUtc = StoreTime.ToStorePrecision(clock.UtcNow);
    }

    /// <summary>
    /// The work a movement belongs to, minted from the stub the <c>parts</c> relation carries.
    /// </summary>
    /// <remarks>
    /// Never described on its own — that is a lookup per parent for a title the
    /// stub already holds — so a parent row has no writers, and its own parent
    /// is the parent stage's to find (<see cref="PartOfAsync"/>). Its title is
    /// refreshed from the stub, and an existing row keeps whatever a fuller
    /// description or that stage gave it.
    /// </remarks>
    private async Task<Work> UpsertParentAsync(Mbid id, string? title, CancellationToken cancellationToken)
    {
        var parent = db.Works.Local.FirstOrDefault(w => w.Mbid == id)
            ?? await db.Works.FirstOrDefaultAsync(w => w.Mbid == id, cancellationToken).ConfigureAwait(false);

        if (parent is null)
        {
            parent = new Work { Id = WorkId.New(), Title = title ?? string.Empty, Mbid = id };
            db.Works.Add(parent);
        }
        else if (title is not null)
        {
            parent.Title = title;
        }

        return parent;
    }

    /// <summary>
    /// Every artist link MusicBrainz states on this recording or work, replacing the last answer.
    /// </summary>
    /// <remarks>
    /// Replaced rather than merged, for <see cref="ApplyCreditsAsync"/>'s reason:
    /// a rerun over unchanged data produces the identical set and a rerun after a
    /// correction produces the corrected one. No artist row is minted; see
    /// <see cref="Contribution"/>.
    ///
    /// The rows this scope added earlier go too: two files of one recording, or
    /// two movements of one work, can be written in a single save, and EF's query
    /// sees only what the database holds.
    /// </remarks>
    private async Task ApplyContributionsAsync(
        RecordingId? recording,
        WorkId? work,
        IReadOnlyList<MusicBrainzRelation> relations,
        CancellationToken cancellationToken)
    {
        var stale = recording is { } recordingId
            ? db.Contributions.Where(c => c.RecordingId == recordingId)
            : db.Contributions.Where(c => c.WorkId == work);

        db.Contributions.RemoveRange(await stale.ToListAsync(cancellationToken).ConfigureAwait(false));
        db.Contributions.RemoveRange(db.Contributions.Local
            .Where(c => recording is not null ? c.RecordingId == recording : c.WorkId == work)
            .ToList());

        for (var position = 0; position < relations.Count; position++)
        {
            var relation = relations[position];

            db.Contributions.Add(new Contribution
            {
                Id = Guid.CreateVersion7(),
                RecordingId = recording,
                WorkId = work,
                Type = Clamp(relation.Type, 100),
                Attributes = relation.Attributes is { Count: > 0 } attributes
                    ? Clamp(string.Join(", ", attributes), 500)
                    : relation.Attribute,
                ArtistMbid = relation.ArtistId,
                Name = Clamp(relation.Name, 1000),
                Position = position,
                Begin = relation.Begin?.ToString(),
                End = relation.End?.ToString(),
            });
        }
    }

    /// <summary>
    /// Every place, area and performance link MusicBrainz states on this recording, replacing the last answer.
    /// </summary>
    /// <remarks><see cref="ApplyContributionsAsync"/>'s shape and reasons.</remarks>
    private async Task ApplySessionsAsync(
        RecordingId recording,
        IReadOnlyList<MusicBrainzSession> sessions,
        CancellationToken cancellationToken)
    {
        db.RecordingSessions.RemoveRange(await db.RecordingSessions
            .Where(session => session.RecordingId == recording)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));
        db.RecordingSessions.RemoveRange(db.RecordingSessions.Local
            .Where(session => session.RecordingId == recording)
            .ToList());

        for (var position = 0; position < sessions.Count; position++)
        {
            var session = sessions[position];

            db.RecordingSessions.Add(new RecordingSession
            {
                Id = Guid.CreateVersion7(),
                RecordingId = recording,
                Type = Clamp(session.Type, 100),
                Mbid = session.TargetId,
                Name = Clamp(session.Name, 1000),
                Begin = session.Begin?.ToString(),
                End = session.End?.ToString(),
                Position = position,
            });
        }
    }

    /// <summary>The ISRCs as the column stores them, cut on a separator rather than mid-code.</summary>
    private static string? Isrcs(IReadOnlyList<string> isrcs)
    {
        var kept = new List<string>();
        var length = 0;

        foreach (var isrc in isrcs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cost = isrc.Length + (kept.Count == 0 ? 0 : 2);
            if (length + cost > 1000) break;

            kept.Add(isrc);
            length += cost;
        }

        return kept.Count == 0 ? null : string.Join(", ", kept);
    }

    /// <summary>
    /// Clamped on length, as the columns are, because an overrun throws out of
    /// <c>SaveChangesAsync</c> and rolls the stage's stamp back with it.
    /// </summary>
    private static string Clamp(string value, int length) =>
        value.Length <= length ? value : value[..length];

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
    internal static string RoleName(CreditRole role) => role switch
    {
        CreditRole.Conductor => "conductor",
        CreditRole.Ensemble => "ensemble",
        CreditRole.Writer => "composer",
        _ => throw new InvalidOperationException($"Role {role} is not a relationship."),
    };
}
