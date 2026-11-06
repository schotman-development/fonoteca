using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class BarcodesTests
{
    /// <summary>
    /// Qobuz's barcode for Shostakovich's <i>The Symphonies</i>, Universal's
    /// check digit dropped, and MusicBrainz's for it.
    /// </summary>
    [Theory]
    [InlineData("0002894444302", "28944443028")]
    [InlineData("028944443028", "28944443028")]
    [InlineData("no barcode", null)]
    public void AWholeBarcodeHasItsCheckDigit(string barcode, string? whole) =>
        Assert.Equal(whole, Barcodes.Whole(barcode));
}
