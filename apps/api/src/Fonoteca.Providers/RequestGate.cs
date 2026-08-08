namespace Fonoteca.Providers;

/// <summary>
/// Lets one request through at a time, no faster than a fixed interval.
/// </summary>
/// <remarks>
/// Every service this project talks to publishes a rate limit and enforces it
/// with a ban rather than a queue: MusicBrainz asks for one request per second
/// and blocks the IP of anything that ignores it, AcoustID asks for three.
/// A library of 100,000 files is days of traffic at those rates, which means
/// the limiter is not a safety net that rarely fires — it is the thing that
/// sets how long identification takes, and it will be saturated for the whole
/// run.
///
/// So it waits rather than rejecting, which is the opposite of what a rate
/// limiter usually does. There is nothing sensible to do with "your lookup was
/// refused because you are going too fast" in a batch that has 99,000 files
/// left; being made to wait is the correct outcome.
///
/// It gates <b>attempts</b>, not calls. Wired in as a
/// <see cref="RateLimitedHandler"/> underneath the resilience pipeline, so a
/// retry after a 503 also waits its turn — a retry storm is precisely the
/// traffic pattern that gets an IP banned, and gating only the outermost call
/// would leave that door open.
///
/// A <see cref="SemaphoreSlim"/> held across the delay rather than a token
/// bucket: the queue is the point, and roughly-FIFO release keeps one unlucky
/// file from starving behind a hundred later ones.
/// </remarks>
public sealed class RequestGate : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly TimeProvider _time;
    private readonly TimeSpan _minimumInterval;

    /// <summary>Guarded by <see cref="_turn"/>; never read outside it.</summary>
    private DateTimeOffset _nextAllowed;

    /// <param name="minimumInterval">
    /// Least time between the start of one request and the start of the next.
    /// Zero disables the gate entirely, which is only reasonable against a
    /// mirror you host yourself.
    /// </param>
    /// <param name="time">Substituted in tests; the system clock otherwise.</param>
    public RequestGate(TimeSpan minimumInterval, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumInterval, TimeSpan.Zero);

        _minimumInterval = minimumInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The interval this gate enforces. Zero means it enforces nothing.</summary>
    public TimeSpan MinimumInterval => _minimumInterval;

    /// <summary>Waits until this caller may send. Returns immediately when nothing is owed.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        if (_minimumInterval <= TimeSpan.Zero) return;

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var owed = _nextAllowed - _time.GetUtcNow();
            if (owed > TimeSpan.Zero)
            {
                await Task.Delay(owed, _time, cancellationToken).ConfigureAwait(false);
            }

            // Measured from now rather than from _nextAllowed, so a gate that
            // has been idle for an hour does not hand out a burst of credit it
            // never earned.
            _nextAllowed = _time.GetUtcNow() + _minimumInterval;
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();
}

/// <summary>Makes every HTTP attempt on a client wait its turn at a <see cref="RequestGate"/>.</summary>
/// <remarks>
/// A handler rather than a call in the client, so it covers requests this
/// project does not build. <c>MetaBrainz.MusicBrainz</c> composes and sends its
/// own; the only place to gate those is the message pipeline underneath it.
/// </remarks>
public sealed class RateLimitedHandler(RequestGate gate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
