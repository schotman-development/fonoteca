namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The credit tags a file carries, read off MusicBrainz's links as Picard reads them.
/// </summary>
/// <remarks>
/// Picard's mapping, as far as these eight tags go: a librettist is a lyricist,
/// an orchestrator and the instrument and vocal arrangers are arrangers, a
/// recording engineer is an engineer, and a player is
/// <c>PERFORMER=Name (instrument)</c>. A <c>writer</c> — a songwriter nobody
/// split into composer and lyricist — has no tag here, as it has none in the
/// owner's list; rights holders are nobody's credit.
///
/// Names as each link credits them, each once, in MusicBrainz's order.
/// </remarks>
public sealed record CreditTags
{
    public static CreditTags None { get; } = new();

    public IReadOnlyList<string> Composers { get; init; } = [];
    public IReadOnlyList<string> Lyricists { get; init; } = [];
    public IReadOnlyList<string> Conductors { get; init; } = [];
    public IReadOnlyList<string> Arrangers { get; init; } = [];
    public IReadOnlyList<string> Producers { get; init; } = [];
    public IReadOnlyList<string> Engineers { get; init; } = [];
    public IReadOnlyList<string> Mixers { get; init; } = [];
    public IReadOnlyList<string> Performers { get; init; } = [];

    /// <param name="links">The recording's contributions and then its work's, in their stored order.</param>
    public static CreditTags From(IEnumerable<(string Type, string? Attributes, string Name)> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string tag, string value)
        {
            if (!tags.TryGetValue(tag, out var values)) tags[tag] = values = [];
            if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }

        foreach (var (type, attributes, name) in links)
        {
            switch (type.ToLowerInvariant())
            {
                case "composer": Add(CatalogueTags.Composer, name); break;
                case "lyricist" or "librettist": Add(CatalogueTags.Lyricist, name); break;
                case "conductor": Add(CatalogueTags.Conductor, name); break;
                case "arranger" or "orchestrator" or "instrument arranger" or "vocal arranger":
                    Add(CatalogueTags.Arranger, name);
                    break;
                case "producer": Add(CatalogueTags.Producer, name); break;
                case "engineer" or "recording" or "audio" or "sound": Add(CatalogueTags.Engineer, name); break;
                case "mix": Add(CatalogueTags.Mixer, name); break;
                case "instrument" or "performer":
                    Add(CatalogueTags.Performer, attributes is null ? name : $"{name} ({attributes})");
                    break;
                case "vocal": Add(CatalogueTags.Performer, $"{name} ({attributes ?? "vocals"})"); break;
                case "performing orchestra": Add(CatalogueTags.Performer, $"{name} (orchestra)"); break;
                case "chorus master" or "concertmaster": Add(CatalogueTags.Performer, $"{name} ({type})"); break;
                default: break;
            }
        }

        IReadOnlyList<string> Of(string tag) => tags.TryGetValue(tag, out var values) ? values : [];

        return new CreditTags
        {
            Composers = Of(CatalogueTags.Composer),
            Lyricists = Of(CatalogueTags.Lyricist),
            Conductors = Of(CatalogueTags.Conductor),
            Arrangers = Of(CatalogueTags.Arranger),
            Producers = Of(CatalogueTags.Producer),
            Engineers = Of(CatalogueTags.Engineer),
            Mixers = Of(CatalogueTags.Mixer),
            Performers = Of(CatalogueTags.Performer),
        };
    }
}
