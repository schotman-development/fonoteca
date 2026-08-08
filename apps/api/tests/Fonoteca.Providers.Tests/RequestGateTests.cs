using System.Diagnostics;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The piece that decides whether this application is a good citizen or a
/// blocked address.
/// </summary>
/// <remarks>
/// Timed against the real clock with intervals in the tens of milliseconds.
/// A fake <see cref="TimeProvider"/> would make the arithmetic exact, but the
/// property that matters is "callers really do wait", and a fake clock is the
/// one way to pass that test while never waiting for anything.
///
/// Every assertion is a lower bound, never an upper one: a loaded CI machine is
/// allowed to be slow, and a test that fails because a delay took 60ms instead
/// of 50ms is a test nobody trusts.
/// </remarks>
public sealed class RequestGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task TheFirstCallerIsNotDelayed()
    {
        using var gate = new RequestGate(Interval);

        var elapsed = await TimeAsync(() => gate.WaitAsync().AsTask());

        Assert.True(
            elapsed < Interval,
            $"an idle gate should let the first caller straight through, waited {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task SuccessiveCallersAreSpacedByTheInterval()
    {
        using var gate = new RequestGate(Interval);

        await gate.WaitAsync();

        var elapsed = await TimeAsync(async () =>
        {
            await gate.WaitAsync();
            await gate.WaitAsync();
        });

        // Two more passes after the first: two intervals of waiting. The 1.5
        // leaves room for a coarse timer while still failing a gate that only
        // waited once.
        Assert.True(
            elapsed >= Interval * 1.5,
            $"two further passes should have cost two intervals, took {elapsed.TotalMilliseconds}ms");
    }

    /// <summary>
    /// The case the gate exists for. Four files identified in parallel must
    /// still leave the service being asked once per interval, not four times at
    /// once — which is what any per-call rate check would allow.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallersAreSerialised()
    {
        using var gate = new RequestGate(Interval);

        var elapsed = await TimeAsync(async () =>
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gate.WaitAsync().AsTask())));

        // Four passes, three of them waiting. 2.5 still fails anything that let
        // two through together.
        Assert.True(
            elapsed >= Interval * 2.5,
            $"four concurrent callers should have been spaced out, took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task AZeroIntervalGatesNothing()
    {
        using var gate = new RequestGate(TimeSpan.Zero);

        var elapsed = await TimeAsync(async () =>
        {
            for (var i = 0; i < 50; i++) await gate.WaitAsync();
        });

        Assert.Equal(TimeSpan.Zero, gate.MinimumInterval);
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"a disabled gate should be free, 50 passes took {elapsed.TotalMilliseconds}ms");
    }

    /// <summary>
    /// A cancelled wait must not leave the gate locked. It is a singleton for
    /// the life of the process, so one leaked permit is a deadlock that lasts
    /// until a restart.
    /// </summary>
    [Fact]
    public async Task CancellingAWaitReleasesTheGate()
    {
        using var gate = new RequestGate(TimeSpan.FromMilliseconds(500));

        // Takes the turn and puts the next one half a second out.
        await gate.WaitAsync();

        // Cancelled while it is inside the delay, holding the turn.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.WaitAsync(cancellation.Token));

        // If the abandoned wait had kept the turn, this would block until the
        // timeout and throw. It is a process-lifetime singleton, so one leaked
        // permit is a deadlock that only a restart clears.
        using var generous = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(generous.Token);
    }

    [Fact]
    public void ANegativeIntervalIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RequestGate(TimeSpan.FromMilliseconds(-1)));

    private static async Task<TimeSpan> TimeAsync(Func<Task> work)
    {
        var started = Stopwatch.GetTimestamp();
        await work();
        return Stopwatch.GetElapsedTime(started);
    }
}
