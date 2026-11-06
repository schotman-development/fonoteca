using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Tests;

public sealed class ShopReleaseTypeTests
{
    [Theory]
    [InlineData("single", "Single", null)]
    [InlineData("epSingle", "Single", null)]
    [InlineData("epmini", "EP", null)]
    [InlineData("ep", "EP", null)]
    [InlineData("compilation", "Album", "Compilation")]
    [InlineData("live", "Album", "Live")]
    [InlineData("album", "Album", null)]
    [InlineData("download", "Album", null)]
    [InlineData(null, "Album", null)]
    public void TheShopsWordIsMusicBrainzsTypes(string? word, string primary, string? secondary) =>
        Assert.Equal((primary, secondary), ShopReleaseType.Of(word));
}
