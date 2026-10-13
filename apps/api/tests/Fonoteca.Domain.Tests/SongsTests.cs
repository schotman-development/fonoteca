using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class SongsTests
{
    [Fact]
    public void TheMovementsOfOneWorkAreOneSongAndEveryOtherRecordingIsItsOwn()
    {
        var symphony = WorkId.New();
        var concerto = WorkId.New();
        var movements = Enumerable.Range(0, 4).Select(_ => RecordingId.New()).ToList();
        var otherPerformance = RecordingId.New();
        var solo = RecordingId.New();
        var song = RecordingId.New();

        var pieceOf = movements.ToDictionary(movement => movement, _ => symphony);
        pieceOf[otherPerformance] = symphony;
        pieceOf[solo] = concerto;

        // The symphony (five recordings), the concerto, and the song held twice.
        Assert.Equal(3, Songs.Count([.. movements, otherPerformance, solo, song, song], pieceOf));
    }
}
