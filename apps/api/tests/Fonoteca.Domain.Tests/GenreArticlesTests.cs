using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>Which article describes each genre, and which genres are still to be asked about.</summary>
public sealed class GenreArticlesTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A genre added to the tree is given an article or named here as having none.</summary>
    [Fact]
    public void EveryGenreHasAnArticleOrIsKnownToHaveNone()
    {
        Assert.Empty(GenreArticles.Titles.Keys.Except(Genres.All));
        Assert.Equal(
            ["Contemporary Country", "Early R&B", Genres.Other, "Stage & Screen"],
            Genres.All.Except(GenreArticles.Titles.Keys).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NothingAskedIsEveryGenreHeld() =>
        Assert.Equal(
            ["Baroque", "Classical", "Jazz"],
            GenreArticles.Unasked(["Jazz", "Classical", "Baroque", "Stage & Screen"], [], Cutoff).Select(genre => genre.Key));

    [Fact]
    public void AnArticleFoundIsNeverAskedForAgain() =>
        Assert.DoesNotContain("Baroque", Names(Asked("Baroque", "Baroque music", found: true, Cutoff.AddYears(-1))));

    [Fact]
    public void NoArticleIsAskedForAgainAfterAWeek()
    {
        Assert.DoesNotContain("Baroque", Names(Asked("Baroque", "Baroque music", found: false, Cutoff.AddDays(1))));
        Assert.Contains("Baroque", Names(Asked("Baroque", "Baroque music", found: false, Cutoff.AddDays(-1))));
    }

    /// <summary>Naming another article asks again, found or not, with no hand-written update.</summary>
    [Fact]
    public void AnotherArticleNamedIsAskedFor()
    {
        var asked = Asked("Baroque", "Baroque", found: true, Cutoff.AddDays(1));

        Assert.Contains("Baroque", Names(asked));
        Assert.False(GenreArticles.IsCurrent(asked[0]));
        Assert.True(GenreArticles.IsCurrent(Asked("Baroque", "Baroque music", found: true, Cutoff)[0]));
    }

    private static GenreDescription[] Asked(string genre, string title, bool found, DateTimeOffset at) =>
    [
        new()
        {
            Genre = genre,
            Title = title,
            Text = found ? "Baroque music refers to the period…" : null,
            Url = found ? "https://en.wikipedia.org/wiki/Baroque_music" : null,
            LookupUtc = at,
        },
    ];

    private static IEnumerable<string> Names(GenreDescription[] asked) =>
        GenreArticles.Unasked(GenreArticles.Titles.Keys, asked, Cutoff).Select(genre => genre.Key);
}
