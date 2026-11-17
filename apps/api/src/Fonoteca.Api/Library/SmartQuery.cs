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
    /// <param name="owner">Whose hearts, plays and tags the rules read; null reads nobody's.</param>
    internal static async Task<List<SmartSong>> SongsAsync(
        FonotecaDbContext db,
        SmartCriteria criteria,
        string seed,
        DateTimeOffset now,
        Guid? owner,
        CancellationToken cancellationToken)
    {
        var stated = await CatalogueEndpoints.StatedGenresAsync(db, cancellationToken).ConfigureAwait(false);

        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null)
            .Where(Predicate(criteria.Rules, now, stated, Owning(db, owner)))
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

    /// <summary>One account's hearts, plays and tags, past the context's own filter: the playlist's owner, not whoever is asking.</summary>
    internal sealed record Owned(IQueryable<Love> Loves, IQueryable<Play> Plays, IQueryable<SongTag> SongTags);

    /// <remarks>An owner of nobody is the empty id, which no account has.</remarks>
    internal static Owned Owning(FonotecaDbContext db, Guid? owner)
    {
        var account = owner ?? Guid.Empty;

        return new Owned(
            db.Loves.IgnoreQueryFilters().Where(love => love.AccountId == account),
            db.Plays.IgnoreQueryFilters().Where(play => play.AccountId == account),
            db.SongTags.IgnoreQueryFilters().Where(songTag => songTag.Tag!.AccountId == account));
    }

    /// <summary>A condition, or a group folded from its rules; an empty group matches every file.</summary>
    /// <param name="stated">The genre names the library states, which a genre condition is asked of (<see cref="Genre"/>).</param>
    internal static Expression<Func<MediaFile, bool>> Predicate(SmartRule rule, DateTimeOffset now, IReadOnlyCollection<string> stated, Owned owned)
    {
        if (rule.Rules is { } children)
        {
            var parts = children.Select(child => Predicate(child, now, stated, owned)).ToList();

            if (parts.Count == 0) return file => true;

            return parts.Aggregate((left, right) => Combine(left, right, rule.Match == SmartMatch.Any));
        }

        var field = rule.Field!.Value;
        var op = rule.Operator!.Value;

        return SmartRules.KindOf(field) switch
        {
            SmartFieldKind.Text when field == SmartField.Genre && op is not (SmartOperator.IsEmpty or SmartOperator.IsNotEmpty) =>
                Genre(op, rule.Value?.Trim() ?? string.Empty, stated),
            SmartFieldKind.Text => Text(Sources(field, owned), op, rule.Value?.Trim() ?? string.Empty),
            SmartFieldKind.Number => Number(field, op, SmartRules.Number(rule.Value), SmartRules.Number(rule.To), owned),
            SmartFieldKind.Flag => Flag(field, op == SmartOperator.IsTrue, owned),
            _ => Since(field, op, (int)SmartRules.Number(rule.Value)!.Value, now, owned),
        };
    }

    /// <summary>
    /// Where a text field's values come from: one column, or a list of them,
    /// and the separator where one column holds several joined.
    /// </summary>
    private sealed record Source(LambdaExpression Values, bool Many, string? Separator = null);

    private static Source One(Expression<Func<MediaFile, string?>> value, string? separator = null) => new(value, false, separator);

    private static Source Many(Expression<Func<MediaFile, IEnumerable<string?>>> values) => new(values, true);

    private static IReadOnlyList<Source> Sources(SmartField field, Owned owned) => field switch
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
        SmartField.Lyrics => [One(file => file.Recording!.Lyrics!.Plain)],
        SmartField.TimedLyrics => [One(file => file.Recording!.Lyrics!.Synced)],
        SmartField.Composed =>
        [
            One(file => file.Recording!.Work!.ComposedBegin),
            One(file => file.Recording!.Work!.ComposedEnd),
            One(file => file.Recording!.Work!.Parent!.ComposedBegin),
            One(file => file.Recording!.Work!.Parent!.ComposedEnd),
        ],
        SmartField.Tag =>
        [
            Many(file => owned.SongTags
                .Where(songTag => (file.RecordingId != null && songTag.RecordingId == file.RecordingId) || songTag.MediaFileId == file.Id)
                .Select(songTag => songTag.Tag!.Name)),
        ],
        SmartField.WorkKey => [One(file => file.Recording!.Work!.Key), One(file => file.Recording!.Work!.Parent!.Key)],
        SmartField.WorkType => [One(file => file.Recording!.Work!.Type), One(file => file.Recording!.Work!.Parent!.Type)],
        SmartField.WorkLanguage =>
        [
            One(file => file.Recording!.Work!.Languages, ", "),
            One(file => file.Recording!.Work!.Parent!.Languages, ", "),
        ],
        SmartField.AlbumType => [One(file => file.ReleaseGroup!.PrimaryType), One(file => file.ReleaseGroup!.SecondaryTypes, ", ")],
        SmartField.Label => [One(file => file.Release!.Label)],
        SmartField.ReleaseCountry => [One(file => file.Release!.Country)],
        SmartField.MediumFormat => [One(file => file.Release!.MediumFormats, "+")],
        SmartField.Barcode => [One(file => file.Release!.Barcode)],
        SmartField.CatalogNumber => [One(file => file.Release!.CatalogNumber)],
        SmartField.ReleaseStatus => [One(file => file.Release!.Status)],
        SmartField.Edition => [One(file => file.Release!.Disambiguation)],
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
        SmartField.ArtistGender =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Gender)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.Gender)),
        ],
        SmartField.ArtistSortName =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.SortName)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.SortName)),
        ],
        SmartField.ArtistDisambiguation =>
        [
            Many(file => file.Recording!.Credits.Select(credit => credit.Artist!.Disambiguation)),
            Many(file => file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.Disambiguation)),
        ],
        SmartField.Key => [One(file => file.TempoAndKey!.Key), One(file => file.TempoAndKey!.Key + " " + file.TempoAndKey.Scale)],
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

    /// <summary>
    /// A genre condition, asked of the genres a file carries (<see cref="Genres"/>)
    /// while the catalogue stores MusicBrainz's.
    /// </summary>
    /// <remarks>
    /// The library states few names, so which of them carry a matching genre is
    /// worked out here, and SQL asks only whether the file's stored list holds
    /// one of those whole. "Blues" therefore matches a file stating "chicago
    /// blues", and "Other" one stating a name the tree has not placed. A value
    /// in MusicBrainz's words is folded first, as the files' genres are.
    /// </remarks>
    private static Expression<Func<MediaFile, bool>> Genre(SmartOperator op, string value, IReadOnlyCollection<string> stated)
    {
        if (Genres.Of(value) is var folded && folded != Genres.Other) value = folded;

        var positive = op is SmartOperator.IsNot ? SmartOperator.Is
            : op is SmartOperator.DoesNotContain ? SmartOperator.Contains
            : op;

        var patterns = Genres
            .Stating(stated, genre => positive switch
            {
                SmartOperator.Is => genre.Equals(value, StringComparison.OrdinalIgnoreCase),
                SmartOperator.StartsWith => genre.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                SmartOperator.EndsWith => genre.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                _ => genre.Contains(value, StringComparison.OrdinalIgnoreCase),
            })
            .Select(name => $"%, {Escape(name)}, %")
            .ToArray();

        Expression<Func<MediaFile, bool>> matched = file => patterns.Any(pattern => EF.Functions.ILike(
            ", "
                + ((file.ReleaseGroup != null && file.ReleaseGroup.Genres != null && file.ReleaseGroup.Genres != ""
                    ? file.ReleaseGroup.Genres
                    : file.Recording!.Genres) ?? string.Empty)
                + ", ",
            pattern));

        return op is SmartOperator.IsNot or SmartOperator.DoesNotContain
            ? Expression.Lambda<Func<MediaFile, bool>>(Expression.Not(matched.Body), matched.Parameters)
            : matched;
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

    private static Expression<Func<MediaFile, bool>> Number(SmartField field, SmartOperator op, double? value, double? to, Owned owned)
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
            SmartField.LoudnessRange => Compare(file => file.Measurement!.LoudnessRangeLu, op, value, to),
            SmartField.TruePeak => Compare(file => file.Measurement!.TruePeakDbtp, op, value, to),
            SmartField.SamplePeak => Compare(file => file.Measurement!.SamplePeakDbfs, op, value, to),
            SmartField.LeadingSilence => Compare(file => (double?)file.Measurement!.LeadingSilenceSeconds, op, value, to),
            SmartField.TrailingSilence => Compare(file => (double?)file.Measurement!.TrailingSilenceSeconds, op, value, to),
            SmartField.Bpm => Compare(
                file => file.TempoAndKey != null && file.TempoAndKey.BpmConfidence >= TempoAndKey.MinimumBpmConfidence
                    ? (double?)file.TempoAndKey.Bpm
                    : null,
                op, value, to),
            SmartField.FileSize => Compare(
                file => (long?)file.SizeBytes,
                op,
                value is { } megabytes ? (long)Math.Round(megabytes * 1_000_000) : null,
                to is { } most ? (long)Math.Round(most * 1_000_000) : null),
            SmartField.DiscNumber => Compare(file => (int?)file.Track!.DiscNumber, op, Whole(value), Whole(to)),
            SmartField.TrackNumber => Compare(file => (int?)file.Track!.Position, op, Whole(value), Whole(to)),
            SmartField.TrackCount => Compare(file => file.Release!.TrackCount, op, Whole(value), Whole(to)),
            SmartField.DiscCount => Compare(file => file.Release!.DiscCount, op, Whole(value), Whole(to)),
            SmartField.PressingMonth => Compare(file => file.Release!.ReleasedMonth, op, Whole(value), Whole(to)),
            SmartField.PressingDay => Compare(file => file.Release!.ReleasedDay, op, Whole(value), Whole(to)),
            SmartField.FileDiscNumber => Compare(file => file.TagDiscNumber, op, Whole(value), Whole(to)),
            SmartField.FileTrackNumber => Compare(file => file.TagTrackNumber, op, Whole(value), Whole(to)),
            SmartField.ArtistBeganYear => CompareAny(
                file => file.Recording!.Credits.Select(credit => credit.Artist!.BeganYear)
                    .Concat(file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.BeganYear)),
                op, Whole(value), Whole(to)),
            SmartField.ArtistEndedYear => CompareAny(
                file => file.Recording!.Credits.Select(credit => credit.Artist!.EndedYear)
                    .Concat(file.ReleaseGroup!.Credits.Select(credit => credit.Artist!.EndedYear)),
                op, Whole(value), Whole(to)),
            SmartField.Plays => Compare(
                file => (int?)owned.Plays.Count(play => file.RecordingId != null
                    && (play.MediaFile != null ? play.MediaFile.RecordingId : play.RecordingId) == file.RecordingId),
                op, Whole(value), Whole(to)),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a number field."),
        };

        static int? Whole(double? number) => number is { } n ? (int)Math.Round(n) : null;
    }

    /// <summary>A comparison of a nullable column; a missing value matches nothing but "is not" and "is unknown".</summary>
    private static Expression<Func<MediaFile, bool>> Compare<T>(
        Expression<Func<MediaFile, T?>> column,
        SmartOperator op,
        T? value,
        T? to)
        where T : struct
    {
        return Expression.Lambda<Func<MediaFile, bool>>(Test(column.Body, op, value, to), column.Parameters[0]);
    }

    /// <summary>A comparison of every value a file has, such as its billed artists': true where any one answers.</summary>
    private static Expression<Func<MediaFile, bool>> CompareAny<T>(
        Expression<Func<MediaFile, IEnumerable<T?>>> values,
        SmartOperator op,
        T? value,
        T? to)
        where T : struct
    {
        var each = Expression.Parameter(typeof(T?), "each");
        var predicate = Expression.Lambda<Func<T?, bool>>(Test(each, op, value, to), each);
        var any = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(T?)], values.Body, predicate);

        return Expression.Lambda<Func<MediaFile, bool>>(any, values.Parameters[0]);
    }

    private static BinaryExpression Test<T>(Expression body, SmartOperator op, T? value, T? to)
        where T : struct
    {
        var low = Expression.Constant(value, typeof(T?));

        return op switch
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

        static T? Min(T? left, T? right) => Comparer<T?>.Default.Compare(left, right) <= 0 ? left : right;

        static T? Max(T? left, T? right) => Comparer<T?>.Default.Compare(left, right) <= 0 ? right : left;
    }

    private static Expression<Func<MediaFile, bool>> Flag(SmartField field, bool wanted, Owned owned)
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
            SmartField.Clipped => wanted
                ? file => file.Measurement != null && file.Measurement.SamplePeakDbfs != null
                    && (file.Measurement.SamplePeakDbfs > 0.01
                        || (file.Measurement.SamplePeakDbfs >= -0.01 && file.Measurement.PeakCount >= 8))
                : file => file.Measurement != null && file.Measurement.SamplePeakDbfs != null
                    && !(file.Measurement.SamplePeakDbfs > 0.01
                        || (file.Measurement.SamplePeakDbfs >= -0.01 && file.Measurement.PeakCount >= 8)),
            SmartField.Loved => file => owned.Loves.Any(love =>
                (file.RecordingId != null && love.RecordingId == file.RecordingId) || love.MediaFileId == file.Id),
            SmartField.AlbumLoved => file => file.ReleaseGroupId != null
                && owned.Loves.Any(love => love.ReleaseGroupId == file.ReleaseGroupId),
            SmartField.ArtistLoved => file => owned.Loves.Any(love =>
                file.Recording!.Credits.Any(credit => credit.ArtistId == love.ArtistId)
                || file.ReleaseGroup!.Credits.Any(credit => credit.ArtistId == love.ArtistId)),
            SmartField.WorkLoved => file => file.Recording!.WorkId != null
                && owned.Loves.Any(love => love.WorkId == file.Recording.WorkId
                    || (file.Recording.Work!.ParentId != null && love.WorkId == file.Recording.Work.ParentId)),
            SmartField.Instrumental => file => file.Recording!.Lyrics != null && file.Recording.Lyrics.Instrumental,
            SmartField.Monitored => file => file.ReleaseGroup != null && file.ReleaseGroup.Monitored,
            SmartField.ArtistEnded => file => file.Recording!.Credits.Any(credit => credit.Artist!.Ended)
                || file.ReleaseGroup!.Credits.Any(credit => credit.Artist!.Ended),
            SmartField.Damaged => file => file.Integrity == IntegrityState.Corrupt
                || file.Integrity == IntegrityState.Truncated
                || file.Integrity == IntegrityState.Unreadable,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a flag."),
        };

        // These answer both ways themselves, since an unprobed or unmeasured file is neither.
        return wanted || field is SmartField.Lossless or SmartField.Clipped
            ? test
            : Expression.Lambda<Func<MediaFile, bool>>(Expression.Not(test.Body), test.Parameters);
    }

    private static Expression<Func<MediaFile, bool>> Since(SmartField field, SmartOperator op, int days, DateTimeOffset now, Owned owned)
    {
        if (field == SmartField.Added) return Added(op, days, now);

        var since = now.AddDays(-days);

        Expression<Func<MediaFile, bool>> within = field switch
        {
            SmartField.Modified => file => file.LastModifiedUtc >= since,
            SmartField.LovedWhen => file => owned.Loves.Any(love => love.LovedUtc >= since
                && ((file.RecordingId != null && love.RecordingId == file.RecordingId) || love.MediaFileId == file.Id)),
            SmartField.Played => file => owned.Plays.Any(play => play.PlayedUtc >= since
                && file.RecordingId != null
                && (play.MediaFile != null ? play.MediaFile.RecordingId : play.RecordingId) == file.RecordingId),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a date field."),
        };

        return op == SmartOperator.InTheLast
            ? within
            : Expression.Lambda<Func<MediaFile, bool>>(Expression.Not(within.Body), within.Parameters);
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
