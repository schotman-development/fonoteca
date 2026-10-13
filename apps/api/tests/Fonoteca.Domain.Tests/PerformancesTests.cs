using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class PerformancesTests
{
    [Fact]
    public void AMovementsAlbumIsOnePerformanceAndAnythingElseIsOnePerAlbumAndPerformers()
    {
        (string Album, string? Performers, bool Movement)[] recordings =
        [
            ("Les Misérables in Concert", "Colm Wilkinson", true),
            ("Les Misérables in Concert", "Philip Quast", true),
            ("Symphonies 1-15", "Radio Filharmonisch Orkest", false),
            ("Symphonies 1-15", "Radio Filharmonisch Orkest", false),
            ("Tribute", "George Benson", false),
            ("Tribute", "Gregory Porter", false),
            ("Live", "Gregory Porter", false),
            ("Les Misérables 25th Anniversary", "Alfie Boe", true),
        ];

        var performances = Performances.Of(recordings, row => row.Album, row => row.Performers, row => row.Movement);

        // The musical whoever sings, once per album; the symphony over two
        // tracks; two singers on one album; one singer on two.
        Assert.Equal([2, 2, 1, 1, 1, 1], performances.Select(performance => performance.Count));
    }
}
