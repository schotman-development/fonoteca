using System.Globalization;
using System.Text.RegularExpressions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Lyrics as text: LRC with a time before each line, or plain words.
/// </summary>
/// <remarks>
/// <b>The tag holds what the source sent, as it sent it</b> — LRC where there
/// is timed text (the owner's choice), so Roon and Navidrome can follow the
/// line being sung. These rules only read it: into lines for a Subsonic client,
/// and to tell a lyrics file beside a track from the release group's advert
/// that often sits under the same name.
/// </remarks>
public static partial class LyricsText
{
    /// <summary>
    /// Lines with words a text needs before it counts as lyrics.
    /// </summary>
    /// <remarks>
    /// The <c>.lrc</c> files here that are not lyrics are one line —
    /// <c>www.t.me/pmedia_music</c> on 69, <c>kmn</c> on 11 — or an LRC header
    /// with no words, as on a symphony's movement. Three lines is the shortest
    /// a verse gets. Longer junk still passes: a rip's "thank you for
    /// downloading" note, and a book's preface on a spoken-word track.
    /// </remarks>
    public const int LeastLines = 3;

    /// <summary>The words line by line, with each one's start where the text is timed.</summary>
    public static LyricsLines Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var timed = new List<LyricsLine>();
        var plain = new List<LyricsLine>();
        var offset = 0;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            var starts = new List<int>();
            var tagged = false;

            while (Tag().Match(line) is { Success: true } tag)
            {
                tagged = true;

                if (Time(tag.Groups["body"].Value) is { } start)
                {
                    starts.Add(start);
                }
                else if (tag.Groups["body"].Value is var body
                    && body.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(body.AsSpan("offset:".Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var shift))
                {
                    offset = shift;
                }

                line = line[tag.Length..].TrimStart();
            }

            var words = Word().Replace(line, string.Empty).Trim();

            if (starts.Count > 0)
            {
                timed.AddRange(starts.Select(start => new LyricsLine(start, words)));
            }
            else if (!tagged)
            {
                plain.Add(new LyricsLine(null, words));
            }
        }

        if (timed.Count > 0)
        {
            return new LyricsLines(true, offset, [.. timed.OrderBy(line => line.Start)]);
        }

        while (plain.Count > 0 && plain[^1].Text.Length == 0) plain.RemoveAt(plain.Count - 1);
        while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);

        return new LyricsLines(false, 0, plain);
    }

    /// <summary>The words without their times.</summary>
    public static string Plain(string text) =>
        string.Join('\n', Lines(text).Lines.Select(line => line.Text));

    /// <summary>Whether a text is somebody's lyrics rather than a header or an advert — see <see cref="LeastLines"/>.</summary>
    public static bool IsLyrics(string text) =>
        Lines(text).Lines.Count(line => line.Text.Length > 0) >= LeastLines;

    /// <summary>An LRC time — <c>mm:ss</c>, <c>mm:ss.xx</c>, <c>mm:ss.xxx</c> or <c>mm:ss:xx</c> — in milliseconds.</summary>
    private static int? Time(string body)
    {
        var match = Stamp().Match(body);

        if (!match.Success) return null;

        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups["f"].Value;
        var millis = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);

        return (((minutes * 60) + seconds) * 1000) + millis;
    }

    /// <summary>A time, or one of LRC's header tags — never <c>[Chorus]</c> or <c>[Verse 1: Jay-Z]</c>, which are words.</summary>
    [GeneratedRegex(@"^\[(?<body>\d{1,3}:[\d.:]+|(?:ar|al|ti|au|by|length|offset|re|tool|ve|la|lang|id|#):[^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^(?<m>\d{1,3}):(?<s>[0-5]?\d)(?:[.:](?<f>\d{1,3}))?$")]
    private static partial Regex Stamp();

    /// <summary>An enhanced LRC's time before each word.</summary>
    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex Word();
}

/// <param name="Synced">Whether the lines carry their start.</param>
/// <param name="Offset">The LRC's own <c>[offset:]</c>, milliseconds to add to every start.</param>
public sealed record LyricsLines(bool Synced, int Offset, IReadOnlyList<LyricsLine> Lines);

/// <param name="Start">Milliseconds from the start of the track, or null where the text is not timed.</param>
public sealed record LyricsLine(int? Start, string Text);
