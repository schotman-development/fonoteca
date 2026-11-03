using System.Globalization;
using System.Text.RegularExpressions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>The movement number a title states, and whether a recording's title and its work's state different ones.</summary>
/// <remarks>
/// A movement's number is its place among its parent's parts, and MusicBrainz
/// states both that and which work a recording performs by hand. The link is
/// what goes wrong. Measured here (2026-11-03), five recordings whose own
/// title names one movement are linked to another:
/// <list type="bullet">
/// <item>the finale of Mendelssohn's First, to its first movement;</item>
/// <item>No. 35 of Paulus, to No. 34;</item>
/// <item>the Adagio of Beethoven's Ninth, to the finale of his Seventh;</item>
/// <item>the Menuetto of Schubert's Second, to its first movement and its
/// Menuetto, in that order;</item>
/// <item>the Allegro of Shostakovich's First, to the finale of another First
/// and its own Allegro, in that order.</item>
/// </list>
/// So of several links the first whose title does not disagree is the work,
/// and a number is not believed where the two titles state different
/// movements; the link itself is MusicBrainz's to correct. A title states one
/// the way MusicBrainz writes it, at
/// the start of a part after a colon: "IV.", "3.", "No. 35". The last such part
/// counts, since a nested part's own number comes last. A letter after the
/// number ("No. 4a", "IIIb.") is one movement over two tracks, not another
/// movement. A title stating none disagrees with nothing.
///
/// The work's number is read off its title, never its place among the parts:
/// the Carmen Fantasy's "I. Moderato" is its second part, after an
/// unnumbered introduction.
/// </remarks>
public static partial class MovementNumbers
{
    /// <returns>The number <paramref name="title"/> states, or null where it states none.</returns>
    public static int? Stated(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        int? stated = null;

        foreach (var part in title.Split(": "))
        {
            var marker = Marker().Match(part);

            if (!marker.Success) continue;

            stated = marker.Groups["roman"].Success
                ? Roman(marker.Groups["roman"].Value)
                : int.Parse(marker.Groups["arabic"].Value, CultureInfo.InvariantCulture);
        }

        return stated;
    }

    /// <summary>Whether both titles state a movement number, and different ones.</summary>
    public static bool Disagree(string recording, string work) =>
        Stated(recording) is { } performed && Stated(work) is { } linked && performed != linked;

    private static int Roman(string numeral)
    {
        var total = 0;

        for (var i = 0; i < numeral.Length; i++)
        {
            var value = Value(numeral[i]);
            total += i + 1 < numeral.Length && Value(numeral[i + 1]) > value ? -value : value;
        }

        return total;
    }

    private static int Value(char digit) => digit switch
    {
        'I' => 1,
        'V' => 5,
        'X' => 10,
        'L' => 50,
        _ => 100,
    };

    [GeneratedRegex(
        @"^(?:(?<roman>[IVXLC]+)[a-z]?\.(?:\s|$)|(?<arabic>\d+)[a-z]?\.(?:\s|$)|[Nn][OoRr]\.\s*(?<arabic>\d+)(?!\d))",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Marker();
}
