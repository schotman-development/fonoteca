namespace Fonoteca.Domain.Catalogue;

/// <summary>What one file adds to the library's totals.</summary>
public sealed record OverviewFile(AudioQuality? Quality, AudioMeasurement? Measurement);

/// <summary>What one held album adds to the library's totals.</summary>
public sealed record OverviewAlbum(int? Year, string? Genres);

public sealed record FileTotals(
    IReadOnlyList<(QualityTier Tier, int Files)> Tiers,
    IReadOnlyList<(string Codec, int Files)> Codecs);

public sealed record AlbumTotals(
    IReadOnlyList<(int Decade, int Albums)> Decades,
    int Undated,
    IReadOnlyList<(string Genre, int Albums)> Genres);

/// <summary>The library in totals, for the home page.</summary>
/// <remarks>
/// Counts what the rows say and nothing more (rule 3): a file the probe has not
/// read, or refused, has no quality and is <see cref="QualityTier.Unknown"/>; an
/// album with no year is undated rather than put in a decade. The tier is the decided
/// one (<see cref="AudioAnalysis.Decided"/>), so a CD rip in a 24-bit FLAC
/// counts as CD here as it does on the upgrade list.
/// </remarks>
public static class LibraryOverview
{
    public static FileTotals OfFiles(IEnumerable<OverviewFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var tiers = new Dictionary<QualityTier, int>();
        var codecs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var tier = AudioAnalysis.Decided(file.Quality, file.Measurement)?.Tier ?? QualityTier.Unknown;
            tiers[tier] = tiers.GetValueOrDefault(tier) + 1;

            if (file.Quality is { } quality)
            {
                codecs[quality.Codec] = codecs.GetValueOrDefault(quality.Codec) + 1;
            }
        }

        return new FileTotals(
            [.. tiers.OrderByDescending(entry => entry.Key).Select(entry => (entry.Key, entry.Value))],
            [.. Commonest(codecs)]);
    }

    public static AlbumTotals OfAlbums(IEnumerable<OverviewAlbum> albums, int genres)
    {
        ArgumentNullException.ThrowIfNull(albums);

        var decades = new Dictionary<int, int>();
        var undated = 0;
        var genreCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var album in albums)
        {
            if (album.Year is { } year)
            {
                var decade = year - (year % 10);
                decades[decade] = decades.GetValueOrDefault(decade) + 1;
            }
            else
            {
                undated++;
            }

            foreach (var genre in FileGenres.Split(album.Genres).Distinct(StringComparer.Ordinal))
            {
                genreCounts[genre] = genreCounts.GetValueOrDefault(genre) + 1;
            }
        }

        return new AlbumTotals(
            [.. decades.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value))],
            undated,
            [.. Commonest(genreCounts).Take(genres)]);
    }

    private static IEnumerable<(string Name, int Count)> Commonest(Dictionary<string, int> counts) =>
        counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));
}
