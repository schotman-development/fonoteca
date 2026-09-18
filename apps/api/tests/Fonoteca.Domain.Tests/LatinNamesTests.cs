using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Which name a screen prints when the artist's own is not in Latin script.
/// </summary>
/// <remarks>
/// Every name in this file came out of the target library, and the alias lists
/// are the ones musicbrainz.org actually served for them. That matters most for
/// the cases that look like noise: <c>Johnny “Guitar” Watson</c> and
/// <c>T‐Bone Walker</c> are real rows, and a first attempt at this rule would
/// have rewritten both. The 22 artists it is meant to catch are outnumbered
/// nearly three to one by names that merely look foreign to a careless test.
/// </remarks>
public sealed class LatinNamesTests
{
    /// <summary>
    /// Typography is not a script, and 59 real names turn on this.
    /// </summary>
    /// <remarks>
    /// The measurement that shaped the rule. Asking "is every character in the
    /// ASCII-plus-accents range" flags 81 of this library's 3,051 artists; only
    /// 22 are actually non-Latin. The other 59 are curly quotes, U+2010 hyphens
    /// and U+2019 apostrophes inside perfectly ordinary Latin names — and
    /// "fixing" them would replace a correct name with a transliteration of
    /// itself.
    /// </remarks>
    [Theory]
    [InlineData("Johnny “Guitar” Watson")]
    [InlineData("T‐Bone Walker")]
    [InlineData("Camille Saint‐Saëns")]
    [InlineData("Gordon Jenkins’ Orchestra and Choir")]
    [InlineData("Claude‐Michel Schönberg")]
    [InlineData("Keb’ Mo’")]
    [InlineData("Andrés Orozco‐Estrada")]
    [InlineData("Dietrich Fischer‐Dieskau")]
    [InlineData("hr‐Sinfonieorchester")]
    public void TypographyDoesNotMakeALatinNameForeign(string name)
    {
        Assert.True(LatinNames.IsLatin(name));
        Assert.Null(LatinNames.Of(name, [English("Something Else")]));
    }

    /// <summary>
    /// Transliterations keep their diacritics and stay Latin.
    /// </summary>
    /// <remarks>
    /// The reason the range reaches past Latin Extended-B into Latin Extended
    /// Additional. <c>Ryūichi</c>, <c>Lǐ Bái</c> and <c>Gétatchèw</c> are the
    /// answers this rule produces, so a test for "is it Latin" that rejected
    /// them would make the rule reject its own output and ask again forever.
    /// </remarks>
    [Theory]
    [InlineData("Ryūichi Sakamoto")]
    [InlineData("Lǐ Bái")]
    [InlineData("Gétatchèw Mèkurya")]
    [InlineData("Dmitrij Dmitrievič Šostakovič")]
    [InlineData("Pēteris Čaikovskis")]
    public void AnAccentedTransliterationIsLatin(string name) =>
        Assert.True(LatinNames.IsLatin(name));

    /// <summary>
    /// A modifier letter is still typography, and this one had the bound wrong.
    /// </summary>
    /// <remarks>
    /// The <c>ʻokina</c> (U+02BB) and the modifier apostrophe (U+02BC) are
    /// category <c>Lm</c>, so <c>Rune.IsLetter</c> calls them letters — and they
    /// sit just above the old U+024F bound. <c>Israel Kamakawiwoʻole</c>
    /// therefore read as non-Latin and had an English alias written over a name
    /// that was already correct: the rule failing in the one direction that
    /// does damage, and the case the range test above would never have found,
    /// because no artist in this library is spelled with one. MusicBrainz
    /// spells most Hawaiian artists this way.
    /// </remarks>
    [Theory]
    [InlineData("Israel Kamakawiwoʻole")]
    [InlineData("Hawaiʻi")]
    [InlineData("Kealiʼi Reichel")]
    public void AModifierLetterApostropheDoesNotMakeANameForeign(string name)
    {
        Assert.True(LatinNames.IsLatin(name));
        Assert.Null(LatinNames.Of(name, [English("Something Else")]));
    }

    [Theory]
    [InlineData("Пётр Ильич Чайковский")]
    [InlineData("内田光子")]
    [InlineData("ጌታቸው፡መኩሪያ")]
    [InlineData("余隆")]
    public void ANonLatinNameIsRecognised(string name) => Assert.False(LatinNames.IsLatin(name));

    /// <summary>
    /// The English primary alias wins, which is the answer in 20 of 22 cases.
    /// </summary>
    /// <remarks>
    /// Tchaikovsky's real document, cut to the rivals that matter. It has
    /// <b>three</b> other English aliases and a Russian primary one, and each is
    /// a way to get this wrong: reading <c>primary</c> alone answers
    /// <c>Пётр Чайковский</c>, reading <c>locale="en"</c> alone answers whichever
    /// of the four sorts first, and taking the document's own order answers
    /// <c>Peter I. Tschaikowsky</c>.
    /// </remarks>
    [Fact]
    public void TheEnglishPrimaryAliasIsPreferredOverEveryRival()
    {
        var aliases = new[]
        {
            new MusicBrainzAlias("Peter I. Tschaikowsky", "en", Primary: false),
            new MusicBrainzAlias("Peter Ilyich Tchaikovsky", "en", Primary: false),
            new MusicBrainzAlias("Пётр Чайковский", "ru", Primary: true),
            new MusicBrainzAlias("Pyotr Ilyich Tchaikovsky", "en", Primary: true),
            new MusicBrainzAlias("Tchaikovsky", "en", Primary: false),
        };

        Assert.Equal(
            "Pyotr Ilyich Tchaikovsky",
            LatinNames.Of("Пётр Ильич Чайковский", aliases));
    }

    /// <summary>
    /// A primary alias in the artist's own script cannot win.
    /// </summary>
    /// <remarks>
    /// Isolated from the case above because it is the one that fails silently:
    /// every Russian artist here has a <c>primary="true"</c> Russian alias, so a
    /// rule that ranks on the flag before the script produces a Cyrillic answer
    /// for the exact rows this feature exists to fix, and the column is filled
    /// rather than left null — which reads downstream as "we looked, and this is
    /// the Latin name".
    /// </remarks>
    [Fact]
    public void ANonLatinAliasNeverWinsHoweverWellLabelled()
    {
        var aliases = new[]
        {
            new MusicBrainzAlias("Дмитрий Шостакович", "ru", Primary: true),
            new MusicBrainzAlias("Dmitri Shostakovich", "en", Primary: true),
        };

        Assert.Equal(
            "Dmitri Shostakovich",
            LatinNames.Of("Дмитрий Дмитриевич Шостакович", aliases));
    }

    /// <summary>
    /// <c>en_GB</c> is English, and demoting it costs the right answer.
    /// </summary>
    [Fact]
    public void AnEnglishLocaleOfAnyFlavourCounts()
    {
        var aliases = new[]
        {
            new MusicBrainzAlias("Aleksandr Glazoenov", "nl", Primary: true),
            new MusicBrainzAlias("Alexander Glazunov", "en_GB", Primary: true),
        };

        Assert.Equal(
            "Alexander Glazunov",
            LatinNames.Of("Александр Константинович Глазунов", aliases));
    }

    /// <summary>
    /// The two artists in this library with no English alias at all.
    /// </summary>
    /// <remarks>
    /// Both carry one Latin alias with no locale and no primary flag, which is
    /// the whole reason the last rung exists. Without it these two keep their
    /// own names while the other twenty are transliterated — the inconsistency
    /// nobody would think to look for.
    /// </remarks>
    [Theory]
    [InlineData("余隆", "Long Yu")]
    [InlineData("村治佳織", "Kaori Muraji")]
    public void ALocalelessLatinAliasIsBetterThanNoAnswer(string name, string expected) =>
        Assert.Equal(expected, LatinNames.Of(name, [new MusicBrainzAlias(expected, null, false)]));

    /// <summary>
    /// A search hint is a way to find somebody, not a name for them.
    /// </summary>
    /// <remarks>
    /// MusicBrainz files misspellings, punctuation variants and abbreviations
    /// under this alias type so its own search box matches them. Harmless while
    /// a named rung wins; on the last rung, which takes any Latin alias in
    /// ordinal order, a deliberate misspelling would become the name on the
    /// page — and "Kaori Murajii" sorts before the real spelling.
    /// </remarks>
    [Fact]
    public void ASearchHintIsNeverTheName()
    {
        var aliases = new[]
        {
            new MusicBrainzAlias("Kaori Murajii", null, Primary: false, Type: "Search hint"),
            new MusicBrainzAlias("Kaori Muraji", null, Primary: false, Type: null),
        };

        Assert.Equal("Kaori Muraji", LatinNames.Of("村治佳織", aliases));
    }

    /// <summary>
    /// No Latin alias means no invention.
    /// </summary>
    /// <remarks>
    /// The rung that was written and deleted. Un-inverting the sort name is
    /// right on a person and wrong on an ensemble — "Jenkins, Gordon, Orchestra
    /// and Choir" becomes "Orchestra and Choir Jenkins, Gordon" — and nothing in
    /// the sort name says which it is. Null leaves the reader printing the
    /// artist's own name, which is at worst hard to read and never false.
    /// </remarks>
    [Fact]
    public void AnArtistWithNoLatinAliasKeepsTheirOwnName() =>
        Assert.Null(LatinNames.Of("余隆", [new MusicBrainzAlias("余隆", "zh_Hant", false)]));

    [Fact]
    public void NoAliasesAtAllIsNotACrash()
    {
        Assert.Null(LatinNames.Of("内田光子", []));
        Assert.Null(LatinNames.Of("内田光子", null));
    }

    /// <summary>
    /// A Latin-named artist is left alone even when an English alias exists.
    /// </summary>
    /// <remarks>
    /// The blast radius, asserted. Plenty of Latin-named artists carry English
    /// aliases — stage names, anglicisations, abbreviations — and substituting
    /// those would rename bands across the whole catalogue on a rule that was
    /// asked to fix 22 rows.
    /// </remarks>
    [Fact]
    public void ALatinNamedArtistIsNeverRenamed() =>
        Assert.Null(LatinNames.Of("Die Ärzte", [English("The Doctors")]));

    /// <summary>
    /// A billing line in the artist's own script is not a billing line.
    /// </summary>
    /// <remarks>
    /// The six rows this library holds, all of them Mitsuko Uchida. Stored,
    /// they outrank the Latin name everywhere a credit line is built, because
    /// every reader resolves <c>CreditedAs ?? LatinName ?? Name</c> — so the
    /// artist page would say Mitsuko Uchida and the release page beside it
    /// would say 内田光子.
    /// </remarks>
    [Fact]
    public void ANonLatinCreditLineIsNotWorthStoring() =>
        Assert.Null(LatinNames.CreditedAs("内田光子", "内田光子"));

    [Fact]
    public void ARealBillingDifferenceSurvives() =>
        Assert.Equal("Bowie", LatinNames.CreditedAs("Bowie", "David Bowie"));

    [Fact]
    public void ACreditMatchingTheArtistIsStillDropped() =>
        Assert.Null(LatinNames.CreditedAs("David Bowie", "David Bowie"));

    /// <summary>
    /// The writer that has no artist row to compare against still drops script.
    /// </summary>
    [Fact]
    public void ACreditWithNoArtistNameIsJudgedOnScriptAlone()
    {
        Assert.Null(LatinNames.CreditedAs("内田光子", artistName: null));
        Assert.Equal("Bowie", LatinNames.CreditedAs("Bowie", artistName: null));
    }

    private static MusicBrainzAlias English(string name) => new(name, "en", Primary: true);
}
