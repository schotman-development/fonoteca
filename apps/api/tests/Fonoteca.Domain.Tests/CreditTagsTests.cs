using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class CreditTagsTests
{
    [Fact]
    public void EachLinkLandsInTheTagPicardGivesIt()
    {
        var tags = CreditTags.From(
        [
            ("composer", null, "Ray Evans"),
            ("librettist", null, "Jay Livingston"),
            ("conductor", null, "Nelson Riddle"),
            ("orchestrator", null, "Nelson Riddle"),
            ("producer", "co", "Lee Gillette"),
            ("recording", null, "John Palladino"),
            ("mix", null, "Mark Wilder"),
            ("instrument", "guitar, bass guitar", "Chris Stapleton"),
            ("vocal", "lead vocals", "Nat King Cole"),
            ("vocal", null, "A Backing Singer"),
            ("performing orchestra", null, "Capitol Studio Orchestra"),
            ("phonographic copyright", null, "Capitol Records"),
            ("writer", null, "Somebody Unsplit"),
        ]);

        Assert.Equal(["Ray Evans"], tags.Composers);
        Assert.Equal(["Jay Livingston"], tags.Lyricists);
        Assert.Equal(["Nelson Riddle"], tags.Conductors);
        Assert.Equal(["Nelson Riddle"], tags.Arrangers);
        Assert.Equal(["Lee Gillette"], tags.Producers);
        Assert.Equal(["John Palladino"], tags.Engineers);
        Assert.Equal(["Mark Wilder"], tags.Mixers);
        Assert.Equal(
            [
                "Chris Stapleton (guitar, bass guitar)",
                "Nat King Cole (lead vocals)",
                "A Backing Singer (vocals)",
                "Capitol Studio Orchestra (orchestra)",
            ],
            tags.Performers);
    }

    [Fact]
    public void OneNameTwiceInATagIsWrittenOnce()
    {
        var tags = CreditTags.From([("producer", null, "Teo Macero"), ("producer", "co", "Teo Macero")]);

        Assert.Equal(["Teo Macero"], tags.Producers);
    }

    /// <summary>Several values travel as one field's several values, and none is a fact nobody holds.</summary>
    [Fact]
    public void SeveralValuesAreJoinedWithTheSeparatorAndNoneProducesNoEntry()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            Genres = ["jazz", "modal jazz"],
            Credits = new CreditTags { Producers = ["Irving Townsend", "Teo Macero"] },
        });

        Assert.Equal($"jazz{CatalogueTags.ValueSeparator}modal jazz", tags[CatalogueTags.Genre]);
        Assert.Equal($"Irving Townsend{CatalogueTags.ValueSeparator}Teo Macero", tags[CatalogueTags.Producer]);
        Assert.False(tags.ContainsKey(CatalogueTags.Composer));
        Assert.False(tags.ContainsKey(CatalogueTags.Isrc));
    }

    /// <summary>A value repeated, blank, or carrying the separator inside it cannot split or double a field.</summary>
    [Fact]
    public void ValuesAreWrittenOnceBlanksDroppedAndNeverSplitFromWithin()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            Genres = ["jazz", "jazz", " ", $"cool{CatalogueTags.ValueSeparator}jazz"],
        });

        Assert.Equal($"jazz{CatalogueTags.ValueSeparator}cool jazz", tags[CatalogueTags.Genre]);
    }
}
