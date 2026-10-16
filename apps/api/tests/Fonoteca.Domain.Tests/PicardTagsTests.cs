using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>The rest of Picard's tags, as the rule offers them.</summary>
public sealed class PicardTagsTests
{
    /// <summary>A file held to its album alone has no pressing, so no pressing's facts are offered.</summary>
    [Fact]
    public void NoPressingMeansNoPressingTags()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource { AlbumTitle = "Kind of Blue", ReleaseTypes = ["Album"], OriginalYear = 1959 });

        foreach (var pressing in new[]
                 {
                     CatalogueTags.Label, CatalogueTags.CatalogNumber, CatalogueTags.Barcode, CatalogueTags.ReleaseCountry,
                     CatalogueTags.ReleaseStatus, CatalogueTags.Media, CatalogueTags.ReleaseTrackId, CatalogueTags.Asin,
                     CatalogueTags.Script, CatalogueTags.DiscSubtitle,
                 })
        {
            Assert.False(tags.ContainsKey(pressing), pressing);
        }

        Assert.Equal("album", tags[CatalogueTags.ReleaseType]);
        Assert.Equal("1959", tags[CatalogueTags.OriginalYear]);
    }

    /// <summary>Picard lowercases a release's status and its types, and writes each type as a value of its own.</summary>
    [Fact]
    public void StatusAndTypesAreLowercasedAsPicardWritesThem()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource { ReleaseStatus = "Official", ReleaseTypes = ["Album", "Live"] });

        Assert.Equal("official", tags[CatalogueTags.ReleaseStatus]);
        Assert.Equal($"album{CatalogueTags.ValueSeparator}live", tags[CatalogueTags.ReleaseType]);
    }

    /// <summary>COMPILATION is "1" for an album billed to Various Artists and absent otherwise, never "0".</summary>
    [Fact]
    public void OnlyAVariousArtistsAlbumIsACompilation()
    {
        Assert.Equal("1", CatalogueTags.For(new CatalogueTagSource { Compilation = true })[CatalogueTags.Compilation]);
        Assert.False(CatalogueTags.For(new CatalogueTagSource()).ContainsKey(CatalogueTags.Compilation));
    }

    /// <summary>The movement's three and SHOWMOVEMENT go together, and only where its number is known.</summary>
    [Fact]
    public void AMovementWithoutANumberIsNoMovement()
    {
        var numbered = CatalogueTags.For(new CatalogueTagSource { Work = "Symphony no. 5", MovementName = "Allegro con brio", Movement = 1, MovementTotal = 4 });
        var unnumbered = CatalogueTags.For(new CatalogueTagSource { Work = "Symphony no. 5", MovementName = "Allegro con brio" });

        Assert.Equal("Allegro con brio", numbered[CatalogueTags.MovementName]);
        Assert.Equal("1", numbered[CatalogueTags.Movement]);
        Assert.Equal("4", numbered[CatalogueTags.MovementTotal]);
        Assert.Equal("1", numbered[CatalogueTags.ShowMovement]);

        Assert.Equal("Symphony no. 5", unnumbered[CatalogueTags.Work]);
        Assert.False(unnumbered.ContainsKey(CatalogueTags.MovementName));
        Assert.False(unnumbered.ContainsKey(CatalogueTags.ShowMovement));
    }

    /// <summary>The reference loudness is stated beside a gain and nowhere else; the range as loudgain writes it.</summary>
    [Fact]
    public void TheReferenceLoudnessGoesWithTheGain()
    {
        var gained = CatalogueTags.For(new CatalogueTagSource { TrackGain = -6.23, TrackRange = 7.564 });
        var ungained = CatalogueTags.For(new CatalogueTagSource { TrackRange = 7.564 });

        Assert.Equal("-18.00 LUFS", gained[CatalogueTags.ReferenceLoudness]);
        Assert.Equal("7.56 dB", gained[CatalogueTags.TrackRange]);
        Assert.False(ungained.ContainsKey(CatalogueTags.ReferenceLoudness));
    }

    /// <summary>A tempo Essentia was unsure of is kept in the catalogue and kept out of the file; the key goes in.</summary>
    [Fact]
    public void AnUnsureTempoIsNoBpmTagButItsKeyIs()
    {
        var unsure = CatalogueTags.For(new CatalogueTagSource
        {
            TempoAndKey = new TempoAndKey { Bpm = 61.4, BpmConfidence = 0.4, Key = "Eb", Scale = "major", KeyStrength = 0.7 },
        });

        Assert.False(unsure.ContainsKey(CatalogueTags.Bpm));
        Assert.Equal("Eb", unsure[CatalogueTags.Key]);
    }

    [Theory]
    [InlineData(136.5, 1.0, 137)]
    [InlineData(120.2, 3.6, 120)]
    [InlineData(120.2, 0.99, null)]
    [InlineData(0, 4.0, null)]
    public void ATempoIsWholeAndOnlyWhereEssentiaWasSure(double bpm, double confidence, int? expected) =>
        Assert.Equal(expected, TempoAndKey.TagBpm(new TempoAndKey { Bpm = bpm, BpmConfidence = confidence }));

    [Theory]
    [InlineData("F#", "minor", 0.6, "F#m")]
    [InlineData("Bb", "minor", 0.6, "Bbm")]
    [InlineData("C", "major", 0.6, "C")]
    [InlineData("A", "minor", 0.0, null)]
    [InlineData("H", "major", 0.6, null)]
    [InlineData("C", "dorian", 0.6, null)]
    public void AKeyIsSpelledAsTkeyWants(string key, string scale, double strength, string? expected) =>
        Assert.Equal(expected, TempoAndKey.TagKey(new TempoAndKey { Key = key, Scale = scale, KeyStrength = strength }));

    [Theory]
    [InlineData("Symphony no. 5 in C minor, op. 67: I. Allegro con brio", "Symphony no. 5 in C minor, op. 67", "Allegro con brio")]
    [InlineData("Symphony no. 5 in C minor, op. 67: IV. Allegro", "Symphony no. 5 in C minor, op. 67", "Allegro")]
    [InlineData("Die Zauberflöte, K. 620: Akt I. Nr. 1 Introduktion", "Die Zauberflöte, K. 620", "Akt I. Nr. 1 Introduktion")]
    [InlineData("Allegro con brio", "Symphony no. 5", "Allegro con brio")]
    [InlineData("Symphony no. 5: I.", "Symphony no. 5", "I.")]
    [InlineData("Symphony no. 5:", "Symphony no. 5", "Symphony no. 5:")]
    public void AMovementIsNamedWithoutItsWholeOrItsNumeral(string title, string parent, string expected) =>
        Assert.Equal(expected, WorkTags.MovementName(title, parent));

    /// <summary>A songwriter, a remixer and a DJ-mixer each have a tag of their own now.</summary>
    [Fact]
    public void WritersRemixersAndDjMixersAreCredited()
    {
        var credits = CreditTags.From([("writer", null, "Bill Withers"), ("remixer", null, "Mark Ronson"), ("mix-DJ", null, "Gilles Peterson")]);

        Assert.Equal(["Bill Withers"], credits.Writers);
        Assert.Equal(["Mark Ronson"], credits.Remixers);
        Assert.Equal(["Gilles Peterson"], credits.DjMixers);
    }
}
