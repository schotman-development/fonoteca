using System.Runtime.CompilerServices;
using Fonoteca.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Data;

/// <summary>
/// <see cref="IEventLog"/> over the <c>DomainEvents</c> table.
/// </summary>
/// <remarks>
/// The table has existed since the initial migration with nothing writing to it.
/// This is its first writer, and the first use is the one ADR 0002 requires: the
/// undo journal for tag writes.
///
/// <b>Appends do not call <c>SaveChanges</c>.</b> They enlist in whatever the
/// caller's unit of work is, which is what makes "the file was tagged" and "the
/// catalogue says so" a single commit rather than two things that can disagree
/// after a crash. A journal that saved itself would be the more obvious design
/// and would quietly break that.
/// </remarks>
public sealed class EventLog(FonotecaDbContext db) : IEventLog
{
    private readonly FonotecaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();

        _db.DomainEvents.Add(domainEvent);
        return Task.CompletedTask;
    }

    public Task AppendAsync(
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();

        if (events.Count > 0) _db.DomainEvents.AddRange(events);

        return Task.CompletedTask;
    }

    /// <summary>
    /// One subject's history, oldest first — the order an undo has to replay in.
    /// </summary>
    /// <remarks>
    /// Ordered by <c>OccurredAtUtc</c> then <c>Id</c>. The timestamp alone is not
    /// a total order: a batch writes many events inside one millisecond, and
    /// PostgreSQL will return ties in whatever order it likes. Version-7 ids are
    /// time-ordered, so they break the tie the same way every time — which is
    /// what makes replaying a history deterministic rather than usually right.
    /// </remarks>
    public IAsyncEnumerable<DomainEvent> ReadSubjectAsync(
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectType);
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        return Read(subjectType, subjectId, cancellationToken);
    }

    private async IAsyncEnumerable<DomainEvent> Read(
        string subjectType,
        string subjectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = _db.DomainEvents
            .AsNoTracking()
            .Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var entry in query)
        {
            yield return entry;
        }
    }
}
