using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class PageIconsTests
{
    /// <summary>nporadio5.nl's head as it was on 2026-10-23, attributes in its order.</summary>
    [Fact]
    public void TheTouchIconIsLargeAndTheSharePictureLeadsTheSmallOnes()
    {
        const string head = """
            <meta content="https://www.nporadio5.nl/images/og_image.jpg" property="og:image" data-next-head=""/>
            <meta content="https://www.nporadio5.nl/images/og_image.jpg" property="og:image:secure_url" data-next-head=""/>
            <link href="/apple-touch-icon.png" rel="apple-touch-icon" sizes="180x180" data-next-head=""/>
            <link href="/favicon-32x32.png" rel="icon" sizes="32x32" type="image/png" data-next-head=""/>
            <link href="/favicon-16x16.png" rel="icon" sizes="16x16" type="image/png" data-next-head=""/>
            <link color="#101010" href="/safari-pinned-tab.svg" rel="mask-icon" data-next-head=""/>
            """;

        var icons = PageIcons.From(head, new Uri("https://www.nporadio5.nl/"));

        Assert.Equal(["https://www.nporadio5.nl/apple-touch-icon.png"], icons.Large);
        Assert.Equal(
            ["https://www.nporadio5.nl/images/og_image.jpg", "https://www.nporadio5.nl/favicon-32x32.png", "https://www.nporadio5.nl/favicon-16x16.png"],
            icons.Small);
    }

    /// <summary>grollooradio.nl's: WordPress's icons, the largest first, and a touch icon with no size counted as Apple draws it.</summary>
    [Fact]
    public void LargeIconsGoLargestFirst()
    {
        const string head = """
            <link rel="icon" href="https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-32x32.png" sizes="32x32" />
            <link rel="icon" href="https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-192x192.png" sizes="192x192" />
            <link rel="apple-touch-icon" href="https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-180x180.png" />
            """;

        var icons = PageIcons.From(head, new Uri("https://grollooradio.nl/"));

        Assert.Equal(
            [
                "https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-192x192.png",
                "https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-180x180.png",
            ],
            icons.Large);
        Assert.Equal(["https://grollooradio.nl/wp-content/uploads/2023/04/cropped-grollooapp-1-32x32.png"], icons.Small);
    }

    [Fact]
    public void AddressesAreReadFromThePageAndOnlyRasterWebPicturesKept()
    {
        const string head = """
            <LINK REL='shortcut icon' HREF='icons/fav.ico'>
            <link rel=icon href=//cdn.example.org/logo-256.png sizes=256x256>
            <link rel="icon" type="image/svg+xml" sizes="any" href="/logo">
            <link rel="apple-touch-icon" href="/touch.svg">
            <link rel="icon" sizes="512x512" href="data:image/png;base64,AAAA">
            <link rel="icon" sizes="512x512" href="javascript:alert(1)">
            <link rel="stylesheet" href="/site.css">
            <link rel="icon" sizes="300x300" href="/a.png?v=1&amp;size=300">
            """;

        var icons = PageIcons.From(head, new Uri("https://radio.example.org/station/"));

        Assert.Equal(["https://radio.example.org/a.png?v=1&size=300", "https://cdn.example.org/logo-256.png"], icons.Large);
        Assert.Equal(["https://radio.example.org/station/icons/fav.ico"], icons.Small);
    }
}
