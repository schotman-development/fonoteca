using System.Diagnostics.CodeAnalysis;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Durable background work.
/// </summary>
/// <remarks>
/// Hangfire backs this today. The interface exists because Wolverine's durable
/// outbox is the stronger reliability story and is the likely successor once
/// job semantics are concrete — keeping feature code off the Hangfire API makes
/// that a one-adapter change rather than a rewrite.
///
/// The contract deliberately requires an <see cref="IJob.IdempotencyKey"/>.
/// Delivery is at-least-once; a full library scan that runs twice must not
/// produce two catalogues, and a tag write that is retried must not apply
/// twice.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification =
        "CA1711 guards against types that look like collections but are not. This is a " +
        "job queue in the ordinary sense of the word, and every alternative the rule would " +
        "accept (Dispatcher, Scheduler) describes it less accurately.")]
public interface IJobQueue
{
    /// <summary>Enqueue for immediate execution. Returns the queue's handle for the job.</summary>
    Task<string> EnqueueAsync<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : IJob;

    Task<string> ScheduleAsync<TJob>(
        TJob job,
        DateTimeOffset runAt,
        CancellationToken cancellationToken = default)
        where TJob : IJob;

    /// <summary>Request cancellation. Best-effort: a job already running decides when to stop.</summary>
    Task<bool> CancelAsync(string jobHandle, CancellationToken cancellationToken = default);
}

/// <summary>A unit of durable background work.</summary>
public interface IJob
{
    /// <summary>
    /// Stable key identifying the work, not the attempt. Two enqueues with the
    /// same key are the same job. This is what makes at-least-once delivery
    /// survivable.
    /// </summary>
    string IdempotencyKey { get; }
}
