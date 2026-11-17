using System.Linq.Expressions;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

internal static partial class SmartQuery
{
    /// <summary>The order only <see cref="SongsAsync"/> can name without a query; null where the sort is a field's.</summary>
    private static SmartSort? Legacy(SmartCriteria criteria) => criteria.SortField switch
    {
        null => criteria.Sort,
        SmartField.Title => SmartSort.Title,
        SmartField.Artist => SmartSort.Artist,
        SmartField.Album => SmartSort.Album,
        SmartField.Year => SmartSort.Year,
        SmartField.Length => SmartSort.Length,
        SmartField.Added => SmartSort.Added,
        _ => null,
    };

    private sealed record Keyed<T>(MediaFileId Id, T Key);

    /// <summary>
    /// The songs in the order of any field, read from the copy that plays each.
    /// </summary>
    /// <remarks>
    /// The key is asked of the database in one more query over those copies,
    /// built from the same expressions the conditions use, so what a field
    /// sorts by is what it is matched on. A field with several values (the
    /// artists billed, the credits) sorts by its first alphabetically.
    /// </remarks>
    private static async Task<IOrderedEnumerable<SmartSong>> ByFieldAsync(
        FonotecaDbContext db,
        List<SmartSong> songs,
        SmartField field,
        bool reverse,
        Owned owned,
        CancellationToken cancellationToken)
    {
        var ids = songs.Select(song => song.Copy.Id).ToList();
        var copies = db.MediaFiles.AsNoTracking().Where(file => ids.Contains(file.Id));
        var culture = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;

        switch (SmartRules.KindOf(field))
        {
            case SmartFieldKind.Text:
                var words = await KeysAsync(copies, TextKey(field, owned), cancellationToken).ConfigureAwait(false);
                return songs
                    .OrderBy(song => words.GetValueOrDefault(song.Copy.Id), Known<string>((left, right) => culture.Compare(left, right, System.Globalization.CompareOptions.IgnoreCase), reverse))
                    .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase);

            case SmartFieldKind.Date:
                var moments = await KeysAsync(copies, DateKey(field, owned), cancellationToken).ConfigureAwait(false);
                return songs
                    .OrderBy(song => moments.GetValueOrDefault(song.Copy.Id), Known<DateTimeOffset?>((left, right) => left!.Value.CompareTo(right!.Value), !reverse))
                    .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase);

            default:
                var numbers = await KeysAsync(copies, NumberKey(field, owned), cancellationToken).ConfigureAwait(false);
                return songs
                    .OrderBy(song => numbers.GetValueOrDefault(song.Copy.Id), Known<double?>((left, right) => left!.Value.CompareTo(right!.Value), reverse))
                    .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task<Dictionary<MediaFileId, T>> KeysAsync<T>(
        IQueryable<MediaFile> copies,
        Expression<Func<MediaFile, T>> key,
        CancellationToken cancellationToken)
    {
        var file = key.Parameters[0];
        var projection = Expression.Lambda<Func<MediaFile, Keyed<T>>>(
            Expression.New(typeof(Keyed<T>).GetConstructors()[0], Expression.Property(file, nameof(MediaFile.Id)), key.Body),
            file);

        var keyed = await copies.Select(projection).ToListAsync(cancellationToken).ConfigureAwait(false);

        return keyed.ToDictionary(row => row.Id, row => row.Key);
    }

    /// <summary>The first of a text field's values, alphabetically, from the first source that has one.</summary>
    private static Expression<Func<MediaFile, string?>> TextKey(SmartField field, Owned owned)
    {
        var file = Expression.Parameter(typeof(MediaFile), "file");

        var first = Sources(field, owned)
            .Select(source =>
            {
                var values = Rebind(source.Values.Parameters[0], file, source.Values.Body);

                return source.Many
                    ? Expression.Call(typeof(Enumerable), nameof(Enumerable.Min), [typeof(string)], values)
                    : values;
            })
            .Aggregate(Expression.Coalesce);

        return Expression.Lambda<Func<MediaFile, string?>>(first, file);
    }

    /// <summary>A number, or a flag as 0 and 1.</summary>
    private static Expression<Func<MediaFile, double?>> NumberKey(SmartField field, Owned owned)
    {
        if (SmartRules.KindOf(field) == SmartFieldKind.Flag)
        {
            var flag = Flag(field, true, owned);

            return Expression.Lambda<Func<MediaFile, double?>>(
                Expression.Convert(Expression.Condition(flag.Body, Expression.Constant(1.0), Expression.Constant(0.0)), typeof(double?)),
                flag.Parameters);
        }

        if (field is SmartField.ArtistBeganYear or SmartField.ArtistEndedYear)
        {
            var years = ArtistYears(field);

            return Expression.Lambda<Func<MediaFile, double?>>(
                Expression.Convert(
                    Expression.Call(typeof(Enumerable), nameof(Enumerable.Min), [typeof(int?)], years.Body),
                    typeof(double?)),
                years.Parameters);
        }

        if (field == SmartField.Length) throw new ArgumentOutOfRangeException(nameof(field), field, "Length is ordered without a query.");

        return Column(field, owned).Value;
    }

    private static Expression<Func<MediaFile, DateTimeOffset?>> DateKey(SmartField field, Owned owned) => field switch
    {
        SmartField.Modified => file => file.LastModifiedUtc,
        SmartField.LovedWhen => file => owned.Loves
            .Where(love => (file.RecordingId != null && love.RecordingId == file.RecordingId) || love.MediaFileId == file.Id)
            .Max(love => (DateTimeOffset?)love.LovedUtc),
        SmartField.Played => file => owned.Plays
            .Where(play => file.RecordingId != null
                && (play.MediaFile != null ? play.MediaFile.RecordingId : play.RecordingId) == file.RecordingId)
            .Max(play => (DateTimeOffset?)play.PlayedUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a date field."),
    };
}
