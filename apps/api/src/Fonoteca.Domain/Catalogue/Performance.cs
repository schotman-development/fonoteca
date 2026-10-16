using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// When and where a recording was performed, and when its piece was written,
/// read off the dates MusicBrainz puts on its links.
/// </summary>
/// <remarks>
/// <b>A span, never a date.</b> The earliest begin and the latest end, each as
/// partial as MusicBrainz states it, so "1962-05 to 1962-10" stays that and a
/// piece "begun 1804, finished 1808" is not narrowed to either. A side nobody
/// dated is absent rather than borrowed from the other.
///
/// <b>The performance is the session's and the players'.</b> The place and
/// area links and the work's <c>performance</c> link count, and of the people
/// only the players and the conductor: a producer's or a mastering engineer's
/// dates can be a reissue's, decades after the music was played.
/// </remarks>
public static class Performance
{
    private static readonly HashSet<string> SessionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "recorded at", "recorded in", "performance",
    };

    /// <summary>The span the recording was made in, and the places, a place's name over its area's.</summary>
    /// <param name="sessions">Its <see cref="RecordingSession"/> rows, in their stored order.</param>
    /// <param name="links">Its <see cref="Contribution"/> rows.</param>
    public static (DateSpan? Span, IReadOnlyList<string> Places) Recorded(
        IEnumerable<(string Type, string Name, string? Begin, string? End)> sessions,
        IEnumerable<(string Type, string? Attributes, string? Begin, string? End)> links)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(links);

        var held = sessions.Where(session => SessionTypes.Contains(session.Type)).ToList();

        var played = links.Where(link => CreditTags.Role(link.Type, link.Attributes)?.Tag
            is CatalogueTags.Performer or CatalogueTags.Conductor);

        var span = Span(held.Select(session => (session.Begin, session.End))
            .Concat(played.Select(link => (link.Begin, link.End))));

        List<string> NamesOf(string type) =>
        [
            .. held
                .Where(session => string.Equals(session.Type, type, StringComparison.OrdinalIgnoreCase))
                .Select(session => session.Name)
                .Distinct(StringComparer.Ordinal),
        ];

        var places = NamesOf("recorded at");

        return (span, places.Count > 0 ? places : NamesOf("recorded in"));
    }

    /// <summary>The years a piece was written, off its composers' links.</summary>
    /// <param name="links">The piece's <see cref="Contribution"/> rows.</param>
    public static DateSpan? Composed(IEnumerable<(string Type, string? Begin, string? End)> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        return Span(links
            .Where(link => string.Equals(link.Type, "composer", StringComparison.OrdinalIgnoreCase))
            .Select(link => (link.Begin, link.End)));
    }

    /// <summary>The earliest begin and the latest end; null where neither is stated.</summary>
    /// <remarks>
    /// A coarser date reaches further: "1962" begins before "1962-05-10" and
    /// ends after "1962-10-31", since it may mean any day of the year.
    /// </remarks>
    public static DateSpan? Span(IEnumerable<(string? Begin, string? End)> dated)
    {
        ArgumentNullException.ThrowIfNull(dated);

        ReleaseDate? from = null;
        ReleaseDate? to = null;

        foreach (var (begin, end) in dated)
        {
            if (ReleaseDate.Parse(begin) is { } b && (from is not { } f || Earliest(b).CompareTo(Earliest(f)) < 0)) from = b;
            if (ReleaseDate.Parse(end) is { } e && (to is not { } t || Latest(e).CompareTo(Latest(t)) > 0)) to = e;
        }

        return from is null && to is null ? null : new DateSpan(from, to);
    }

    private static (int, int, int) Earliest(ReleaseDate date) => (date.Year, date.Month ?? 0, date.Day ?? 0);

    private static (int, int, int) Latest(ReleaseDate date) => (date.Year, date.Month ?? 13, date.Day ?? 32);
}

/// <summary>From one partial date to another; either side may be unknown, not both.</summary>
public sealed record DateSpan(ReleaseDate? From, ReleaseDate? To);
