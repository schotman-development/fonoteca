namespace Fonoteca.Domain.Events;

/// <summary>
/// One entry in the append-only event log.
/// </summary>
/// <remarks>
/// A single log, three current uses and one future one:
///
///   - the <b>tag undo journal</b> — what a file's tags were before a write
///   - the <b>scan audit trail</b> — what the scanner saw and decided
///   - <b>operator actions</b> — who asked for what, and when
///   - later, <b>listening history</b>, which becomes a new event type rather
///     than new infrastructure
///
/// Building this once now is the cheap half of supporting playback later; see
/// the plan's "Designed for, not built" section.
///
/// Events are never updated or deleted. Corrections are new events.
/// </remarks>
public sealed record DomainEvent
{
    public required Guid Id { get; init; }

    /// <summary>Dotted event name, e.g. <c>tagging.write.committed</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The kind of thing this concerns, e.g. <c>recording</c>, <c>file</c>.</summary>
    public required string SubjectType { get; init; }

    /// <summary>Identifier of the thing this concerns.</summary>
    public required string SubjectId { get; init; }

    /// <summary>From <c>ICallerContext.ActorId</c>: who caused this.</summary>
    public required string ActorId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>
    /// Event-specific body, stored as JSONB. Kept opaque to the log itself so a
    /// new event type never needs a migration.
    /// </summary>
    public required string PayloadJson { get; init; }

    /// <summary>
    /// Groups events belonging to one logical operation — every file touched by
    /// a single batch tag edit shares a correlation id, which is what makes the
    /// batch reversible as a unit.
    /// </summary>
    public string? CorrelationId { get; init; }

    public static DomainEvent Create(
        string type,
        string subjectType,
        string subjectId,
        string actorId,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        string? correlationId = null) =>
        new()
        {
            // Version 7: time-ordered, so the log's primary key stays
            // insert-friendly instead of scattering writes across the B-tree.
            Id = Guid.CreateVersion7(),
            Type = type,
            SubjectType = subjectType,
            SubjectId = subjectId,
            ActorId = actorId,
            OccurredAtUtc = occurredAtUtc,
            PayloadJson = payloadJson,
            CorrelationId = correlationId,
        };
}

/// <summary>Append-only writer for <see cref="DomainEvent"/>.</summary>
public interface IEventLog
{
    Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default);

    Task AppendAsync(
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken = default);

    /// <summary>Read a subject's history, oldest first. The undo journal reads this.</summary>
    IAsyncEnumerable<DomainEvent> ReadSubjectAsync(
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken = default);
}
