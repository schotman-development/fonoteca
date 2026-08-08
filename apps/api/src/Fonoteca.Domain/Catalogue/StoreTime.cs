namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Timestamps, rounded to what the database will actually keep.
/// </summary>
/// <remarks>
/// PostgreSQL's <c>timestamptz</c> stores microseconds; .NET keeps 100-nanosecond
/// ticks. Store a filesystem timestamp untruncated and the value that comes back
/// is not the value that went in, so the next scan finds every file modified —
/// 100,000 spurious updates, and every hash, fingerprint and identification in
/// the catalogue discarded along with them. A "nothing changed" rescan that
/// never converges.
///
/// It lives here, in the domain, because two passes now depend on it agreeing
/// with itself. The scan floors a file's modification time on the way in; the
/// tag writer floors the <i>new</i> modification time after it has replaced the
/// file, so that the next scan sees the file it just wrote as unchanged. Two
/// copies of this rule that drifted apart would put the two passes into a loop,
/// each undoing the other's work, and the symptom would appear nowhere near
/// either of them.
/// </remarks>
public static class StoreTime
{
    /// <summary>The same instant, truncated to whole microseconds in UTC.</summary>
    public static DateTimeOffset ToStorePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
