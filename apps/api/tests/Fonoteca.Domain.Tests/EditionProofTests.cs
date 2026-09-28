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
