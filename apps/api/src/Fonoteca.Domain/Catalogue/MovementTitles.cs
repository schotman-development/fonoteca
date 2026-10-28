namespace Fonoteca.Domain.Catalogue;

/// <summary>A movement's own name, with the piece's name in front taken off.</summary>
/// <remarks>
/// MusicBrainz titles a part "<i>piece</i>: <i>movement</i>", but the piece
/// in front is not always spelled as the piece itself is. Measured here
/// (2026-10-23), every one of these left the whole title on screen:
/// <list type="bullet">
/// <item>shorter — "Violin Concerto in A minor, op. 53: I. Allegro ma non
/// troppo" under "…, op. 53, B. 96/B. 108", "Das Rheingold: Scene I" under
/// "Das Rheingold, WWV 86A";</item>
/// <item>longer — "Symphony no. 3 in D minor, Part I: I. Kräftig. Entschieden"
/// under "Symphony no. 3 in D minor";</item>
/// <item>typography — straight quotes against curly, "B-flat" against
/// U+2010's "B‐flat", "E.T. The" against "E.T. the", and the French
/// " : ".</item>
/// </list>
/// So the name before a colon is the piece's where, typography folded and
/// case ignored, it is the piece's, or one of the two runs on from the other at
/// a comma or a space. The first colon that does wins, since a piece's own
/// title may hold one ("12 Variations on French Traditional: …"), and a colon
/// inside the piece's name is no place for the other to run on from. A name
/// that is something else stays: "Prélude no. 15 … “Raindrop”: Sostenuto"
/// under "24 Préludes" keeps which prélude it is.
/// </remarks>
public static class MovementTitles
{
    /// <returns>The rest of <paramref name="title"/>, or null where nothing in front names <paramref name="piece"/>.</returns>
    public static string? Without(string title, string piece)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(piece);

        var folded = Fold(title);
        var whole = Fold(piece);

        for (var colon = folded.IndexOf(": ", StringComparison.Ordinal);
            colon > 0;
            colon = folded.IndexOf(": ", colon + 1, StringComparison.Ordinal))
        {
            var rest = title[(colon + 2)..].Trim();

            if (rest.Length > 0 && Names(folded[..colon].TrimEnd(), whole)) return rest;
        }

        return null;
    }

    private static bool Names(string name, string whole) =>
        string.Equals(name, whole, StringComparison.OrdinalIgnoreCase)
        || RunsOn(name, whole)
        || RunsOn(whole, name);

    private static bool RunsOn(string shorter, string longer) =>
        shorter.Length > 0
        && longer.Length > shorter.Length
        && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
        && longer[shorter.Length] is ',' or ' ';

    /// <summary>One character for one, so an index into the fold is an index into the title.</summary>
    private static string Fold(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] switch
                {
                    '“' or '”' or '„' or '″' => '"',
                    '‘' or '’' or '′' => '\'',
                    '‐' or '‑' or '–' => '-',
                    var other => other,
                };
            }
        });
}
