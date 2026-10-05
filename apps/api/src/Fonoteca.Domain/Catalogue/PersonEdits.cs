using System.Text.Json;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// A person's corrections to a catalogue row, kept beside the providers' answers.
/// </summary>
/// <remarks>
/// <b>Rule 4.</b> The row's own columns are what MusicBrainz and Wikipedia said,
/// and a pass rewrites them whenever it asks again. What a person said is a map
/// from field to value that no pass writes, and it wins wherever a field is
/// present. A present field with a null value is an answer ("this orchestra has
/// no country"), which a nullable column could not tell apart from "not edited".
///
/// <b>Only differences are stored.</b> The page sends the whole form, and a
/// field that matches what the provider holds is not an edit — so putting a
/// value back is how an edit is undone, and the "set by you" mark goes with it.
///
/// Every value is a string, because the map is only ever read back by field
/// name: a year is <c>"1840"</c>, a list is joined with <c>", "</c>.
/// </remarks>
public static class PersonEdits
{
    public static IReadOnlyDictionary<string, string?> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string?>();

        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
            ?? new Dictionary<string, string?>();
    }

    public static string? Write(IReadOnlyDictionary<string, string?> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return edits.Count == 0 ? null : JsonSerializer.Serialize(edits);
    }

    /// <summary>
    /// An album's corrections: its own, over the ones made to the pressing shown.
    /// </summary>
    /// <remarks>
    /// Two rows because they are two subjects — the album's title and year are
    /// not a pressing's, and the pressing a page shows changes as editions are
    /// proved — read back as one map so a page asks one question.
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> Combine(string? album, string? pressing)
    {
        var edits = new Dictionary<string, string?>(Read(pressing), StringComparer.Ordinal);

        foreach (var (field, value) in Read(album)) edits[field] = value;

        return edits;
    }

    /// <summary>The value to show: the person's where there is one, the provider's otherwise.</summary>
    public static string? Apply(IReadOnlyDictionary<string, string?> edits, string field, string? provider)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return edits.TryGetValue(field, out var edited) ? edited : provider;
    }

    /// <summary>
    /// The fields where what a person asked for differs from what the provider holds.
    /// </summary>
    /// <remarks>
    /// Blank and whitespace read as null on both sides, because a form cannot
    /// send a null and a cleared input is somebody saying "none".
    /// </remarks>
    public static Dictionary<string, string?> Diff(
        IReadOnlyDictionary<string, string?> wanted,
        IReadOnlyDictionary<string, string?> provider)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(provider);

        var edits = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (field, value) in wanted)
        {
            var mine = Blank(value);
            var theirs = Blank(provider.GetValueOrDefault(field));

            if (!string.Equals(mine, theirs, StringComparison.Ordinal)) edits[field] = mine;
        }

        return edits;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
