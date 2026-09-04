using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Which tags a catalogue row implies — the rule, without a file in sight.
/// </summary>
/// <remarks>
/// The reason this lives in the domain: what a library-wide pass would write
/// into somebody's music is asserted here in milliseconds, with no ATL, no
/// database and no provider. Everything the integration tests then check is
/// about the <i>writing</i>.
/// </remarks>
public sealed class CatalogueTagsTests
{
    /// <summary>
    /// The rule that keeps a pass over 100,000 files from being a deletion.
    /// </summary>
    /// <remarks>
    /// There is no way to say "erase this field", so a fact the catalogue does
    /// not hold produces no entry at all. Written as a blank instead, the first
    /// run over a library would strip the genre, the composer and the comment
    /// off every file that MusicBrainz happens to be quiet about.
    /// </remarks>
    [Fact]
    public void AFactTheCatalogueDoesNotHoldIsNotOfferedAsABlank()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            RecordingTitle = "So What",
            AlbumTitle = "Kind of Blue",
        });

        Assert.False(tags.ContainsKey(CatalogueTags.Artist));
        Assert.False(tags.ContainsKey(CatalogueTags.Year));
        Assert.False(tags.ContainsKey(CatalogueTags.RecordingId));
        Assert.Equal("So What", tags[CatalogueTags.Title]);
    }

    /// <summary>
    /// The release's spelling wins, because it is the one on the album.
    /// </summary>
    /// <remarks>
    /// MusicBrainz prints "Song (feat. Someone)" on a release's track list and
    /// keeps the recording plain, and a person looking at that album expects to
    /// see what the sleeve says.
    /// </remarks>
    [Fact]
    public void TheTrackTitleOutranksTheRecordingTitle()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            RecordingTitle = "Sing It Back",
            TrackTitle = "Sing It Back (Boris Musical Mix)",
        });

        Assert.Equal("Sing It Back (Boris Musical Mix)", tags[CatalogueTags.Title]);
    }

    /// <summary>
    /// "Track 3 of 0" is worse than "track 3".
    /// </summary>
    /// <remarks>
    /// <c>Release.TrackCount</c> and <c>DiscCount</c> are nullable and come back
    /// zero on a release MusicBrainz lists no media for. A player reads a total
    /// of zero as a broken album rather than as an unknown one.
    /// </remarks>
    [Fact]
    public void ACountOfZeroIsNotATotal()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            TrackNumber = 3,
            TrackTotal = 0,
            DiscNumber = 1,
            DiscTotal = 0,
        });

        Assert.Equal("3", tags[CatalogueTags.TrackNumber]);
        Assert.Equal("1", tags[CatalogueTags.DiscNumber]);
        Assert.False(tags.ContainsKey(CatalogueTags.TrackTotal));
        Assert.False(tags.ContainsKey(CatalogueTags.DiscTotal));
    }

    /// <summary>
    /// A collaboration keeps both artists' identifiers.
    /// </summary>
    /// <remarks>
    /// ATL's <c>AdditionalFields</c> is one string per name and cannot hold the
    /// repeated tags Picard writes, so several ids share one field. The
    /// separator is the one ID3v2.3 already uses for the same list, and it is
    /// what beets and Lidarr split on.
    /// </remarks>
    [Fact]
    public void SeveralArtistsShareOneFieldRatherThanLosingAllButTheFirst()
    {
        var hart = new Mbid(new Guid("11111111-1111-4111-8111-111111111111"));
        var bonamassa = new Mbid(new Guid("22222222-2222-4222-8222-222222222222"));

        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            ArtistCredit = "Beth Hart & Joe Bonamassa",
            ArtistMbids = [hart, bonamassa],
        });

        Assert.Equal($"{hart}/{bonamassa}", tags[CatalogueTags.ArtistId]);
    }

    /// <summary>
    /// Partial dates stay partial: the year, and never an invented day.
    /// </summary>
    /// <remarks>
    /// The catalogue keeps year, month and day apart precisely because
    /// MusicBrainz publishes "1969" as often as it publishes a date, and the
    /// difference decides which of two editions looks older. Only the year
    /// crosses into a tag, so nothing here can promise the first of January.
    /// </remarks>
    [Fact]
    public void OnlyTheYearIsOfferedAsADate()
    {
        var tags = CatalogueTags.For(new CatalogueTagSource { Year = 1969 });

        Assert.Equal("1969", tags[CatalogueTags.Year]);
        Assert.DoesNotContain(tags.Keys, key => key.Contains("DATE", StringComparison.Ordinal));
    }

    /// <summary>Every identifier the catalogue holds reaches the file.</summary>
    [Fact]
    public void EveryIdentifierTheCatalogueHoldsIsOffered()
    {
        var id = new Guid("33333333-3333-4333-8333-333333333333");

        var tags = CatalogueTags.For(new CatalogueTagSource
        {
            RecordingMbid = new Mbid(id),
            ReleaseMbid = new Mbid(id),
            ReleaseGroupMbid = new Mbid(id),
            WorkMbid = new Mbid(id),
            AlbumArtistMbids = [new Mbid(id)],
            AcoustId = new AcoustId(id),
        });

        Assert.Equal(
            [
                CatalogueTags.AcoustIdField,
                CatalogueTags.AlbumArtistId,
                CatalogueTags.ReleaseId,
                CatalogueTags.ReleaseGroupId,
                CatalogueTags.RecordingId,
                CatalogueTags.WorkId,
            ],
            tags.Keys.Order(StringComparer.Ordinal));
    }
}
