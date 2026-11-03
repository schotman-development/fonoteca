namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Who stated an album's genres (<see cref="ReleaseGroup.GenresSource"/>)
/// where it was a person or an agent, and what a person may state.
/// </summary>
/// <remarks>
/// <b>A person's answer gives way to nothing</b> (rule 4, ADR 0020): a shop's
/// genres give way to MusicBrainz's, a person's or an agent's to no rule —
/// only to their next answer, or to taking them back, which hands the album to
/// MusicBrainz and the shops again.
///
/// <b>In MusicBrainz's spelling</b>, lowercased, so every reader takes them as
/// it takes MusicBrainz's — <see cref="ClassicalWorks"/> looks for
/// <c>classical</c> as written. A name <see cref="Genres.Of"/> does not fold is
/// refused rather than stored as <see cref="Genres.Other"/>.
/// </remarks>
public static class GenreSources
{
    /// <summary>A person's answer, from a signed-in request.</summary>
    public const string Person = "person";

    /// <summary>An agent's answer, through <c>/mcp</c>.</summary>
    public const string Agent = "agent";

    /// <summary>Whether the genres are a person's or an agent's, which no rule replaces.</summary>
    public static bool IsDecided(string? source) => source is Person or Agent;

    /// <summary>A person's genres as stored: trimmed, lowercased, once each, in their order.</summary>
    /// <returns>Null where one is no genre the tree folds, named by <paramref name="unknown"/>.</returns>
    public static IReadOnlyList<string>? Stated(IEnumerable<string?> typed, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var stated = new List<string>();

        foreach (var name in typed.Select(name => name?.Trim() ?? string.Empty).Where(name => name.Length > 0))
        {
            if (Genres.Of(name) == Genres.Other)
            {
                unknown = name;
                return null;
            }

            var spelled = name.ToLowerInvariant();

            if (!stated.Contains(spelled, StringComparer.Ordinal)) stated.Add(spelled);
        }

        unknown = null;
        return stated;
    }
}
