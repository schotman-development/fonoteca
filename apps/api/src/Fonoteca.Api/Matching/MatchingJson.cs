using System.Text.Json.Serialization;

namespace Fonoteca.Api.Matching;

/// <summary>
/// The event log's payload for one person-made identification decision.
/// </summary>
/// <remarks>
/// Stored as JSONB in <c>DomainEvents.PayloadJson</c>, which is opaque to the
/// log itself so a new event type never needs a migration.
///
/// It records the decision, not the evidence. The candidate set a person chose
/// from is recovered rather than stored — it is a live answer from AcoustID and
/// MusicBrainz, and it moves — so writing a snapshot of it here would create a
/// second, ageing copy of somebody else's data whose only use would be to
/// disagree with theirs. What is worth keeping is what was decided, about which
/// file, by whom, and when: the first two are here and the last two are on the
/// <c>DomainEvent</c> itself.
///
/// <see cref="Path"/> is descriptive rather than a key, for the reason the tag
/// journal gives: paths move, and <c>DomainEvent.SubjectId</c> is a
/// <c>varchar(200)</c> where a library path is up to 4096.
/// </remarks>
public sealed record RecordingDecisionPayload
{
    public required string Path { get; init; }

    /// <summary>The recording chosen. Null when the person rejected every candidate.</summary>
    public required Guid? Recording { get; init; }

    /// <summary>
    /// The cluster resolved for it and written into the file's tags.
    /// </summary>
    /// <remarks>
    /// Null on a rejection, and also when AcoustID named no cluster for a chosen
    /// recording — which is why it is nullable independently of
    /// <see cref="Recording"/> rather than travelling with it.
    /// </remarks>
    public required Guid? AcoustId { get; init; }

    /// <summary>The identification outcome the decision left on the file.</summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// The event log's payload for one person-made attribution decision.
/// </summary>
/// <remarks>
/// One of these per decision, not per file. Attribution's unit is a component —
/// a set of files that shared a candidate set and were refused together — and
/// the answer is given once about the whole set, so <see cref="Stamp"/> is the
/// subject and the file count travels as data.
///
/// <see cref="Decided"/> and <see cref="Files"/> differ whenever the chosen
/// release lists only part of the component, which is ordinary: a component is
/// not a promise that every file came from one album. Recording both is what
/// makes "why is this rip still on the worklist" answerable from the log alone.
/// </remarks>
public sealed record ComponentDecisionPayload
{
    /// <summary>The <c>ReleaseLookupUtc</c> ticks the component's files share.</summary>
    /// <remarks>A string for the reason the route is one — see <c>CatalogueEndpoints.Component</c>.</remarks>
    public required string Stamp { get; init; }

    /// <summary>The release chosen. Null when the person rejected every candidate.</summary>
    public required Guid? Release { get; init; }

    /// <summary>Files the decision actually filed or closed.</summary>
    public required int Decided { get; init; }

    /// <summary>Files the component held when it was answered.</summary>
    public required int Files { get; init; }

    /// <summary>The attribution outcome the decision left on them.</summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// The event log's payload for one person filing files under an album by hand.
/// </summary>
/// <remarks>
/// The only decision payload that records the <i>seating</i> and not just the
/// answer, and it has to. Everywhere else the pairing is reproducible — a
/// recording MBID and a release determine the slot, so the log can stay small
/// and the mapping can be recomputed. Here the pairing came out of a person's
/// head: none of these files holds a recording MBID, so nothing in the catalogue
/// or at MusicBrainz can say afterwards why file 7 was seated on track 8. Left
/// out, "who decided this and on what" would have no answer at all.
///
/// <see cref="Seats"/> is a flat list of <c>{fileId:N}@{disc}-{position}</c>
/// rather than a list of objects, because it is read by a person looking at one
/// row of JSONB and its whole job is to fit on a screen at album length.
/// </remarks>
public sealed record AlbumFilingPayload
{
    public required Guid Release { get; init; }

    /// <summary>The album's title as MusicBrainz gave it, so the log reads without a lookup.</summary>
    public required string Title { get; init; }

    public required int Filed { get; init; }

    /// <summary>Pairs naming a file that was no longer an open question.</summary>
    public required int Skipped { get; init; }

    /// <summary>Every seat taken, as <c>{fileId:N}@{disc}-{position}</c>.</summary>
    public required IReadOnlyList<string> Seats { get; init; }
}

/// <summary>
/// The event log's payload for one person dismissing a whole folder.
/// </summary>
/// <remarks>
/// The only trace this decision leaves. It writes no identity, no release and no
/// track — it takes questions away — so without the log there is nothing
/// afterwards to say a folder was ever answered, or by whom, or how many files
/// it cost. <see cref="Folder"/> is the whole path even though the event's
/// subject id is cut to two hundred characters, which is the point of it being
/// here as well.
/// </remarks>
public sealed record FolderUnreleasedPayload
{
    public required string Folder { get; init; }

    /// <summary>Files that had an open question and now do not.</summary>
    public required int Closed { get; init; }
}

/// <summary>
/// The event log's payload for one person reopening a whole folder.
/// </summary>
/// <remarks>
/// The same argument as the dismissal above, in the other direction: this
/// decision <i>removes</i> identities that a pass wrote, so afterwards nothing
/// in the catalogue says they were ever there or who took them away. The count
/// is the only measure of what it cost.
/// </remarks>
public sealed record FolderReopenedPayload
{
    public required string Folder { get; init; }

    /// <summary>Files that gave up a derived identity or album.</summary>
    public required int Reopened { get; init; }
}

/// <summary>
/// What was contributed to AcoustID, and what it acknowledged.
/// </summary>
/// <remarks>
/// The only outbound claim this application makes, so this entry is the only
/// record that it happened — the catalogue keeps a stamp per file and nothing
/// else, and AcoustID's own copy is not readable from here. The submission ids
/// are kept for that reason: they are the handle on
/// <c>/v2/submission_status</c>, which nothing here calls and a person may.
/// </remarks>
public sealed record FingerprintContributionPayload
{
    public required Guid Release { get; init; }

    public required string Title { get; init; }

    /// <summary>Files whose fingerprint was sent.</summary>
    public required int Submitted { get; init; }

    /// <summary>AcoustID's own ids for the queued submissions.</summary>
    public required IReadOnlyList<long> Submissions { get; init; }
}

/// <summary>
/// Source-generated, matching <c>TaggingJson</c>.
/// </summary>
/// <remarks>
/// One decision payload is serialised per answer — a handful an afternoon — but
/// <see cref="AcoustIdEvidence"/> is serialised once per file in a library of
/// 100,000, inside a pass that is already spending 340ms a row at the rate
/// limit. That one earns the generator on throughput; the rest are here so there
/// is one way of writing a stored document rather than two.
///
/// <c>RecordingCandidatesResponse</c> is listed because the candidate cache
/// stores the wire shape verbatim — a cache hit is then a read and a
/// deserialise, with no second type to keep in step with the first.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(RecordingDecisionPayload))]
[JsonSerializable(typeof(ComponentDecisionPayload))]
[JsonSerializable(typeof(AlbumFilingPayload))]
[JsonSerializable(typeof(FolderUnreleasedPayload))]
[JsonSerializable(typeof(FolderReopenedPayload))]
[JsonSerializable(typeof(FingerprintContributionPayload))]
[JsonSerializable(typeof(AcoustIdEvidence))]
[JsonSerializable(typeof(Fonoteca.Api.Endpoints.RecordingCandidatesResponse))]
[JsonSerializable(typeof(Fonoteca.Api.Endpoints.ComponentCandidatesResponse))]
public sealed partial class MatchingJson : JsonSerializerContext;
