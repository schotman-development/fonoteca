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
[JsonSerializable(typeof(AcoustIdEvidence))]
[JsonSerializable(typeof(Fonoteca.Api.Endpoints.RecordingCandidatesResponse))]
[JsonSerializable(typeof(Fonoteca.Api.Endpoints.ComponentCandidatesResponse))]
public sealed partial class MatchingJson : JsonSerializerContext;
