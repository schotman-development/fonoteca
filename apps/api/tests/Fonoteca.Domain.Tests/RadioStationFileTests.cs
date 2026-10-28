using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class RadioStationFileTests
{
    private static readonly RadioStation Radio5 = new(
        "NPO Radio 5",
        "https://icecast.omroep.nl/radio5-bb-mp3",
        "https://www.nporadio5.nl/",
        "https://www.nporadio5.nl/apple-touch-icon.png");

    [Fact]
    public void ANewFileIsXspfWithEachFieldWhereTheSpecPutsIt()
    {
        Assert.Equal(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <title>NPO Radio 5</title>
              <trackList>
                <track>
                  <location>https://icecast.omroep.nl/radio5-bb-mp3</location>
                  <title>NPO Radio 5</title>
                  <info>https://www.nporadio5.nl/</info>
                  <image>https://www.nporadio5.nl/apple-touch-icon.png</image>
                </track>
              </trackList>
            </playlist>

            """.ReplaceLineEndings("\n"),
            RadioStationFile.Write(null, Radio5).ReplaceLineEndings("\n"));

        Assert.Equal("Radio/NPO Radio 5.xspf", RadioStationFile.PathFor("NPO Radio 5"));
        Assert.Equal("Radio/ACDC Radio.xspf", RadioStationFile.PathFor("AC/DC Radio"));
    }

    [Fact]
    public void WhatIsWrittenReadsBackAndAFieldLeftOutIsGone()
    {
        Assert.Equal(Radio5, RadioStationFile.Read(RadioStationFile.Write(null, Radio5), "Radio/x.xspf"));

        var bare = Radio5 with { HomePageUrl = null, ImageUrl = null };
        var text = RadioStationFile.Write(RadioStationFile.Write(null, Radio5), bare);

        Assert.Equal(bare, RadioStationFile.Read(text, "Radio/x.xspf"));
        Assert.DoesNotContain("<info>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<image>", text, StringComparison.Ordinal);
    }

    /// <summary>radio-browser's own export, its identifier and comment kept, a field added in the spec's place.</summary>
    [Fact]
    public void AnotherProgramsFileIsReadAndOnlyTheStationsFieldsChange()
    {
        const string exported = """
            <?xml version="1.0" encoding="UTF-8"?>
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <!-- exported -->
              <trackList>
                <track>
                  <location>https://icecast.omroep.nl/radio5-sb-aac</location>
                  <identifier>radiobrowser:5cb448f8-1f80-4d78-bc23-ef2394378708</identifier>
                  <title>NPO Radio 5 </title>
                  <image>https://www.nporadio5.nl/apple-touch-icon.png</image>
                </track>
              </trackList>
            </playlist>
            """;

        Assert.Equal(
            new RadioStation("NPO Radio 5", "https://icecast.omroep.nl/radio5-sb-aac", null, "https://www.nporadio5.nl/apple-touch-icon.png"),
            RadioStationFile.Read(exported, "Radio/radio5.xspf"));

        var text = RadioStationFile.Write(exported, Radio5);

        Assert.Equal(Radio5, RadioStationFile.Read(text, "Radio/radio5.xspf"));
        Assert.Contains("<!-- exported -->", text, StringComparison.Ordinal);
        Assert.Contains("<identifier>radiobrowser:5cb448f8-1f80-4d78-bc23-ef2394378708</identifier>", text, StringComparison.Ordinal);
        Assert.Matches(@"(?s)<identifier>.*<title>NPO Radio 5</title>\s*<info>.*</info>\s*<image>", text);
    }

    [Fact]
    public void ANameIsThePlaylistsTitleElseTheTracksElseTheFiles()
    {
        const string untitled = """
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <trackList><track><location>http://stream.gal.io/arrow</location></track></trackList>
            </playlist>
            """;

        Assert.Equal("Arrow Classic Rock", RadioStationFile.Read(untitled, "Radio/Arrow Classic Rock.xspf")?.Name);
        Assert.Equal("Arrow", RadioStationFile.Read(untitled.Replace("<track>", "<track><title>Arrow</title>", StringComparison.Ordinal), "Radio/a.xspf")?.Name);
    }

    /// <summary>A client is handed only web addresses, and a file with no web stream is no station.</summary>
    [Fact]
    public void OnlyWebAddressesAreAStationsAndAFileWithoutOneIsNone()
    {
        const string local = """
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <trackList><track>
                <location>file:///music/live.mp3</location>
                <info>javascript:alert(1)</info>
                <image>data:image/png;base64,AAAA</image>
              </track></trackList>
            </playlist>
            """;

        Assert.Null(RadioStationFile.Read(local, "Radio/a.xspf"));

        var station = RadioStationFile.Read(
            local.Replace("<location>", "<location>https://example.org/live</location><location>", StringComparison.Ordinal),
            "Radio/a.xspf");

        Assert.Equal("https://example.org/live", station?.StreamUrl);
        Assert.Null(station!.HomePageUrl);
        Assert.Null(station.ImageUrl);

        Assert.Null(RadioStationFile.Read("#EXTM3U\nhttps://example.org/live\n", "Radio/a.xspf"));
        Assert.Null(RadioStationFile.Read("<html><body/></html>", "Radio/a.xspf"));
        Assert.False(RadioStationFile.WebAddress("https://example.org/\u0001"));
    }

    [Fact]
    public void ADocumentTypeIsRefusedRatherThanExpanded()
    {
        const string laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE playlist [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <title>&b;</title>
              <trackList><track><location>https://example.org/live</location></track></trackList>
            </playlist>
            """;

        Assert.Null(RadioStationFile.Read(laughs, "Radio/a.xspf"));
    }
}
