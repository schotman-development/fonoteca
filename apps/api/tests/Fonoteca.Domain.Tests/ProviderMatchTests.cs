using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using static Fonoteca.Domain.Tests.AlbumFolderFixtures;

namespace Fonoteca.Domain.Tests;

/// <summary>Which MusicBrainz release a shop's album is, from its barcode and its track list.</summary>
public sealed class ProviderMatchTests
{
    private static readonly (int, int)[] TwoTracks = [(1, 1), (1, 2)];

    [Fact]
    public void TheReleaseWithTheBarcodeAndTheSameTrackListIsTheAlbum()
    {
        var release = Barcoded("rumours", "603497941032", "The Chain", "Dreams");

        // Qobuz's leading zero and MusicBrainz's lack of one are the same barcode.
        Assert.Same(release, ProviderMatch.Pick("0603497941032", TwoTracks, [release]));
    }

    [Fact]
    public void ADifferentTrackListIsNotTheAlbumWhateverItsBarcode()
    {
        var deluxe = Barcoded("deluxe", "0603497941032", "The Chain", "Dreams", "Silver Springs");

        Assert.Null(ProviderMatch.Pick("0603497941032", TwoTracks, [deluxe]));
    }

    [Fact]
    public void TwoReleasesFittingAlikeAreNoAnswer()
    {
        var one = Barcoded("cd", "0603497941032", "The Chain", "Dreams");
        var other = Barcoded("digital", "0603497941032", "The Chain", "Dreams");

        Assert.Null(ProviderMatch.Pick("0603497941032", TwoTracks, [one, other]));
    }

    [Fact]
    public void AnotherBarcodeOrNoneIsNoAnswer()
    {
        var release = Barcoded("rumours", "0093624990811", "The Chain", "Dreams");

        Assert.Null(ProviderMatch.Pick("0603497941032", TwoTracks, [release]));
        Assert.Null(ProviderMatch.Pick(null, TwoTracks, [release]));
    }

    private static MusicBrainzRelease Barcoded(string id, string barcode, params string[] titles) =>
        Release(id, [.. titles.Select(title => (title, (int?)200_437))]) with { Barcode = barcode };
}
