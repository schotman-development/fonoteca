using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class PersonTagsTests
{
    [Theory]
    [InlineData("TITLE", "So What")]
    [InlineData("TITLE", null)]
    [InlineData("YEAR", "1959")]
    [InlineData("TRACKNUMBER", "12")]
    [InlineData("COMMENT", "first take\nsecond line")]
    [InlineData("GENRE", "Modal jazz")]
    public void AValueAFileCanCarryIsAccepted(string field, string? value) =>
        Assert.Null(PersonTags.Problem(field, value));

    [Theory]
    [InlineData("MUSICBRAINZ_TRACKID", "x")]
    [InlineData("ACOUSTID_ID", "x")]
    [InlineData("TITLE", "  ")]
    [InlineData("TITLE", "two\nlines")]
    [InlineData("TITLE", "a\u001Fb")]
    [InlineData("YEAR", "1959-10-12")]
    [InlineData("YEAR", "0")]
    [InlineData("TRACKNUMBER", "1000")]
    [InlineData("DISCNUMBER", "-1")]
    public void AValueNoFileShouldCarryIsRefused(string field, string? value) =>
        Assert.NotNull(PersonTags.Problem(field, value));

    [Theory]
    [InlineData("Occasion")]
    [InlineData("REPLAYGAIN_TRACK_GAIN")]
    [InlineData("Original Year (2)")]
    public void AFieldAPersonNamesIsTheirsToSet(string name) =>
        Assert.Null(PersonTags.Problem(name, "x"));

    [Theory]
    [InlineData("Mood")]
    [InlineData("TT2")]
    [InlineData("MusicBrainz Album Id")]
    [InlineData("musicbrainz_trackid")]
    [InlineData("Acoustid Id")]
    [InlineData("1st")]
    [InlineData("a=b")]
    [InlineData("Lyrics:eng")]
    [InlineData("Title")]
    [InlineData("Publisher")]
    [InlineData("Bpm")]
    [InlineData("Album Artist")]
    [InlineData("info.IART")]
    public void ANameNoContainerCanSafelyCarryIsNotOne(string name) =>
        Assert.NotNull(PersonTags.NameProblem(name));

    [Fact]
    public void TheAlbumsFieldsAreFieldsTheEditorSets() =>
        Assert.All(PersonTags.AlbumFields, field => Assert.Contains(field, PersonTags.Fields));

    [Fact]
    public void AnAlbumsCorrectionIsLaidOverItsPressings()
    {
        var edits = PersonEdits.Combine(
            """{"title":"Album's"}""",
            """{"title":"Pressing's","label":"Columbia"}""");

        Assert.Equal("Album's", edits["title"]);
        Assert.Equal("Columbia", edits["label"]);
    }
}
