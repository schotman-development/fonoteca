namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The credit tags a file carries, read off MusicBrainz's links as Picard reads them.
/// </summary>
/// <remarks>
/// Picard's mapping, as far as these eleven tags go: a librettist is a lyricist,
/// an orchestrator and the instrument and vocal arrangers are arrangers, a
/// recording engineer is an engineer, and a player is
/// <c>PERFORMER=Name (instrument)</c>. A <c>writer</c> — a songwriter nobody
/// split into composer and lyricist — is a <c>WRITER</c>, a remixer a
/// <c>REMIXER</c> and a DJ-mixer a <c>DJMIXER</c>; rights holders are nobody's credit.
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
    public IReadOnlyList<string> Writers { get; init; } = [];
    public IReadOnlyList<string> Remixers { get; init; } = [];
    public IReadOnlyList<string> DjMixers { get; init; } = [];

    /// <summary>The tag one link goes under, and for a player what they played; null for a link that is nobody's credit.</summary>
    /// <remarks>Picard's mapping, once, so Subsonic's contributors and the files cannot disagree.</remarks>
    public static (string Tag, string? Part)? Role(string type, string? attributes)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.ToLowerInvariant() switch
        {
            "composer" => (CatalogueTags.Composer, null),
            "lyricist" or "librettist" => (CatalogueTags.Lyricist, null),
            "conductor" => (CatalogueTags.Conductor, null),
            "arranger" or "orchestrator" or "instrument arranger" or "vocal arranger" => (CatalogueTags.Arranger, null),
            "producer" => (CatalogueTags.Producer, null),
            "engineer" or "recording" or "audio" or "sound" => (CatalogueTags.Engineer, null),
            "mix" => (CatalogueTags.Mixer, null),
            "writer" => (CatalogueTags.Writer, null),
            "remixer" => (CatalogueTags.Remixer, null),
            "mix-dj" => (CatalogueTags.DjMixer, null),
            "instrument" or "performer" => (CatalogueTags.Performer, attributes),
            "vocal" => (CatalogueTags.Performer, attributes ?? "vocals"),
            "performing orchestra" => (CatalogueTags.Performer, "orchestra"),
            "chorus master" or "concertmaster" => (CatalogueTags.Performer, type),
            _ => null,
        };
    }

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
            if (Role(type, attributes) is not { } role) continue;

            Add(role.Tag, role.Part is null ? name : $"{name} ({role.Part})");
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
            Writers = Of(CatalogueTags.Writer),
            Remixers = Of(CatalogueTags.Remixer),
            DjMixers = Of(CatalogueTags.DjMixer),
        };
    }
}
