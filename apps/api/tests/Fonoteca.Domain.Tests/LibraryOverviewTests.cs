using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class LibraryOverviewTests
{
    private static AudioQuality Flac(int depth) => new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = depth,
        BitrateBps = 900_000,
        IsLossless = true,
    };

    [Fact]
    public void FilesAreCountedByTheirDecidedTier()
    {
        var totals = LibraryOverview.OfFiles([
            new OverviewFile(Flac(24), null),

            // 24 bits declared, 16 used: a CD rip, as the upgrade list counts it.
            new OverviewFile(Flac(24), new AudioMeasurement { EffectiveBitDepth = 16 }),

            // Never probed, or refused: no tier, no codec.
            new OverviewFile(null, null),
            new OverviewFile(null, null),
        ]);

        Assert.Equal(
            [(QualityTier.LosslessHiRes, 1), (QualityTier.LosslessCd, 1), (QualityTier.Unknown, 2)],
            totals.Tiers);
        Assert.Equal([("flac", 2)], totals.Codecs);
    }

    [Fact]
    public void AlbumsGoInTheDecadeTheyFirstCameOutAndEachGenreCountsAnAlbumOnce()
    {
        var totals = LibraryOverview.OfAlbums(
            [
                new OverviewAlbum(1979, ["Rock", "Blues", "Rock"]),
                new OverviewAlbum(1970, ["Rock"]),
                new OverviewAlbum(1980, ["Jazz"]),
                new OverviewAlbum(null, ["Blues"]),
                new OverviewAlbum(2001, []),
            ],
            genres: 2);

        Assert.Equal([(1970, 2), (1980, 1), (2000, 1)], totals.Decades);
        Assert.Equal(1, totals.Undated);

        // Commonest first, a tie by name, cut to the two asked for.
        Assert.Equal([("Blues", 2), ("Rock", 2)], totals.Genres);
    }
}
