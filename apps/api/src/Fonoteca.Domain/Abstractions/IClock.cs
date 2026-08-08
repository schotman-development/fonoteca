namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Time, as a dependency.
/// </summary>
/// <remarks>
/// Scan scheduling, rate-limit windows, retry backoff and the undo journal all
/// reason about time. Injecting it keeps those rules testable without sleeping,
/// and keeps <c>DateTimeOffset.UtcNow</c> out of the domain.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. The only implementation that should exist outside tests.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
