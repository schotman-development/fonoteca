using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What a smart playlist's rules may say, and whether a set of them says it.
/// </summary>
/// <remarks>
/// <b>A tree, as deep as it needs to be</b> (the owner's choice, after
/// WordPress's <c>tax_query</c>): a group matches all or any of its rules, and
/// a rule is a condition or another group. Every client that reads the m3u
/// sees a plain list, so the rules can ask anything the catalogue knows rather
/// than what another server's language can carry — the reason for the width
/// of <see cref="SmartField"/>.
///
/// <b>Conditions are about a file and what the catalogue says of it</b>; a
/// song is in the playlist once, as the best of its copies that matched. The
/// catalogue's hearts, plays and tags belong to an account, so those rules read
/// the playlist's owner (<c>SmartPlaylist.OwnerId</c>). Text matches without
/// regard to case. A value the catalogue does not hold matches no comparison, but does match "is not" and "does not
/// contain", of a number as of text, since a song with no genre is not jazz;
/// <see cref="SmartOperator.IsEmpty"/> asks for it outright.
///
/// The screen's twin is <c>smartRules.ts</c>; a field's kind there must match
/// <see cref="KindOf"/>, or the screen offers what this refuses.
/// </remarks>
public static class SmartRules
{
    /// <summary>How deep groups may nest; past this a tree is a mistake, not a question.</summary>
    public const int MaxDepth = 8;

    /// <summary>Conditions and groups in one tree, together.</summary>
    public const int MaxRules = 500;

    public const int MaxLimit = 100_000;

    public const int MaxText = 500;

    /// <summary>Ten years: "added in the last" further back than that is every file.</summary>
    public const int MaxDays = 3_650;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SmartFieldKind KindOf(SmartField field) => field switch
    {
        SmartField.Year or SmartField.PressingYear or SmartField.Length or SmartField.BitDepth
            or SmartField.SampleRate or SmartField.Bitrate or SmartField.Channels or SmartField.DynamicRange
            or SmartField.Loudness or SmartField.LoudnessRange or SmartField.TruePeak or SmartField.SamplePeak
            or SmartField.LeadingSilence or SmartField.TrailingSilence or SmartField.Bpm or SmartField.FileSize
            or SmartField.DiscNumber or SmartField.TrackNumber or SmartField.TrackCount or SmartField.DiscCount
            or SmartField.PressingMonth or SmartField.PressingDay or SmartField.FileDiscNumber or SmartField.FileTrackNumber
            or SmartField.ArtistBeganYear or SmartField.ArtistEndedYear or SmartField.Plays => SmartFieldKind.Number,
        SmartField.Lossless or SmartField.HiRes or SmartField.Followed or SmartField.Damaged or SmartField.Clipped
            or SmartField.Instrumental or SmartField.Monitored or SmartField.ArtistEnded or SmartField.Loved
            or SmartField.AlbumLoved or SmartField.ArtistLoved or SmartField.WorkLoved => SmartFieldKind.Flag,
        SmartField.Added or SmartField.Modified or SmartField.LovedWhen or SmartField.Played => SmartFieldKind.Date,
        _ => SmartFieldKind.Text,
    };

    public static bool Allows(SmartFieldKind kind, SmartOperator op) => kind switch
    {
        SmartFieldKind.Text => op is SmartOperator.Is or SmartOperator.IsNot or SmartOperator.Contains
            or SmartOperator.DoesNotContain or SmartOperator.StartsWith or SmartOperator.EndsWith
            or SmartOperator.IsEmpty or SmartOperator.IsNotEmpty,
        SmartFieldKind.Number => op is SmartOperator.Is or SmartOperator.IsNot or SmartOperator.AtLeast
            or SmartOperator.AtMost or SmartOperator.Between or SmartOperator.IsEmpty or SmartOperator.IsNotEmpty,
        SmartFieldKind.Flag => op is SmartOperator.IsTrue or SmartOperator.IsFalse,
        SmartFieldKind.Date => op is SmartOperator.InTheLast or SmartOperator.NotInTheLast,
        _ => false,
    };

    /// <summary>A value the operator reads as a number: invariant culture, finite.</summary>
    public static double? Number(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;

    /// <summary>What is wrong with a set of rules, each said once; empty where nothing is.</summary>
    public static IReadOnlyList<string> Problems(SmartCriteria? criteria)
    {
        if (criteria?.Rules is null) return ["`rules` is missing."];

        var problems = new List<string>();
        var count = 0;

        Check(criteria.Rules, 1, isRoot: true);

        if (count > MaxRules) problems.Add($"A smart playlist holds at most {MaxRules} rules and groups; this has {count}.");

        if (criteria.Limit is < 1 or > MaxLimit) problems.Add($"`limit` is between 1 and {MaxLimit}, or left out for every song.");

        return [.. problems.Distinct(StringComparer.Ordinal)];

        void Check(SmartRule rule, int depth, bool isRoot)
        {
            count++;

            if (rule.Rules is { } children)
            {
                if (rule.Field is not null || rule.Operator is not null)
                {
                    problems.Add("A rule is a group or a condition, not both.");
                }

                if (depth > MaxDepth) problems.Add($"Groups nest at most {MaxDepth} deep.");

                foreach (var child in children)
                {
                    if (child is null) problems.Add("A group holds an empty rule.");
                    else Check(child, depth + 1, isRoot: false);
                }

                return;
            }

            if (isRoot)
            {
                problems.Add("The rules start with a group.");
                return;
            }

            if (rule.Field is not { } field || rule.Operator is not { } op)
            {
                problems.Add("A condition names a field and an operator.");
                return;
            }

            var kind = KindOf(field);

            if (!Allows(kind, op))
            {
                problems.Add($"`{op}` cannot be asked of `{field}`.");
                return;
            }

            switch (op)
            {
                case SmartOperator.IsEmpty or SmartOperator.IsNotEmpty or SmartOperator.IsTrue or SmartOperator.IsFalse:
                    return;

                case SmartOperator.InTheLast or SmartOperator.NotInTheLast:
                    if (Number(rule.Value) is not { } days || days != Math.Floor(days) || days < 1 || days > MaxDays)
                    {
                        problems.Add($"`{field}` takes a whole number of days from 1 to {MaxDays}.");
                    }

                    return;

                case SmartOperator.Between:
                    if (Number(rule.Value) is null || Number(rule.To) is null)
                    {
                        problems.Add($"`between` on `{field}` takes two numbers, `value` and `to`.");
                    }

                    return;
            }

            if (kind == SmartFieldKind.Number)
            {
                if (Number(rule.Value) is null) problems.Add($"`{field}` takes a number.");
            }
            else if (string.IsNullOrWhiteSpace(rule.Value) || rule.Value.Length > MaxText)
            {
                problems.Add($"`{field}` takes some text, at most {MaxText} characters.");
            }
        }
    }

    /// <summary>The rules as stored: camelCase, enums by name.</summary>
    public static string Serialize(SmartCriteria criteria) => JsonSerializer.Serialize(criteria, Json);

    /// <summary>Stored rules read back; null where they do not parse, which a playlist then writes nothing for.</summary>
    public static SmartCriteria? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<SmartCriteria>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A smart playlist's rules, its order and how many songs it keeps.</summary>
/// <param name="Rules">The top group.</param>
/// <param name="Reverse">
/// Turns <paramref name="Sort"/> round from its own direction — newest first
/// for <see cref="SmartSort.Added"/>, A to Z for text, lowest first for
/// numbers. Ignored for <see cref="SmartSort.Random"/>.
/// </param>
/// <param name="Limit">The first this many songs in that order; null for every song that matches.</param>
/// <param name="SortField">
/// Orders by any <see cref="SmartField"/>, and then <paramref name="Sort"/> is
/// ignored. Dates run newest first, everything else lowest or A to Z first
/// (a flag: no before yes), and a song with no value comes last either way
/// round. Null keeps <paramref name="Sort"/>, which is all a playlist saved
/// before this had.
/// </param>
public sealed record SmartCriteria(SmartRule Rules, SmartSort Sort, bool Reverse, int? Limit, SmartField? SortField = null);

/// <summary>
/// A group of rules, or one condition: a group where <paramref name="Rules"/>
/// is present, a condition otherwise.
/// </summary>
/// <param name="Match">A group's: all of its rules, or any. Absent reads as all.</param>
/// <param name="Value">A condition's text, number or count of days, as text.</param>
/// <param name="To">The upper bound of <see cref="SmartOperator.Between"/>, inclusive like the lower.</param>
public sealed record SmartRule(
    SmartMatch? Match,
    IReadOnlyList<SmartRule>? Rules,
    SmartField? Field,
    SmartOperator? Operator,
    string? Value,
    string? To);

[JsonConverter(typeof(JsonStringEnumConverter<SmartMatch>))]
public enum SmartMatch
{
    All = 0,
    Any = 1,
}

/// <summary>What a condition can ask about.</summary>
/// <remarks>
/// Per song: its billing, its work, the people MusicBrainz credits on it or
/// its work (and the work's parent), its genres as a file carries them
/// (<see cref="FileGenres"/>). Per album: its title, billing, type, year — the
/// album's, as the album list shows it, or the pressing's where the folder is
/// proven to be one. Per artist billed on the song or its album: followed, a
/// country, a type. Per file: what the probe and the analysis measured, its
/// path, and when the scan first saw it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SmartField>))]
public enum SmartField
{
    Title = 0,
    Artist,
    AlbumArtist,
    Album,
    Work,
    Genre,
    Composer,
    Lyricist,
    Conductor,
    Performer,
    Arranger,
    Producer,
    Engineer,

    /// <summary>Anyone billed on the song or credited on it or its work, in any role.</summary>
    Credited,

    Isrc,

    /// <summary>The words as LRCLIB has them, untimed.</summary>
    Lyrics,

    /// <summary>LRCLIB's timed lyrics, each line with its time.</summary>
    TimedLyrics,

    /// <summary>When the work was composed, as MusicBrainz dates it: "1805", "1805-03".</summary>
    Composed,

    /// <summary>The work's key as MusicBrainz states it ("C minor"), or its parent's.</summary>
    WorkKey,

    /// <summary>MusicBrainz's work type — Symphony, Opera, Song — of the work or its parent.</summary>
    WorkType,

    /// <summary>ISO 639-3 codes of the language the work's words are in.</summary>
    WorkLanguage,

    /// <summary>MusicBrainz's primary type or any secondary one: Album, Single, EP, Live, Compilation, Soundtrack.</summary>
    AlbumType,

    Label,
    ReleaseCountry,
    MediumFormat,
    Barcode,
    CatalogNumber,

    /// <summary>Official, Promotion, Bootleg, Pseudo-Release.</summary>
    ReleaseStatus,

    /// <summary>What sets the pressing apart from others of the album: "remastered", "Japanese edition".</summary>
    Edition,
    PressingMonth,
    PressingDay,

    ArtistCountry,

    /// <summary>Person, Group, Orchestra, Choir, Character, Other.</summary>
    ArtistType,

    ArtistGender,
    ArtistBeganYear,
    ArtistEndedYear,
    ArtistSortName,
    ArtistDisambiguation,

    /// <summary>An artist billed on the song or its album has ended: died or disbanded.</summary>
    ArtistEnded,

    Path,
    Codec,
    Year,
    PressingYear,

    /// <summary>In seconds.</summary>
    Length,

    /// <summary>The bits the audio really uses where the analysis found fewer than the container declares.</summary>
    BitDepth,

    /// <summary>In kHz.</summary>
    SampleRate,

    /// <summary>In kbps.</summary>
    Bitrate,

    Channels,
    DynamicRange,

    /// <summary>Integrated loudness, in LUFS.</summary>
    Loudness,

    /// <summary>In LU.</summary>
    LoudnessRange,

    /// <summary>In dBTP.</summary>
    TruePeak,

    /// <summary>In dBFS.</summary>
    SamplePeak,

    /// <summary>Samples at full scale or past it, as <see cref="AudioAnalysis.Clipped"/> reads them.</summary>
    Clipped,

    /// <summary>In seconds.</summary>
    LeadingSilence,

    /// <summary>In seconds.</summary>
    TrailingSilence,

    /// <summary>Beats per minute, only where the reading is confident enough to tag (<see cref="TempoAndKey.TagBpm"/>).</summary>
    Bpm,

    /// <summary>Essentia's reading ("F# minor" or just "F#"), however sure it was.</summary>
    Key,

    /// <summary>In MB.</summary>
    FileSize,

    /// <summary>The disc number the file's own tag states, as the scan read it.</summary>
    FileDiscNumber,

    /// <summary>The track number the file's own tag states, as the scan read it.</summary>
    FileTrackNumber,

    Lossless,

    /// <summary>Lossless, and more than 16 real bits or more than 48 kHz.</summary>
    HiRes,

    /// <summary>An artist billed on the song or its album is followed.</summary>
    Followed,

    /// <summary>The probe found the file corrupt, truncated or unreadable.</summary>
    Damaged,

    /// <summary>LRCLIB says it has no words.</summary>
    Instrumental,

    /// <summary>The album is monitored for new editions.</summary>
    Monitored,

    /// <summary>The disc the track is on, in the pressing the file is filed under.</summary>
    DiscNumber,

    /// <summary>The track's place on its disc in that pressing.</summary>
    TrackNumber,

    /// <summary>Tracks on the pressing.</summary>
    TrackCount,

    /// <summary>Discs in the pressing.</summary>
    DiscCount,

    /// <summary>The owner has hearted the song.</summary>
    Loved,

    /// <summary>The owner has hearted the album.</summary>
    AlbumLoved,

    /// <summary>The owner has hearted an artist billed on the song or its album.</summary>
    ArtistLoved,

    /// <summary>The owner has hearted the whole work the song is a movement of.</summary>
    WorkLoved,

    /// <summary>When the owner hearted the song.</summary>
    LovedWhen,

    /// <summary>How often the owner has played the song, whichever copy.</summary>
    Plays,

    /// <summary>When the owner last played the song.</summary>
    Played,

    /// <summary>A tag the owner has put on the song.</summary>
    Tag,

    /// <summary>When the scan first saw the file.</summary>
    Added,

    /// <summary>When the file's bytes last changed on disk.</summary>
    Modified,
}

[JsonConverter(typeof(JsonStringEnumConverter<SmartOperator>))]
public enum SmartOperator
{
    Is = 0,
    IsNot,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,
    AtLeast,
    AtMost,
    Between,
    IsEmpty,
    IsNotEmpty,
    IsTrue,
    IsFalse,
    InTheLast,
    NotInTheLast,
}

public enum SmartFieldKind
{
    Text = 0,
    Number,
    Flag,
    Date,
}

[JsonConverter(typeof(JsonStringEnumConverter<SmartSort>))]
public enum SmartSort
{
    /// <summary>Newest first, by when the scan first saw the song's file.</summary>
    Added = 0,
    Title,
    Artist,
    Album,
    Year,
    Length,

    /// <summary>A fixed shuffle: the same rules give the same order, and saving them deals again.</summary>
    Random,
}
