using System.Buffers.Binary;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// A smart playlist's rules as one query, and the songs they come to.
/// </summary>
/// <remarks>
/// <b>The rules are a <c>WHERE</c> over files, in SQL</b> — every condition an
/// expression EF translates, the groups folded with <c>AND</c> and <c>OR</c> — so
/// a library of 100,000 files is never read into memory to be filtered. What
/// comes back is the matching files' few columns; the song is chosen, ordered
/// and cut in memory from those, because "the best copy of each recording"
/// is <see cref="PlaylistFile.Best"/>'s rule and is not written twice.
///
/// A file no recording is known for is not a song and is never in one.
/// </remarks>
internal static class SmartQuery
{
    private static readonly string[] Composers = ["composer"];
    private static readonly string[] Lyricists = ["lyricist", "librettist"];
    private static readonly string[] Conductors = ["conductor"];
    private static readonly string[] Performers = ["instrument", "performer", "vocal"];
    private static readonly string[] Arrangers = ["arranger", "orchestrator", "instrument arranger", "vocal arranger"];
    private static readonly string[] Producers = ["producer"];
    private static readonly string[] Engineers = ["engineer", "recording", "audio", "sound", "mix"];

    /// <summary>The songs the rules come to, in their order and cut to their limit, each with the copy that plays it.</summary>
    /// <param name="seed">What deals <see cref="SmartSort.Random"/>: the same seed, the same order.</param>
    internal static async Task<List<SmartSong>> SongsAsync(
        FonotecaDbContext db,
        SmartCriteria criteria,
        string seed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null)
            .Where(Predicate(criteria.Rules, now))
            .Select(file => new
            {
                file.Id,
                file.RecordingId,
                file.Path,
                file.SizeBytes,
                file.Quality,
                file.Measurement,
                file.Integrity,
                file.Recording!.Title,
                Artist = file.Recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)
                    .FirstOrDefault(),
                Album = file.ReleaseGroup == null ? null : file.ReleaseGroup.Title,
                Year = file.ReleaseGroup == null
                    ? null
                    : file.ReleaseGroup.FirstReleaseYear
                        ?? file.ReleaseGroup.Releases.Where(release => release.Status == "Official").Min(release => release.ReleasedYear)
                        ?? file.ReleaseGroup.Releases.Min(release => release.ReleasedYear),
                file.Recording.Duration,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songs = files
            .GroupBy(file => file.RecordingId!.Value)
            .Select(group =>
            {
                var copies = group.ToDictionary(file => file.Id);
                var best = PlaylistFile.Best(group.Select(file =>
                    new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity)))!;
                var row = copies[best.Id];

                return new SmartSong(
                    group.Key,
                    best,
                    row.Title,
                    row.Artist,
                    row.Album,
                    row.Year,
                    best.Quality?.Duration ?? row.Duration,
                    group.Select(file => file.Id.Value.ToString("N")).Max(StringComparer.Ordinal)!);
            });

        var culture = CultureInfo.InvariantCulture.CompareInfo;
        var reverse = criteria.Reverse;
        var text = Known<string>((left, right) => culture.Compare(left, right, CompareOptions.IgnoreCase), reverse);
        var plain = Known<string>((left, right) => culture.Compare(left, right, CompareOptions.IgnoreCase), false);

        IOrderedEnumerable<SmartSong> ordered = criteria.Sort switch
        {
            SmartSort.Title => songs.OrderBy(song => song.Title, text).ThenBy(song => song.Artist, plain),
            SmartSort.Artist => songs.OrderBy(song => song.Artist, text).ThenBy(song => song.Album, plain),
            SmartSort.Album => songs.OrderBy(song => song.Album, text),
            SmartSort.Year => songs.OrderBy(song => song.Year, Known<int?>((left, right) => left!.Value.CompareTo(right!.Value), reverse))
                .ThenBy(song => song.Album, plain),
            SmartSort.Length => songs.OrderBy(song => song.Duration, Known<TimeSpan?>((left, right) => left!.Value.CompareTo(right!.Value), reverse)),
            SmartSort.Random => songs.OrderBy(song => Deal(seed, song.RecordingId), Comparer<byte[]>.Create(Bytes)),
            _ => reverse
                ? songs.OrderBy(song => song.Added, StringComparer.Ordinal)
                : songs.OrderByDescending(song => song.Added, StringComparer.Ordinal),
        };

        var all = ordered.ThenBy(song => song.Copy.Path, StringComparer.Ordinal);

        return [.. criteria.Limit is { } limit ? all.Take(limit) : all];
    }

    /// <summary>A condition, or a group folded from its rules; an empty group matches every file.</summary>
    internal static Expression<Func<MediaFile, bool>> Predicate(SmartRule rule, DateTimeOffset now)
    {
        if (rule.Rules is { } children)
        {
            var parts = children.Select(child => Predicate(child, now)).ToList();

            if (parts.Count == 0) return file => true;

            return parts.Aggregate((left, right) => Combine(left, right, rule.Match == SmartMatch.Any));
        }

        var field = rule.Field!.Value;
        var op = rule.Operator!.Value;

        return SmartRules.KindOf(field) switch
        {
            SmartFieldKind.Text => Text(Sources(field), op, rule.Value?.Trim() ?? string.Empty),
            SmartFieldKind.Number => Number(field, op, SmartRules.Number(rule.Value), SmartRules.Number(rule.To)),
            SmartFieldKind.Flag => op == SmartOperator.IsTrue ? Flag(field, true) : Flag(field, false),
            _ => Added(op, (int)SmartRules.Number(rule.Value)!.Value, now),
        };
    }

    /// <summary>
    /// Where a text field's values come from: one column, or a list of them,
    /// and the separator where one column holds several joined.
    /// </summary>
    private sealed record Source(LambdaExpression Values, bool Many, string? Separator = null);

    private static Source One(Expression<Func<MediaFile, string?>> value, string? separator = null) => new(value, false, separator);

    private static Source Many(Expression<Func<MediaFile, IEnumerable<string?>>> values) => new(values, true);

    private static IReadOnlyList<Source> Sources(SmartField field) => field switch
    {
        SmartField.Title => [One(file => file.Recording!.Title)],
        SmartField.Artist =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)),
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Name)),
        ],
        SmartField.AlbumArtist =>
        [
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.Name)),
        ],
        SmartField.Album => [One(file => file.ReleaseGroup!.Title)],
        SmartField.Work => [One(file => file.Recording!.Work!.Title), One(file => file.Recording!.Work!.Parent!.Title)],
        SmartField.Genre =>
        [
            One(
                file => file.ReleaseGroup != null && file.ReleaseGroup.Genres != null && file.ReleaseGroup.Genres != ""
                    ? file.ReleaseGroup.Genres
                    : file.Recording!.Genres,
                ", "),
        ],
        SmartField.Composer => Credited(Composers),
        SmartField.Lyricist => Credited(Lyricists),
        SmartField.Conductor => Credited(Conductors),
        SmartField.Performer => Credited(Performers),
        SmartField.Arranger => Credited(Arrangers),
        SmartField.Producer => Credited(Producers),
        SmartField.Engineer => Credited(Engineers),
        SmartField.Credited =>
        [
            .. Credited(null),
            Many(file => file.Recording!.Credits.Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)),
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Name)),
        ],
        SmartField.Isrc => [One(file => file.Recording!.Isrcs, ", "), One(file => file.Recording!.Isrc)],
        SmartField.AlbumType => [One(file => file.ReleaseGroup!.PrimaryType), One(file => file.ReleaseGroup!.SecondaryTypes, ", ")],
        SmartField.Label => [One(file => file.Release!.Label)],
        SmartField.ReleaseCountry => [One(file => file.Release!.Country)],
        SmartField.MediumFormat => [One(file => file.Release!.MediumFormats, "+")],
        SmartField.ArtistCountry =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Country)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.Country)),
        ],
        SmartField.ArtistType =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Type)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.Type)),
        ],
        SmartField.Path => [One(file => file.Path)],
        SmartField.Codec => [One(file => file.Quality!.Codec)],
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a text field."),
    };

    /// <summary>The people credited on the song, its work and the work's parent, in these roles; every role where null.</summary>
    private static IReadOnlyList<Source> Credited(string[]? roles) =>
        roles is null
            ?
            [
                Many(file => file.Recording!.Contributions.Select(credit => credit.Name)),
                Many(file => file.Recording!.Work!.Contributions.Select(credit => credit.Name)),
                Many(file => file.Recording!.Work!.Parent!.Contributions.Select(credit => credit.Name)),
            ]
            :
            [
                Many(file => file.Recording!.Contributions.Where(credit => roles.Contains(credit.Type)).Select(credit => credit.Name)),
                Many(file => file.Recording!.Work!.Contributions.Where(credit => roles.Contains(credit.Type)).Select(credit => credit.Name)),
                Many(file => file.Recording!.Work!.Parent!.Contributions.Where(credit => roles.Contains(credit.Type)).Select(credit => credit.Name)),
            ];

    /// <remarks>
    /// Matched with <c>ILIKE</c>, its wildcards escaped, over the value or ""
    /// — never over null, whose <c>NOT</c> is null too and would drop a song
    /// with no genre from "genre is not jazz". Where one column holds several
    /// values, each is a word between separators: "is Live" is not "is
    /// Live Album".
    /// </remarks>
    private static Expression<Func<MediaFile, bool>> Text(IReadOnlyList<Source> sources, SmartOperator op, string value)
    {
        var file = Expression.Parameter(typeof(MediaFile), "file");
        var escaped = Escape(value);

        if (op is SmartOperator.IsEmpty or SmartOperator.IsNotEmpty)
        {
            Expression<Func<string?, bool>> filled = text => text != null && text != "";
            var any = sources.Select(source => Test(source, file, (_, text) => Bind(filled, text))).Aggregate(Expression.OrElse);

            return Expression.Lambda<Func<MediaFile, bool>>(op == SmartOperator.IsNotEmpty ? any : Expression.Not(any), file);
        }

        var positive = op is SmartOperator.IsNot ? SmartOperator.Is
            : op is SmartOperator.DoesNotContain ? SmartOperator.Contains
            : op;

        var matched = sources
            .Select(source => Test(source, file, (separator, text) =>
            {
                var (subject, pattern) = Pattern(positive, separator, escaped, text);
                return Expression.Call(
                    typeof(NpgsqlDbFunctionsExtensions),
                    nameof(NpgsqlDbFunctionsExtensions.ILike),
                    null,
                    Expression.Property(null, typeof(EF), nameof(EF.Functions)),
                    subject,
                    Expression.Constant(pattern),
                    Expression.Constant("\\"));
            }))
            .Aggregate(Expression.OrElse);

        return Expression.Lambda<Func<MediaFile, bool>>(
            op is SmartOperator.IsNot or SmartOperator.DoesNotContain ? Expression.Not(matched) : matched,
            file);
    }

    /// <summary>What a test of one source's text says of the file: of the value, or of any of the values.</summary>
    private static Expression Test(Source source, ParameterExpression file, Func<string?, Expression, Expression> test)
    {
        var values = Rebind(source.Values.Parameters[0], file, source.Values.Body);

        if (!source.Many) return test(source.Separator, values);

        var text = Expression.Parameter(typeof(string), "text");
        var predicate = Expression.Lambda<Func<string?, bool>>(test(source.Separator, text), text);

        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(string)], values, predicate);
    }

    /// <summary>The string <c>ILIKE</c> is asked of, and the pattern; with a separator, each value between two.</summary>
    private static (Expression Subject, string Pattern) Pattern(SmartOperator op, string? separator, string escaped, Expression text)
    {
        var coalesced = Expression.Coalesce(text, Expression.Constant(string.Empty));

        if (separator is null || op == SmartOperator.Contains)
        {
            return (coalesced, op switch
            {
                SmartOperator.Is => escaped,
                SmartOperator.StartsWith => escaped + "%",
                SmartOperator.EndsWith => "%" + escaped,
                _ => "%" + escaped + "%",
            });
        }

        var gap = Escape(separator);
        var concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string), typeof(string)])!;
        var wrapped = Expression.Call(concat, Expression.Constant(separator), coalesced, Expression.Constant(separator));

        return (wrapped, op switch
        {
            SmartOperator.Is => $"%{gap}{escaped}{gap}%",
            SmartOperator.StartsWith => $"%{gap}{escaped}%",
            _ => $"%{escaped}{gap}%",
        });
    }

    private static Expression<Func<MediaFile, bool>> Number(SmartField field, SmartOperator op, double? value, double? to)
    {
        return field switch
        {
            SmartField.Year => Compare(
                file => file.ReleaseGroup == null
                    ? null
                    : file.ReleaseGroup.FirstReleaseYear
                        ?? file.ReleaseGroup.Releases.Where(release => release.Status == "Official").Min(release => release.ReleasedYear)
                        ?? file.ReleaseGroup.Releases.Min(release => release.ReleasedYear),
                op, Whole(value), Whole(to)),
            SmartField.PressingYear => Compare(file => file.Release!.ReleasedYear, op, Whole(value), Whole(to)),
            SmartField.Length => Compare(
                file => file.Quality!.Duration ?? file.Recording!.Duration,
                op,
                value is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                to is { } most ? TimeSpan.FromSeconds(most) : null),
            SmartField.BitDepth => Compare(
                file => file.Quality!.IsLossless && file.Measurement!.EffectiveBitDepth > 0 && file.Measurement.EffectiveBitDepth < file.Quality.BitDepth
                    ? file.Measurement.EffectiveBitDepth
                    : file.Quality!.BitDepth,
                op, Whole(value), Whole(to)),
            SmartField.SampleRate => Compare(file => (int?)file.Quality!.SampleRateHz, op, Whole(value * 1000), Whole(to * 1000)),
            SmartField.Bitrate => Compare(
                file => (long?)file.Quality!.BitrateBps,
                op,
                value is { } kbps ? (long)Math.Round(kbps * 1000) : null,
                to is { } most ? (long)Math.Round(most * 1000) : null),
            SmartField.Channels => Compare(file => (int?)file.Quality!.Channels, op, Whole(value), Whole(to)),
            SmartField.DynamicRange => Compare(file => file.Measurement!.DynamicRange, op, value, to),
            SmartField.Loudness => Compare(file => file.Measurement!.IntegratedLufs, op, value, to),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a number field."),
        };

        static int? Whole(double? number) => number is { } n ? (int)Math.Round(n) : null;
    }

    /// <summary>A comparison of a nullable column; a missing value matches nothing but "is not" and "is empty".</summary>
    private static Expression<Func<MediaFile, bool>> Compare<T>(
        Expression<Func<MediaFile, T?>> column,
        SmartOperator op,
        T? value,
        T? to)
        where T : struct
    {
        var file = column.Parameters[0];
        var body = column.Body;
        var low = Expression.Constant(value, typeof(T?));

        Expression test = op switch
        {
            SmartOperator.IsEmpty => Expression.Equal(body, Expression.Constant(null, typeof(T?))),
            SmartOperator.IsNotEmpty => Expression.NotEqual(body, Expression.Constant(null, typeof(T?))),
            SmartOperator.Is => Expression.Equal(body, low),
            SmartOperator.IsNot => Expression.NotEqual(body, low),
            SmartOperator.AtLeast => Expression.GreaterThanOrEqual(body, low),
            SmartOperator.AtMost => Expression.LessThanOrEqual(body, low),
            _ => Expression.AndAlso(
                Expression.GreaterThanOrEqual(body, Expression.Constant(Min(value, to), typeof(T?))),
                Expression.LessThanOrEqual(body, Expression.Constant(Max(value, to), typeof(T?)))),
        };

        return Expression.Lambda<Func<MediaFile, bool>>(test, file);

        static T? Min(T? left, T? right) => Comparer<T?>.Default.Compare(left, right) <= 0 ? left : right;

        static T? Max(T? left, T? right) => Comparer<T?>.Default.Compare(left, right) <= 0 ? right : left;
    }

    private static Expression<Func<MediaFile, bool>> Flag(SmartField field, bool wanted)
    {
        Expression<Func<MediaFile, bool>> test = field switch
        {
            SmartField.Lossless => wanted
                ? file => file.Quality != null && file.Quality.IsLossless
                : file => file.Quality != null && !file.Quality.IsLossless,
            SmartField.HiRes => file => file.Quality != null
                && file.Quality.IsLossless
                && ((file.Measurement != null && file.Measurement.EffectiveBitDepth > 0 && file.Measurement.EffectiveBitDepth < file.Quality.BitDepth
                        ? file.Measurement.EffectiveBitDepth
                        : file.Quality.BitDepth) > 16
                    || file.Quality.SampleRateHz > 48_000),
            SmartField.Followed => file => file.Recording!.Credits.Any(credit => credit.Artist!.Followed)
                || file.ReleaseGroup!.Credits.Any(credit => credit.Artist!.Followed),
            SmartField.Damaged => file => file.Integrity == IntegrityState.Corrupt
                || file.Integrity == IntegrityState.Truncated
                || file.Integrity == IntegrityState.Unreadable,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a flag."),
        };

        // Lossless answers both ways itself, since an unprobed file is neither.
        return wanted || field == SmartField.Lossless
            ? test
            : Expression.Lambda<Func<MediaFile, bool>>(Expression.Not(test.Body), test.Parameters);
    }

    /// <remarks>
    /// A file's id is a UUIDv7 minted when the scan first saw it, so "added
    /// since" is "id at least" the smallest v7 of that millisecond — compared
    /// as a row, the one way Npgsql compares a uuid by order.
    /// </remarks>
    private static Expression<Func<MediaFile, bool>> Added(SmartOperator op, int days, DateTimeOffset now)
    {
        var since = new MediaFileId(Earliest(now.AddDays(-days)));

        return op == SmartOperator.InTheLast
            ? file => EF.Functions.GreaterThanOrEqual(ValueTuple.Create(file.Id), ValueTuple.Create(since))
            : file => EF.Functions.LessThan(ValueTuple.Create(file.Id), ValueTuple.Create(since));
    }

    /// <summary>The lowest UUIDv7 of a millisecond: its timestamp, every other bit clear.</summary>
    internal static Guid Earliest(DateTimeOffset moment)
    {
        Span<byte> bytes = stackalloc byte[16];
        Span<byte> milliseconds = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(milliseconds, moment.ToUnixTimeMilliseconds());
        milliseconds[2..].CopyTo(bytes);

        return new Guid(bytes, bigEndian: true);
    }

    private static Expression<Func<MediaFile, bool>> Combine(
        Expression<Func<MediaFile, bool>> left,
        Expression<Func<MediaFile, bool>> right,
        bool any)
    {
        var file = left.Parameters[0];
        var body = Rebind(right.Parameters[0], file, right.Body);

        return Expression.Lambda<Func<MediaFile, bool>>(any ? Expression.OrElse(left.Body, body) : Expression.AndAlso(left.Body, body), file);
    }

    private static Expression Bind(Expression<Func<string?, bool>> test, Expression text) =>
        Rebind(test.Parameters[0], text, test.Body);

    private static Expression Rebind(ParameterExpression from, Expression to, Expression body) =>
        new Replace(from, to).Visit(body);

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Known values in order, turned round where asked; unknown last either way round.</summary>
    private static Comparer<T?> Known<T>(Func<T, T, int> compare, bool reverse) =>
        Comparer<T?>.Create((left, right) =>
            left is null ? (right is null ? 0 : 1)
            : right is null ? -1
            : reverse ? compare(right, left) : compare(left, right));

    /// <summary>A song's place in the shuffle: a hash of the seed and the recording, the same every time it is asked.</summary>
    private static byte[] Deal(string seed, RecordingId recording) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(seed + recording.Value.ToString("N")));

    private static int Bytes(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);
}

/// <summary>One song a smart playlist holds: the copy that plays it, and what it is ordered by.</summary>
/// <param name="Added">The newest of its matching files' ids, as hex that orders by when the scan saw them.</param>
internal sealed record SmartSong(
    RecordingId RecordingId,
    PlaylistCopy Copy,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    TimeSpan? Duration,
    string Added);
