using Fonoteca.Domain.Identification;
using static Fonoteca.Domain.Tests.AlbumFolderFixtures;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// When the audio says a folder cannot be the compact disc it otherwise fits.
/// </summary>
/// <remarks>
/// Measured on the target library: 60 releases MusicBrainz lists as CD held files
/// no CD could have produced — hi-res downloads, and 16/44.1 downloads whose
/// track lengths are not whole CD sectors. Their lengths matched; the medium is
/// the one percent that did not.
/// </remarks>
public sealed class EditionProofTests
{
    // 588 samples is one CD sector at 44.1 kHz; 180 seconds is 13,500 sectors.
    private const double Aligned = 7_938_000 / 44_100.0;
    private const double Unaligned = 7_938_001 / 44_100.0;

    [Theory]
    [InlineData(24, 96_000, Aligned, true)]
    [InlineData(24, 44_100, Aligned, true)]
    [InlineData(16, 48_000, Aligned, true)]
    [InlineData(16, 44_100, Unaligned, true)]
    [InlineData(16, 44_100, Aligned, false)]
    public void LosslessAudioACompactDiscCouldNotHoldContradictsOne(int depth, int rate, double seconds, bool contradicts) =>
        Assert.Equal(contradicts, EditionProof.ContradictsCompactDisc(Lossless(depth, rate, seconds)));

    [Fact]
    public void LossyOrUnmeasuredAudioProvesNothingEitherWay()
    {
        Assert.False(EditionProof.ContradictsCompactDisc(Lossy()));
        Assert.False(EditionProof.ContradictsCompactDisc(null));
    }

    [Theory]
    [InlineData("CD", true)]
    [InlineData("HDCD", true)]
    [InlineData("Copy Control CD", true)]
    [InlineData(null, true)]
    [InlineData("Digital Media", false)]
    [InlineData("12\" Vinyl", false)]
    [InlineData("Hybrid SACD", false)]
    [InlineData("Hybrid SACD (SACD layer)", false)]
    [InlineData("Hybrid SACD (CD layer)", true)]
    public void OnlyTheCompactDiscFamilyIsHeldToIt(string? format, bool compactDisc) =>
        Assert.Equal(compactDisc, EditionProof.MayBeCompactDisc(format));

    /// <summary>
    /// Placeholder tracks are not songs a folder could hold, so a rip of every
    /// song proves the pressing that pads its hidden track with silence.
    /// </summary>
    /// <remarks>
    /// Marc Broussard's <i>Carencro</i>: eleven <c>[silence]</c> tracks before
    /// track 23. Counted, twelve files could never equal twenty-three tracks.
    /// </remarks>
    [Fact]
    public void AHiddenTrackBehindSilentPlaceholdersProvesItsPressing()
    {
        var album = Release("carencro", Tracks(
            ("a", 180_437),
            ("b", 200_437),
            ("[silence]", 4_000),
            ("[silence]", 4_000),
            ("[data track]", 4_000),
            ("hidden", 240_437)));

        var files = new[] { File("a", 180_437, [album]), File("b", 200_437, [album]), File("hidden", 240_437, [album]) };

        var seats = EditionProof.Seat(album, files);

        Assert.NotNull(seats);
        Assert.Equal([1, 2, 6], seats.Select(seat => seat.Slot.Position));

        var fit = ReleaseFit.For(album, [.. files.Select(file => new AttributionFile(file.Id, file.Recording!.Value, file.Duration))]);
        Assert.NotNull(fit);
        Assert.Equal(1.0, fit.Coverage);
    }

    /// <summary>
    /// A rip that kept its silent tracks as files proves the pressing just as
    /// one that dropped them does: a placeholder counts where a file holds it.
    /// </summary>
    [Fact]
    public void ARipThatKeptItsSilentTracksProvesItsPressingToo()
    {
        var album = Release("carencro", Tracks(
            ("a", 180_437),
            ("[silence]", 4_000),
            ("hidden", 240_437)));

        var files = new[]
        {
            File("a", 180_437, [album]),
            File("[silence]", 4_000, [album]),
            File("hidden", 240_437, [album]),
        };

        var seats = EditionProof.Seat(album, files);

        Assert.NotNull(seats);
        Assert.Equal([1, 2, 3], seats.Select(seat => seat.Slot.Position));

        var fit = ReleaseFit.For(album, [.. files.Select(file => new AttributionFile(file.Id, file.Recording!.Value, file.Duration))]);
        Assert.NotNull(fit);
        Assert.Equal(1.0, fit.Coverage);
    }

    /// <summary>A song that is really called "Silence" is a song.</summary>
    [Fact]
    public void OnlyMusicBrainzsExactPlaceholderTitlesAreLeftOut()
    {
        Assert.True(Domain.Catalogue.TrackTitles.IsPlaceholder("[silence]"));
        Assert.True(Domain.Catalogue.TrackTitles.IsPlaceholder("[data track]"));
        Assert.False(Domain.Catalogue.TrackTitles.IsPlaceholder("Silence"));
        Assert.False(Domain.Catalogue.TrackTitles.IsPlaceholder("[untitled]"));
        Assert.False(Domain.Catalogue.TrackTitles.IsPlaceholder(null));
    }

    [Fact]
    public void AHiResFileMatchingACompactDiscToTheMillisecondDoesNotProveIt()
    {
        var cd = Release("cd", Tracks(("a", 180_000 + 437)), format: "CD");
        var digital = Release("digital", Tracks(("a", 180_000 + 437)), format: "Digital Media");
        var file = File("a", 180_437, [cd, digital], quality: Lossless(24, 96_000, 180.437));

        Assert.Null(EditionProof.Seat(cd, [file]));
        Assert.NotNull(EditionProof.Seat(digital, [file]));
    }
}
