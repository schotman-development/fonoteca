using System.Globalization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What a folder of files nobody could identify says about itself: one album,
/// read from their own tags.
/// </summary>
/// <remarks>
/// <b>One album per album folder</b>, the owner's choice: a folder of six
/// covers, each tagged as its own single, is one album of six. It is named by
/// the album tag where every file states the same one, and by the folder
/// otherwise; it is billed to the album artist every file states, else to the
/// artist every file states, else to nobody.
///
/// <b>A song is what its file says</b>: its title, else the file's name; its
/// artists, else the album's; its place, where every file is numbered and no
/// two share a slot, else the folder's order by name.
///
/// <b>Only the genres the catalogue knows</b> (<see cref="Genres"/>), spelt in
/// lower case as MusicBrainz spells them: a tagger's "International Pop" folds
/// into nothing and would read as Other everywhere.
/// </remarks>
public static class TagAlbums
{
    /// <summary>The disc a file states no number for.</summary>
    private const int FirstDisc = 1;

    /// <summary>Between the names of one tag holding several, as taggers write them.</summary>
    private static readonly char[] ValueSeparators = [';', CatalogueTags.ValueSeparator];

    public static TagAlbum Of(string folder, IReadOnlyList<TagAlbumFile> files)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(files);

        var ordered = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

        var albumBilling = Shared(ordered, file => Credits(file.Tags.AlbumArtist, file.Tags.AlbumArtistIds))
            ?? Shared(ordered, file => Credits(file.Tags.Artist, file.Tags.ArtistIds))
            ?? [];

        var slots = Slots(ordered);

        var songs = ordered
            .Select((file, index) => new TagSong(
                file.Id,
                Text(file.Tags.Title) ?? FileName(file.Path),
                Credits(file.Tags.Artist, file.Tags.ArtistIds) is { Count: > 0 } own ? own : albumBilling,
                slots[index].Disc,
                slots[index].Position,
                KnownGenres(file.Tags.Genre)))
            .ToList();

        return new TagAlbum(
            SharedText(ordered, file => file.Tags.Album) ?? LastSegment(folder),
            albumBilling,
            SharedNumber(ordered, file => Year(file.Tags.Year)),
            songs);
    }

    /// <summary>Every file's own disc and track, where they are complete and distinct; the folder's order otherwise.</summary>
    private static List<(int Disc, int Position)> Slots(IReadOnlyList<TagAlbumFile> files)
    {
        var stated = files
            .Select(file => (Disc: Number(file.Tags.Disc) ?? FirstDisc, Position: Number(file.Tags.Track)))
            .ToList();

        if (stated.All(slot => slot.Position is not null) && stated.Distinct().Count() == stated.Count)
        {
            return [.. stated.Select(slot => (slot.Disc, slot.Position!.Value))];
        }

        return [.. files.Select((_, index) => (FirstDisc, index + 1))];
    }

    /// <summary>A billing line: each name its tag holds, with the MBID at the same place where the ids line up.</summary>
    private static List<TagCredit> Credits(string? names, string? ids)
    {
        var named = Values(names);
        var identified = Values(ids)
            .Select(id => Guid.TryParse(id, out var value) ? new Mbid(value) : (Mbid?)null)
            .ToList();

        var aligned = identified.Count == named.Count;

        return
        [
            .. named.Select((name, index) => new TagCredit(
                name,
                aligned ? identified[index] : null,
                index,
                index == named.Count - 1 ? null : index == named.Count - 2 ? " & " : ", ")),
        ];
    }

    private static List<string> KnownGenres(string? genre) =>
    [
        .. Values(genre)
            .Where(name => Genres.Of(name) != Genres.Other)
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The value every file states alike, or null where one states none or another.</summary>
    private static List<TagCredit>? Shared(IReadOnlyList<TagAlbumFile> files, Func<TagAlbumFile, List<TagCredit>> read)
    {
        var lines = files.Select(read).ToList();

        return lines.Count > 0 && lines[0].Count > 0 && lines.All(line => line.SequenceEqual(lines[0])) ? lines[0] : null;
    }

    private static string? SharedText(IReadOnlyList<TagAlbumFile> files, Func<TagAlbumFile, string?> read)
    {
        var values = files.Select(file => Text(read(file))).Distinct(StringComparer.Ordinal).ToList();

        return values is [{ } only] ? only : null;
    }

    private static int? SharedNumber(IReadOnlyList<TagAlbumFile> files, Func<TagAlbumFile, int?> read) =>
        files.Select(read).Distinct().ToList() is [{ } only] ? only : null;

    private static List<string> Values(string? joined) =>
    [
        .. (joined ?? string.Empty)
            .Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal),
    ];

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>"3" or "3/12".</summary>
    private static int? Number(string? value)
    {
        var whole = (value ?? string.Empty).Split('/')[0].Trim();

        return int.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    /// <summary>"2019", or the year of "2019-05-01".</summary>
    private static int? Year(string? value) =>
        value is { Length: >= 4 }
        && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year > 0
            ? year
            : null;

    private static string FileName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string LastSegment(string folder) => folder.TrimEnd('/').Split('/')[^1];
}

/// <summary>What one file's tags say, as read.</summary>
/// <param name="ArtistIds">Its <c>MUSICBRAINZ_ARTISTID</c>, one per name in <paramref name="Artist"/>.</param>
public sealed record TagReading(
    string? Title = null,
    string? Artist = null,
    string? ArtistIds = null,
    string? AlbumArtist = null,
    string? AlbumArtistIds = null,
    string? Album = null,
    string? Track = null,
    string? Disc = null,
    string? Year = null,
    string? Genre = null);

public sealed record TagAlbumFile(MediaFileId Id, string Path, TagReading Tags);

/// <summary>One name on a billing line; <paramref name="Mbid"/> is the file's own claim about who.</summary>
public sealed record TagCredit(string Name, Mbid? Mbid, int Position, string? JoinPhrase);

public sealed record TagSong(
    MediaFileId Id,
    string Title,
    IReadOnlyList<TagCredit> Billing,
    int Disc,
    int Position,
    IReadOnlyList<string> Genres);

public sealed record TagAlbum(string Title, IReadOnlyList<TagCredit> Billing, int? Year, IReadOnlyList<TagSong> Songs)
{
    public int DiscCount => Songs.Select(song => song.Disc).DefaultIfEmpty(1).Max();
}
