using System.Diagnostics;
using System.Security.Cryptography;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Writing the whole catalogue into a file — ADR 0002's path, many fields at once.
/// </summary>
/// <remarks>
/// <see cref="AcoustIdTagWriteTests"/>'s companion, and the stakes are the same
/// or higher: this rewrites every identified file in the library rather than one
/// field on the ones a pass has just decided.
///
/// The per-format assertions go through <b>ffprobe</b> rather than either of the
/// two libraries under test, exactly as that class's do. ATL agreeing with
/// TagLib# proves they agree; a third tool from an unrelated project proves the
/// tag is where the format says it goes. <c>MUSICBRAINZ TRACK ID</c> — which is
/// what ATL produces from the title-case spelling on a FLAC — would satisfy both
/// of ours and be invisible to Picard.
/// </remarks>
public sealed class CatalogueTagWriteTests : IDisposable
{
    private static readonly Guid Recording = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Release = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Group = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Artist = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Cluster = new("55555555-5555-4555-8555-555555555555");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fonoteca-catalogue-tags-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingEventLog _events = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public CatalogueTagWriteTests() => Directory.CreateDirectory(_root);

    /// <summary>What the catalogue would say about a file it has fully answered.</summary>
    private static CatalogueTagSource Answered => new()
    {
        TrackTitle = "So What",
        RecordingTitle = "So What",
        RecordingMbid = new Mbid(Recording),
        TrackNumber = 1,
        TrackTotal = 5,
        DiscNumber = 1,
        DiscTotal = 1,
        AlbumTitle = "Kind of Blue",
        ReleaseMbid = new Mbid(Release),
        ReleaseGroupMbid = new Mbid(Group),
        Year = 1959,
        ArtistCredit = "Miles Davis",
        ArtistMbids = [new Mbid(Artist)],
        AlbumArtistCredit = "Miles Davis",
        AlbumArtistMbids = [new Mbid(Artist)],
        AcoustId = new AcoustId(Cluster),
        Genres = ["jazz", "modal jazz"],
        Isrcs = ["USSM15900113"],
        Credits = new CreditTags
        {
            Composers = ["Miles Davis"],
            Lyricists = ["Eddie Jefferson"],
            Conductors = ["Gil Evans"],
            Arrangers = ["Gil Evans"],
            Producers = ["Irving Townsend", "Teo Macero"],
            Engineers = ["Fred Plaut"],
            Mixers = ["Mark Wilder"],
            Performers = ["John Coltrane (tenor saxophone)", "Bill Evans (piano)"],
        },
        TrackGain = -6.23,
        TrackPeak = 0.988553,
        AlbumGain = -5.9,
        AlbumPeak = 0.999,
        DynamicRange = 13,
        AlbumDynamicRange = 12,
    };

    /// <summary>The rest of Picard's tags, as a filed, analysed and measured file carries them.</summary>
    private static CatalogueTagSource Picard => Answered with
    {
        ArtistSort = "Davis, Miles",
        AlbumArtistSort = "Davis, Miles",
        Artists = ["Miles Davis", "John Coltrane"],
        AlbumArtists = ["Miles Davis"],
        ComposerSorts = ["Davis, Miles"],
        ComposerMbids = [new Mbid(Artist)],
        Credits = Answered.Credits with { Writers = ["Bill Evans"], Remixers = ["Mark Wilder"], DjMixers = ["Gilles Peterson"] },
        Website = "https://www.milesdavis.com/",
        Label = "Columbia",
        CatalogNumber = "CL 1355",
        Barcode = "074646493528",
        ReleaseCountry = "US",
        ReleaseStatus = "Official",
        Media = "12\" Vinyl",
        ReleaseTrackMbid = new Mbid(Cluster),
        Asin = "B000002ADT",
        Script = "Latn",
        DiscSubtitle = "Side A",
        ReleaseTypes = ["Album", "Compilation"],
        OriginalYear = 1958,
        Compilation = true,
        Work = "Kind of Blue Suite",
        MovementName = "So What",
        Movement = 1,
        MovementTotal = 5,
        Languages = ["zxx"],
        AcoustIdFingerprint = "AQADtNIyRUkkJUkiCQ",
        TrackRange = 7.56,
        TempoAndKey = new TempoAndKey { Bpm = 136.2, BpmConfidence = 3.1, Key = "D", Scale = "minor", KeyStrength = 0.8 },
    };

    /// <summary>
    /// The rest of Picard's tags land under Picard's names, as a tool that is
    /// neither of ours reads them.
    /// </summary>
    /// <remarks>
    /// ffprobe names some ID3 frames and MP4 atoms its own way — <c>TPUB</c> is
    /// "publisher", <c>soar</c> "sort_artist", <c>TCMP</c> and <c>cpil</c>
    /// "compilation" — and reports an ID3 frame's first value only. The MP4
    /// atoms and the ID3 URL frame it does not report are read as atoms and
    /// frames below. An MP4 carries no WRITER, WEBSITE, ORIGINALYEAR or ASIN,
    /// as Picard writes none there.
    /// </remarks>
    [Theory]
    [InlineData("flac", "ARTISTSORT", "Davis, Miles")]
    [InlineData("flac", "ALBUMARTISTSORT", "Davis, Miles")]
    [InlineData("flac", "ARTISTS", "Miles Davis;John Coltrane")]
    [InlineData("flac", "ALBUMARTISTS", "Miles Davis")]
    [InlineData("flac", "COMPOSERSORT", "Davis, Miles")]
    [InlineData("flac", "MUSICBRAINZ_COMPOSERID", "44444444-4444-4444-8444-444444444444")]
    [InlineData("flac", "WRITER", "Bill Evans")]
    [InlineData("flac", "REMIXER", "Mark Wilder")]
    [InlineData("flac", "DJMIXER", "Gilles Peterson")]
    [InlineData("flac", "WEBSITE", "https://www.milesdavis.com/")]
    [InlineData("flac", "LABEL", "Columbia")]
    [InlineData("flac", "CATALOGNUMBER", "CL 1355")]
    [InlineData("flac", "BARCODE", "074646493528")]
    [InlineData("flac", "RELEASECOUNTRY", "US")]
    [InlineData("flac", "RELEASESTATUS", "official")]
    [InlineData("flac", "RELEASETYPE", "album;compilation")]
    [InlineData("flac", "MEDIA", "12\" Vinyl")]
    [InlineData("flac", "MUSICBRAINZ_RELEASETRACKID", "55555555-5555-4555-8555-555555555555")]
    [InlineData("flac", "ASIN", "B000002ADT")]
    [InlineData("flac", "SCRIPT", "Latn")]
    [InlineData("flac", "DISCSUBTITLE", "Side A")]
    [InlineData("flac", "ORIGINALYEAR", "1958")]
    [InlineData("flac", "COMPILATION", "1")]
    [InlineData("flac", "WORK", "Kind of Blue Suite")]
    [InlineData("flac", "MOVEMENTNAME", "So What")]
    [InlineData("flac", "MOVEMENT", "1")]
    [InlineData("flac", "MOVEMENTTOTAL", "5")]
    [InlineData("flac", "SHOWMOVEMENT", "1")]
    [InlineData("flac", "LANGUAGE", "zxx")]
    [InlineData("flac", "ACOUSTID_FINGERPRINT", "AQADtNIyRUkkJUkiCQ")]
    [InlineData("flac", "REPLAYGAIN_TRACK_RANGE", "7.56 dB")]
    [InlineData("flac", "REPLAYGAIN_REFERENCE_LOUDNESS", "-18.00 LUFS")]
    [InlineData("flac", "BPM", "136")]
    [InlineData("flac", "KEY", "Dm")]
    [InlineData("mp3", "artist-sort", "Davis, Miles")]
    [InlineData("mp3", "TSO2", "Davis, Miles")]
    [InlineData("mp3", "Artists", "Miles Davis")]
    [InlineData("mp3", "ALBUMARTISTS", "Miles Davis")]
    [InlineData("mp3", "TSOC", "Davis, Miles")]
    [InlineData("mp3", "MusicBrainz Composer Id", "44444444-4444-4444-8444-444444444444")]
    [InlineData("mp3", "Writer", "Bill Evans")]
    [InlineData("mp3", "TPE4", "Mark Wilder")]
    [InlineData("mp3", "DJMIXER", "Gilles Peterson")]
    [InlineData("mp3", "publisher", "Columbia")]
    [InlineData("mp3", "CATALOGNUMBER", "CL 1355")]
    [InlineData("mp3", "BARCODE", "074646493528")]
    [InlineData("mp3", "MusicBrainz Album Release Country", "US")]
    [InlineData("mp3", "MusicBrainz Album Status", "official")]
    [InlineData("mp3", "MusicBrainz Album Type", "album")]
    [InlineData("mp3", "TMED", "12\" Vinyl")]
    [InlineData("mp3", "MusicBrainz Release Track Id", "55555555-5555-4555-8555-555555555555")]
    [InlineData("mp3", "ASIN", "B000002ADT")]
    [InlineData("mp3", "SCRIPT", "Latn")]
    [InlineData("mp3", "TSST", "Side A")]
    [InlineData("mp3", "TDOR", "1958")]
    [InlineData("mp3", "compilation", "1")]
    [InlineData("mp3", "WORK", "Kind of Blue Suite")]
    [InlineData("mp3", "MOVEMENTNAME", "So What")]
    [InlineData("mp3", "MOVEMENT", "1")]
    [InlineData("mp3", "MOVEMENTTOTAL", "5")]
    [InlineData("mp3", "SHOWMOVEMENT", "1")]
    [InlineData("mp3", "language", "zxx")]
    [InlineData("mp3", "Acoustid Fingerprint", "AQADtNIyRUkkJUkiCQ")]
    [InlineData("mp3", "REPLAYGAIN_TRACK_RANGE", "7.56 dB")]
    [InlineData("mp3", "REPLAYGAIN_REFERENCE_LOUDNESS", "-18.00 LUFS")]
    [InlineData("mp3", "TBPM", "136")]
    [InlineData("mp3", "TKEY", "Dm")]
    [InlineData("m4a", "sort_artist", "Davis, Miles")]
    [InlineData("m4a", "sort_album_artist", "Davis, Miles")]
    [InlineData("m4a", "ARTISTS", "Miles Davis; John Coltrane")]
    [InlineData("m4a", "ALBUMARTISTS", "Miles Davis")]
    [InlineData("m4a", "sort_composer", "Davis, Miles")]
    [InlineData("m4a", "MusicBrainz Composer Id", "44444444-4444-4444-8444-444444444444")]
    [InlineData("m4a", "REMIXER", "Mark Wilder")]
    [InlineData("m4a", "DJMIXER", "Gilles Peterson")]
    [InlineData("m4a", "LABEL", "Columbia")]
    [InlineData("m4a", "CATALOGNUMBER", "CL 1355")]
    [InlineData("m4a", "BARCODE", "074646493528")]
    [InlineData("m4a", "MusicBrainz Album Release Country", "US")]
    [InlineData("m4a", "MusicBrainz Album Status", "official")]
    [InlineData("m4a", "MusicBrainz Album Type", "album; compilation")]
    [InlineData("m4a", "MEDIA", "12\" Vinyl")]
    [InlineData("m4a", "MusicBrainz Release Track Id", "55555555-5555-4555-8555-555555555555")]
    [InlineData("m4a", "SCRIPT", "Latn")]
    [InlineData("m4a", "DISCSUBTITLE", "Side A")]
    [InlineData("m4a", "compilation", "1")]
    [InlineData("m4a", "LANGUAGE", "zxx")]
    [InlineData("m4a", "Acoustid Fingerprint", "AQADtNIyRUkkJUkiCQ")]
    [InlineData("m4a", "replaygain_track_range", "7.56 dB")]
    [InlineData("m4a", "replaygain_reference_loudness", "-18.00 LUFS")]
    [InlineData("m4a", "initialkey", "Dm")]
    [InlineData("m4a", "WRITER", null)]
    [InlineData("m4a", "WEBSITE", null)]
    [InlineData("m4a", "ORIGINALYEAR", null)]
    [InlineData("m4a", "ASIN", null)]
    public async Task PicardsOtherTagsLandWherePicardPutsThem(string extension, string tag, string? expected)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await WritePicard(writer, path)).Status);

        Assert.Equal(expected, Tag(TagsReportedByFfprobe(path), tag));
    }

    /// <summary>The MP4 atoms and the MP3 URL frame ffprobe does not report, read where the container keeps them.</summary>
    [Fact]
    public async Task TheAtomsAndTheUrlFrameFfprobeDoesNotReportAreThere()
    {
        SkipWithoutTools();

        var (mp4Writer, mp4) = Given(SourceFor("m4a"));
        var (mp3Writer, mp3) = Given(SourceFor("mp3"));

        Assert.Equal(TagWriteStatus.Written, (await WritePicard(mp4Writer, mp4)).Status);
        Assert.Equal(TagWriteStatus.Written, (await WritePicard(mp3Writer, mp3)).Status);

        using (var file = TagLib.File.Create(Path.Combine(_root, mp4.Value)))
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, false);

            string? Atom(string name) => apple.DataBoxes(TagLib.ByteVector.FromString(name, TagLib.StringType.Latin1))
                .Select(box => box.Flags == (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsText ? box.Text : string.Join(",", box.Data.Data))
                .FirstOrDefault();

            Assert.Equal("0,136", Atom("tmpo"));
            Assert.Equal("Kind of Blue Suite", Atom("©wrk"));
            Assert.Equal("So What", Atom("©mvn"));
            Assert.Equal("1", Atom("©mvi"));
            Assert.Equal("5", Atom("©mvc"));
            Assert.Equal("1", Atom("shwm"));
        }

        using (var file = TagLib.File.Create(Path.Combine(_root, mp3.Value)))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);

            Assert.Equal("https://www.milesdavis.com/", id3.GetFrames("WOAR").Single().ToString());
        }
    }

    /// <summary>
    /// Written once, the same tags are nothing to do: every one reads back
    /// under the name it was planned by, typed or not.
    /// </summary>
    /// <remarks>
    /// The diff is the worklist, so a field the reading reports under another
    /// name than the plan's would be rewritten on every run, forever.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task PicardsOtherTagsWrittenOnceAreNothingToDo(string extension)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await WritePicard(writer, path)).Status);

        var again = await writer.PlanAsync(path, CatalogueTags.For(Picard), Token);

        Assert.Empty(again!.Changes);
    }

    /// <summary>
    /// A person's field of their own under a container's spelling of a
    /// catalogue field is one change to that field, and the person's value.
    /// </summary>
    /// <remarks>
    /// An MP4's "initialkey" is KEY; planned twice, the write would set it
    /// twice and fail its own read-back on whichever value lost.
    /// </remarks>
    [Fact]
    public async Task APersonsFieldSpelledAsACatalogueOneIsOneChangeAndTheirs()
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor("m4a"));

        var plan = await writer.PlanAsync(
            path, CatalogueTags.For(Picard), new Dictionary<string, string?> { ["initialkey"] = "Am" }, Token);

        var key = Assert.Single(plan!.Changes, change => change.Field.Equals("initialkey", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Am", key.To);

        Assert.Equal(
            TagWriteStatus.Written,
            (await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token)).Status);
    }

    private async Task<TagWriteResult> WritePicard(TagWriter writer, LibraryPath path)
    {
        var plan = await writer.PlanAsync(path, CatalogueTags.For(Picard), Token);
        return await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);
    }

    /// <summary>An MP4 is never planned an ISRC or a conductor; every other container is.</summary>
    [Fact]
    public void AnMp4IsNotGivenTheFieldsAtlWouldHideInIt()
    {
        Assert.Null(CatalogueTagFields.Spell(new LibraryPath("a/b.m4a"), CatalogueTags.Isrc));
        Assert.Null(CatalogueTagFields.Spell(new LibraryPath("a/b.m4a"), CatalogueTags.Conductor));
        Assert.Equal(CatalogueTags.Lyricist, CatalogueTagFields.Spell(new LibraryPath("a/b.m4a"), CatalogueTags.Lyricist));
        Assert.Equal(CatalogueTags.Isrc, CatalogueTagFields.Spell(new LibraryPath("a/b.flac"), CatalogueTags.Isrc));
        Assert.Equal(CatalogueTags.Conductor, CatalogueTagFields.Spell(new LibraryPath("a/b.mp3"), CatalogueTags.Conductor));
    }

    /// <summary>
    /// Genres and credits land under Picard's names, several values as several
    /// where the container can say so, as a tool that is neither of ours reads them.
    /// </summary>
    /// <remarks>
    /// ffprobe joins a Vorbis field's values with ";" and reports only an ID3
    /// frame's first, which is why the MP3 rows check the first. An MP4 holds one
    /// value joined with "; ", and no ISRC or conductor — see
    /// <c>CatalogueTagFields.NotInMp4</c>.
    /// </remarks>
    [Theory]
    [InlineData("flac", "genre", "jazz;modal jazz")]
    [InlineData("flac", "PRODUCER", "Irving Townsend;Teo Macero")]
    [InlineData("flac", "PERFORMER", "John Coltrane (tenor saxophone);Bill Evans (piano)")]
    [InlineData("flac", "LYRICIST", "Eddie Jefferson")]
    [InlineData("flac", "CONDUCTOR", "Gil Evans")]
    [InlineData("flac", "ISRC", "USSM15900113")]
    [InlineData("flac", "composer", "Miles Davis")]
    [InlineData("flac", "ENGINEER", "Fred Plaut")]
    [InlineData("flac", "MIXER", "Mark Wilder")]
    [InlineData("flac", "ARRANGER", "Gil Evans")]
    [InlineData("mp3", "genre", "jazz")]
    [InlineData("mp3", "PRODUCER", "Irving Townsend")]
    [InlineData("mp3", "TSRC", "USSM15900113")]
    [InlineData("mp3", "TEXT", "Eddie Jefferson")]
    [InlineData("mp3", "performer", "Gil Evans")]
    [InlineData("mp3", "composer", "Miles Davis")]
    [InlineData("m4a", "genre", "jazz; modal jazz")]
    [InlineData("m4a", "PERFORMER", "John Coltrane (tenor saxophone); Bill Evans (piano)")]
    [InlineData("m4a", "LYRICIST", "Eddie Jefferson")]
    [InlineData("m4a", "ISRC", null)]
    [InlineData("flac", "REPLAYGAIN_TRACK_GAIN", "-6.23 dB")]
    [InlineData("flac", "REPLAYGAIN_ALBUM_PEAK", "0.999000")]
    [InlineData("mp3", "REPLAYGAIN_TRACK_GAIN", "-6.23 dB")]
    [InlineData("mp3", "REPLAYGAIN_ALBUM_GAIN", "-5.90 dB")]
    [InlineData("m4a", "replaygain_track_peak", "0.988553")]
    [InlineData("m4a", "replaygain_album_gain", "-5.90 dB")]
    [InlineData("flac", "DYNAMIC RANGE", "13")]
    [InlineData("flac", "ALBUM DYNAMIC RANGE", "12")]
    [InlineData("mp3", "DYNAMIC RANGE", "13")]
    [InlineData("mp3", "ALBUM DYNAMIC RANGE", "12")]
    [InlineData("m4a", "DYNAMIC RANGE", "13")]
    [InlineData("m4a", "ALBUM DYNAMIC RANGE", "12")]
    public async Task GenresAndCreditsLandWhereOtherTaggersPutThem(string extension, string tag, string? expected)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Equal(expected, Tag(TagsReportedByFfprobe(path), tag));
    }

    /// <summary>An MP4's ReplayGain atoms are spelled in lowercase, as every other tagger spells them.</summary>
    [Fact]
    public async Task AnMp4sReplayGainIsSpelledInLowercase()
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor("m4a"));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Contains(TagsReportedByFfprobe(path).Keys, key => key == "replaygain_track_gain");
    }

    /// <summary>An MP4's DR atoms keep foobar2000's spelling, unlike its ReplayGain's.</summary>
    [Fact]
    public async Task AnMp4sDynamicRangeIsSpelledAsFoobarSpellsIt()
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor("m4a"));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Contains(TagsReportedByFfprobe(path).Keys, key => key == "DYNAMIC RANGE");
        Assert.Contains(TagsReportedByFfprobe(path).Keys, key => key == "ALBUM DYNAMIC RANGE");
    }

    /// <summary>An Opus file gets no REPLAYGAIN_* field: RFC 7845 gives it R128_TRACK_GAIN instead.</summary>
    [Fact]
    public void AnOpusFileGetsNoReplayGainField()
    {
        Assert.Null(CatalogueTagFields.Spell(new LibraryPath("a.opus"), CatalogueTags.TrackGain));
        Assert.Null(CatalogueTagFields.Spell(new LibraryPath("a.opus"), CatalogueTags.TrackRange));
        Assert.Null(CatalogueTagFields.Spell(new LibraryPath("a.opus"), CatalogueTags.ReferenceLoudness));
        Assert.Equal(CatalogueTags.TrackRange, CatalogueTagFields.Spell(new LibraryPath("a.ogg"), CatalogueTags.TrackRange));
        Assert.Equal(CatalogueTags.TrackGain, CatalogueTagFields.Spell(new LibraryPath("a.ogg"), CatalogueTags.TrackGain));
        Assert.Equal(CatalogueTags.DynamicRange, CatalogueTagFields.Spell(new LibraryPath("a.opus"), CatalogueTags.DynamicRange));
    }

    /// <summary>
    /// The two spellings, each confirmed by a tool that is neither of ours.
    /// </summary>
    /// <remarks>
    /// These are the authority on <see cref="CatalogueTagFields"/>. If a future
    /// ATL changes where it puts a custom field, this fails rather than a
    /// library quietly acquiring identifiers no other tool reads.
    /// </remarks>
    [Theory]
    [InlineData("flac", "MUSICBRAINZ_TRACKID", "MUSICBRAINZ_ALBUMID")]
    [InlineData("mp3", "MusicBrainz Track Id", "MusicBrainz Album Id")]
    [InlineData("m4a", "MusicBrainz Track Id", "MusicBrainz Album Id")]
    public async Task TheIdentifiersLandWhereEveryOtherTaggerLooksForThem(
        string extension,
        string expectedRecordingTag,
        string expectedReleaseTag)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Written, result.Status);

        var tags = TagsReportedByFfprobe(path);

        Assert.Equal(Recording.ToString("D"), tags[expectedRecordingTag], ignoreCase: true);
        Assert.Equal(Release.ToString("D"), tags[expectedReleaseTag], ignoreCase: true);
    }

    /// <summary>
    /// The ordinary fields, through the properties ATL owns rather than as custom ones.
    /// </summary>
    /// <remarks>
    /// A title put into <c>AdditionalFields["TITLE"]</c> is a second title beside
    /// the real one on every container that has a real one — valid, invisible,
    /// and only detectable with a third tool. ffprobe reports the real one under
    /// its own lowercase name, so finding the value there is the proof.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task TheOrdinaryFieldsGoWhereAPlayerReadsThem(string extension)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var tags = TagsReportedByFfprobe(path);

        // Case-insensitively, and only here: Vorbis comments come back as TITLE
        // and ID3 as title, which is the container's business rather than ours.
        // The identifier assertions above stay ordinal, because there the
        // underscore and the spaces are exactly what is being checked.
        Assert.Equal("So What", Tag(tags, "title"));
        Assert.Equal("Miles Davis", Tag(tags, "artist"));
        Assert.Equal("Kind of Blue", Tag(tags, "album"));
        Assert.Contains("1959", Tag(tags, "date") ?? "", StringComparison.Ordinal);
    }

    private static string? Tag(Dictionary<string, string> tags, string name) => tags
        .FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
        .Value;

    /// <summary>
    /// The property that makes running this over a whole library affordable.
    /// </summary>
    /// <remarks>
    /// There is no <c>TagsWrittenUtc</c> column and no migration: the diff is the
    /// worklist. A second run reads each file, finds it already says what the
    /// catalogue says, and does not open it for writing — so re-running after
    /// enriching one more album costs a tag read per file rather than eight
    /// thousand container rewrites.
    /// </remarks>
    [Fact]
    public async Task WritingTheSameCatalogueTwiceRewritesNothing()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var afterFirst = Hash(path);
        var again = await Write(writer, path);

        Assert.Equal(TagWriteStatus.NothingToDo, again.Status);
        Assert.Equal(afterFirst, Hash(path));

        // One journal entry, not two: an undo entry for a write that did not
        // happen is worse than useless, because undoing it restores nothing.
        Assert.Single(_events.Entries);
    }

    /// <summary>
    /// A write is put back field by field from what its plan recorded: the
    /// fields it added are gone again and the ones it replaced hold their old values.
    /// </summary>
    /// <remarks>
    /// Read back by ffprobe as well as by ours, because a removal that left an
    /// empty frame behind satisfies a reader that treats empty as absent and
    /// shows up in every player that does not.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    [InlineData("artwork")]
    [InlineData("tagged")]
    public async Task AWriteIsPutBackFromWhatItsPlanRecorded(string source)
    {
        SkipWithoutTools();

        var (writer, path) = Given(source switch
        {
            "ogg" => Corpus.Ogg,
            "artwork" => Corpus.FlacWithArtwork,
            "tagged" => Corpus.AlreadyTaggedFlac,
            _ => SourceFor(source),
        });

        // Ogg keeps its comments on the stream, not the container.
        var stream = source == "ogg";
        var before = TagsReportedByFfprobe(path, stream);
        var written = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Written, written.Status);

        var restore = await writer.PlanRestoreAsync(
            path,
            [.. written.Plan!.Changes.Select(change => KeyValuePair.Create(change.Field, change.From))],
            Token);

        var undone = await writer.ApplyAsync(restore, "file-1", "undo", "owner", "tagging.undo", Token);

        Assert.True(undone.Status == TagWriteStatus.Written, undone.Detail);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root)).ReadAsync(path, cancellationToken: Token);

        foreach (var change in written.Plan.Changes)
        {
            Assert.Equal(change.From, reading.Find(change.Field));
        }

        // Keys case-folded: ATL uppercases a Vorbis key on every save it makes,
        // "encoder" included, and that is the container's spelling, not a value.
        Assert.NotEmpty(TagsReportedByFfprobe(path, stream));
        Assert.Equal(Folded(before), Folded(TagsReportedByFfprobe(path, stream)));

        static string[] Folded(Dictionary<string, string> tags) =>
            [.. tags.Select(tag => $"{tag.Key.ToUpperInvariant()}={tag.Value}").Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The property everything else rests on: the audio is not touched.
    /// </summary>
    /// <remarks>
    /// Decoded to raw PCM with ffmpeg and hashed, before and after. Comparing the
    /// files themselves would prove nothing — the container legitimately changes.
    /// </remarks>
    [Fact]
    public async Task TheAudioItselfIsBitIdenticalAfterTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);
        var before = DecodedAudioHash(path);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Equal(before, DecodedAudioHash(path));
    }

    [Fact]
    public async Task EmbeddedArtworkSurvivesTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.FlacWithArtwork);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        var before = await reader.ReadAsync(path, cancellationToken: Token);
        Assert.Equal(1, before.PictureCount);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var after = await reader.ReadAsync(path, cancellationToken: Token);

        Assert.Equal(1, after.PictureCount);
        Assert.Equal(before.PictureDigests, after.PictureDigests);
    }

    /// <summary>
    /// Correcting a field the file already held is an edit, not a verification failure.
    /// </summary>
    /// <remarks>
    /// The check that a write disturbed nothing else is one-directional and
    /// reports <i>everything</i> that moved, so on a multi-field write most of
    /// what it reports is the point. Excluding the intended fields is what makes
    /// overwriting a wrong album name possible at all — and the AcoustID path had
    /// the same latent problem, where replacing an existing, different AcoustID
    /// always failed verification and rolled itself back.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task CorrectingAValueTheFileAlreadyCarriedIsNotReadAsLosingIt(string extension)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        // The same file again, saying something else about every field. The
        // exclusion matches ATL's own key names, so a container that reported
        // its custom fields under a prefix would abort here rather than in
        // production.
        var corrected = Answered with
        {
            TrackTitle = "Blue in Green",
            AlbumTitle = "Kind of Blue (Legacy Edition)",
            ArtistCredit = "Miles Davis Sextet",
            TrackNumber = 3,
            Year = 1997,
            RecordingMbid = new Mbid(new Guid("99999999-9999-4999-8999-999999999999")),
        };

        var plan = await writer.PlanAsync(path, CatalogueTags.For(corrected), Token);
        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);

        var tags = TagsReportedByFfprobe(path);

        Assert.Equal("Blue in Green", Tag(tags, "title"));
        Assert.Equal("Kind of Blue (Legacy Edition)", Tag(tags, "album"));
        Assert.Contains("1997", Tag(tags, "date") ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The default posture: a complete dry run that changes not one byte.
    /// </summary>
    [Fact]
    public async Task NothingIsWrittenWhileFileMutationIsDisabled()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac, allowMutation: false);
        var before = Hash(path);

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Refused, result.Status);
        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        // The plan is real, which is what makes the run a dry run rather than a
        // no-op: the whole diff has been computed and journalled.
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsNoOp);

        var refusal = Assert.Single(_events.Entries);
        Assert.Equal("tagging.catalogue.refused", refusal.Type);
    }

    /// <summary>
    /// The undo journal has to distinguish "was absent" from "was blank".
    /// </summary>
    /// <remarks>
    /// Reversing an absent field means removing it; reversing a blank one means
    /// restoring a blank. Every field on an untagged FLAC is the first case.
    /// </remarks>
    [Fact]
    public async Task TheUndoJournalRecordsEveryFieldItChanged()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);

        await Write(writer, path);

        var entry = Assert.Single(_events.Entries);

        Assert.Equal("tagging.catalogue.written", entry.Type);
        Assert.Equal("file", entry.SubjectType);
        Assert.Equal("batch", entry.CorrelationId);
        Assert.Contains("\"previous\":null", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(CatalogueTags.RecordingId, entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(Recording.ToString("D"), entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An ID3v2.3 date keeps its day through a write.
    /// </summary>
    /// <remarks>
    /// ID3v2.3 keeps the year and the day apart, in <c>TYER</c> and <c>TDAT</c>;
    /// ATL reads that as a year alone and would write <c>TDRC</c> without the day.
    /// The writer hands ATL the date TagLib# read, so the day survives the upgrade
    /// to 2.4.
    /// </remarks>
    [Fact]
    public async Task AnId3v23DateKeepsItsDayThroughAWrite()
    {
        SkipWithoutTools();

        var path = await Mp3With("dated.mp3", "3", "-metadata", "date=1959-08-17");

        var result = await Write(Writer(allowMutation: true), path);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        Assert.Equal("1959-08-17", TagsReportedByFfprobe(path)["date"]);
    }

    /// <summary>
    /// An ID3v2.4 year stays a year, in the recording date and the original date.
    /// </summary>
    /// <remarks>
    /// ATL reads a 2.4 <c>TDRC</c> or <c>TDOR</c> of "1959" as a full date and
    /// would write "1959-01-01" — a first of January nobody claimed.
    /// </remarks>
    [Fact]
    public async Task AnId3v24YearIsNotGivenADay()
    {
        SkipWithoutTools();

        var path = await Mp3With("year.mp3", "4", "-metadata", "date=1959", "-metadata", "TDOR=1958");

        var result = await Write(Writer(allowMutation: true), path);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        var tags = TagsReportedByFfprobe(path);
        Assert.Equal("1959", tags["date"]);
        Assert.Equal("1958", tags["TDOR"]);
    }

    /// <summary>
    /// A file the two readers time differently before the write is still written.
    /// </summary>
    /// <remarks>
    /// LAME's CBR header says "Info" rather than "Xing", and sits in a frame at a
    /// lower bitrate than the stream. TagLib# 2.3 only knows "Xing", so it times
    /// the stream at the header frame's 64 kbps and reads a 128 kbps file as twice
    /// its length — 207 files in the target library. Each reader is held to its
    /// own reading of the original, which is what catches a write that lost audio.
    /// </remarks>
    [Fact]
    public async Task AFileTheTwoReadersTimeDifferentlyIsStillWritten()
    {
        SkipWithoutTools();

        var raw = Path.Combine(_root, "raw.mp3");

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            [
                "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=10:sample_rate=48000",
                "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k", "-write_xing", "0", "-id3v2_version", "0", raw,
            ])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        // A 192-byte MPEG-1 layer III frame at 64 kbps, 48 kHz, stereo, carrying
        // the "Info" header over the 128 kbps frames that follow it.
        var audio = await File.ReadAllBytesAsync(raw, Token);
        var info = new byte[192];
        info[0] = 0xFF; info[1] = 0xFB; info[2] = 0x54;
        "Info"u8.CopyTo(info.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(40), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(44), audio.Length / 384);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(48), audio.Length);
        await File.WriteAllBytesAsync(Path.Combine(_root, "info.mp3"), [.. info, .. audio], Token);

        var result = await Write(Writer(allowMutation: true), new LibraryPath("info.mp3"));

        Assert.Equal(TagWriteStatus.Written, result.Status);
    }

    private async Task<LibraryPath> Mp3With(string name, string id3Version, params string[] metadata)
    {
        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            [
                "-v", "error", "-y", "-i", Corpus.Mp3, "-c", "copy", "-id3v2_version", id3Version,
                .. metadata, Path.Combine(_root, name),
            ])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        return new LibraryPath(name);
    }

    /// <summary>
    /// An MP3's MusicBrainz recording id keeps its owner through a write.
    /// </summary>
    /// <remarks>
    /// ATL wrote a text-encoding byte in front of every <c>UFID</c> owner, and
    /// 748 files here lost the id to every other reader that way.
    /// </remarks>
    [Fact]
    public async Task AnMp3sMusicBrainzIdKeepsItsOwnerThroughAWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);
        Identify(path, "http://musicbrainz.org");

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Equal(("http://musicbrainz.org", Recording.ToString()), Identifier(path));

        // Byte for byte: no encoding byte before the owner, no NUL after the id.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, path.Value), Token);
        var at = bytes.AsSpan().IndexOf("UFID"u8);
        var size = (bytes[at + 4] << 21) | (bytes[at + 5] << 14) | (bytes[at + 6] << 7) | bytes[at + 7];
        Assert.Equal(
            [.. "http://musicbrainz.org"u8, 0, .. System.Text.Encoding.Latin1.GetBytes(Recording.ToString())],
            bytes[(at + 10)..(at + 10 + size)]);
    }

    [Fact]
    public async Task ADamagedMusicBrainzIdIsRepairedWhenNothingElseNeedsWriting()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);
        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        // What an earlier save left behind: the owner behind a stray byte.
        Identify(path, "\u0003http://musicbrainz.org");

        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered), Token);
        Assert.Equal("UFID", Assert.Single(plan!.Changes).Field);

        var result = await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        Assert.Equal(("http://musicbrainz.org", Recording.ToString()), Identifier(path));
    }

    [Fact]
    public async Task AWebLinkSurvivesAWrite()
    {
        SkipWithoutTools();

        // An ID3v2.3 tag of one WXXX frame — description, NUL, URL — on an MP3
        // that had none.
        var name = "linked.mp3";
        var bare = Path.Combine(_root, "bare.mp3");

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg", ["-v", "error", "-y", "-i", Corpus.Mp3, "-c", "copy", "-id3v2_version", "0", bare])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        byte[] body = [0, .. "Home"u8, 0, .. "https://example.org/band"u8];
        byte[] frame = [.. "WXXX"u8, 0, 0, 0, (byte)body.Length, 0, 0, .. body];
        byte[] tag = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, (byte)frame.Length, .. frame];
        await File.WriteAllBytesAsync(Path.Combine(_root, name), [.. tag, .. await File.ReadAllBytesAsync(bare, Token)], Token);

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), new LibraryPath(name))).Status);

        // Read off the bytes: TagLib# 2.3 reads neither half of a WXXX frame,
        // this one or the original.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, name), Token);
        var at = bytes.AsSpan().IndexOf("WXXX"u8);
        Assert.True(at > 0);

        var size = (bytes[at + 4] << 21) | (bytes[at + 5] << 14) | (bytes[at + 6] << 7) | bytes[at + 7];
        Assert.Equal(body, bytes[(at + 10)..(at + 10 + size)]);
    }

    [Fact]
    public async Task AFieldAnApeTagHoldsToo()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);
        const string stale = "99999999-9999-4999-8999-999999999999";

        using (var file = TagLib.File.Create(Path.Combine(_root, path.Value)))
        {
            file.Tag.MusicBrainzArtistId = stale;
            ((TagLib.Ape.Tag)file.GetTag(TagLib.TagTypes.Ape, true)).SetValue("MusicBrainz Artist Id", stale);
            file.Save();
        }

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        using var written = TagLib.File.Create(Path.Combine(_root, path.Value));
        Assert.Equal(Artist.ToString(), ((TagLib.Ape.Tag)written.GetTag(TagLib.TagTypes.Ape, false)).GetItem("MusicBrainz Artist Id").ToString());
    }

    /// <summary>
    /// An ID3 tag in front of a FLAC, which ATL cannot read, is dropped by the write.
    /// </summary>
    [Fact]
    public async Task AnId3TagInFrontOfAFlacIsDroppedByTheWrite()
    {
        SkipWithoutTools();

        var name = "prefixed.flac";
        byte[] title = [0, .. "Old"u8];
        byte[] frame = [.. "TIT2"u8, 0, 0, 0, (byte)title.Length, 0, 0, .. title];
        byte[] tag = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, (byte)frame.Length, .. frame];
        await File.WriteAllBytesAsync(Path.Combine(_root, name), [.. tag, .. await File.ReadAllBytesAsync(Corpus.Flac, Token)], Token);

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), new LibraryPath(name))).Status);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, name), Token);
        Assert.Equal("fLaC"u8.ToArray(), bytes[..4]);
        Assert.Equal("So What", TagsReportedByFfprobe(new LibraryPath(name))["TITLE"]);
    }

    /// <summary>...unless the ID3 tag holds a cover the FLAC does not.</summary>
    [Fact]
    public async Task AnId3TagInFrontOfAFlacHoldingTheOnlyCoverIsKept()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Id3PrefixedFlac);
        var before = Hash(path);

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.VerificationFailed, result.Status);
        Assert.Contains("picture", result.Detail, StringComparison.Ordinal);
        Assert.Equal(before, Hash(path));
    }

    /// <summary>A cover stored inside a Vorbis comment, which ATL drops on save, moves to a picture block of its own.</summary>
    /// <remarks>
    /// Beside an ordinary picture block, as in the 51 files here: ATL matches
    /// pictures to blocks by position and the comment's one has none.
    /// </remarks>
    [Fact]
    public async Task ACoverStoredInAVorbisCommentMovesToAPictureBlock()
    {
        SkipWithoutTools();

        var image = await File.ReadAllBytesAsync(await Cover(), Token);
        byte[] mime = [.. "image/png"u8];
        byte[] block =
        [
            0, 0, 0, 3, 0, 0, 0, (byte)mime.Length, .. mime, 0, 0, 0, 0,
            0, 0, 0, 16, 0, 0, 0, 16, 0, 0, 0, 24, 0, 0, 0, 0,
            (byte)(image.Length >> 24), (byte)(image.Length >> 16), (byte)(image.Length >> 8), (byte)image.Length,
            .. image,
        ];

        var name = "comment-cover.flac";

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            [
                "-v", "error", "-y", "-i", Corpus.FlacWithArtwork, "-map", "0", "-c", "copy",
                "-metadata", "METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(block), Path.Combine(_root, name),
            ])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        var writer = Writer(allowMutation: true);
        var path = new LibraryPath(name);
        var audio = DecodedAudioHash(path);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        // Both covers, each in a block of its own, and the audio untouched.
        using var written = TagLib.File.Create(Path.Combine(_root, name));
        Assert.Equal(2, written.Tag.Pictures.Length);
        Assert.Contains(written.Tag.Pictures, picture => picture.Data.Data.AsSpan().SequenceEqual(image));
        Assert.Equal(audio, DecodedAudioHash(path));
    }

    /// <summary>Totals held under two keys, which ATL reads as none, survive a write that does not set them.</summary>
    [Fact]
    public async Task TotalsUnderTwoKeysSurviveAWriteThatDoesNotSetThem()
    {
        SkipWithoutTools();

        var name = "totals.flac";

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            [
                "-v", "error", "-y", "-i", Corpus.Flac, "-c", "copy",
                "-metadata", "TRACKTOTAL=18", "-metadata", "TOTALTRACKS=18", "-metadata", "DISCTOTAL=1",
                Path.Combine(_root, name),
            ])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        var writer = Writer(allowMutation: true);
        var plan = await writer.PlanAsync(
            new LibraryPath(name), CatalogueTags.For(Answered with { TrackTotal = null, DiscTotal = null }), Token);
        var result = await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);

        using var written = TagLib.File.Create(Path.Combine(_root, name));
        Assert.Equal(18u, written.Tag.TrackCount);
        Assert.Equal(1u, written.Tag.DiscCount);
    }

    /// <summary>A stored 0001-01-01, a tagger's "no date", may go when ATL drops it.</summary>
    [Fact]
    public async Task APlaceholderDateMayGo()
    {
        SkipWithoutTools();

        var name = "placeholder.flac";

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            ["-v", "error", "-y", "-i", Corpus.Flac, "-c", "copy", "-metadata", "date=0001-01-01", Path.Combine(_root, name)])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        var writer = Writer(allowMutation: true);
        var plan = await writer.PlanAsync(new LibraryPath(name), CatalogueTags.For(Answered with { Year = null }), Token);
        var result = await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);
    }

    /// <summary>A date's time and zone are part of it: a write that would drop the zone is refused.</summary>
    [Fact]
    public async Task ADateWhoseZoneAWriteWouldDropRefusesTheWrite()
    {
        SkipWithoutTools();

        var path = await FlacWith("zoned.flac", "date=2023-09-08T07:00:00Z");
        var before = Hash(path);

        var result = await WriteKeepingTheDate(path);

        Assert.Equal(TagWriteStatus.VerificationFailed, result.Status);
        Assert.Equal(before, Hash(path));
    }

    /// <summary>ATL ends every file identifier with a NUL, and only MusicBrainz's is known to be text.</summary>
    [Fact]
    public async Task AnIdentifierOfAnotherOwnerRefusesTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);
        Identify(path, "http://www.cddb.com/id3/taginfo1.html");
        var before = Hash(path);

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.VerificationFailed, result.Status);
        Assert.Contains("file identifier", result.Detail, StringComparison.Ordinal);
        Assert.Equal(before, Hash(path));
    }

    /// <summary>A MusicBrainz UFID with nothing after its owner, which TagLib# reads as no identifier at all.</summary>
    [Fact]
    public async Task AnEmptyMusicBrainzIdentifierIsWrittenBackAsItWas()
    {
        SkipWithoutTools();

        byte[] body = [.. "http://musicbrainz.org"u8, 0];
        var path = await Mp3Tagged("empty-id.mp3", 3, Frame("UFID", body));

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);
        Assert.Equal(body, FrameBody(path, "UFID"));
    }

    /// <summary>
    /// A MusicBrainz id trimmed in a tag ATL wrote with no padding leaves one
    /// byte of it, and ATL reads one to four as audio: a 166-second MP3 timed at
    /// 887 refused 116 writes here.
    /// </summary>
    [Fact]
    public async Task AnIdTrimmedInATagWithNoPaddingLeavesTheFileItsLength()
    {
        SkipWithoutTools();

        var path = await Mp3Tagged("tight.mp3", 3, Frame("UFID", [.. "http://musicbrainz.org"u8, 0, .. System.Text.Encoding.Latin1.GetBytes(Recording.ToString())]));
        var length = new ATL.Track(Path.Combine(_root, path.Value)).DurationMs;

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);
        Assert.Equal(length, new ATL.Track(Path.Combine(_root, path.Value)).DurationMs, 1.0);
    }

    /// <summary>An original date ATL would give a day it never had refuses the write.</summary>
    [Fact]
    public async Task AnOriginalDateGivenADayRefusesTheWrite()
    {
        SkipWithoutTools();

        var path = await Mp3With("month.mp3", "4", "-metadata", "date=1959", "-metadata", "TDOR=1958-05");
        var before = Hash(path);

        var result = await Write(Writer(allowMutation: true), path);

        Assert.Equal(TagWriteStatus.VerificationFailed, result.Status);
        Assert.Equal(before, Hash(path));
    }

    /// <summary>An ID3v2.2 tag, whose frames ATL writes back under their 2.4 names: TMT is TMED.</summary>
    [Fact]
    public async Task AnId3v22TagIsWrittenUnderItsNewNamesWithoutLosingAField()
    {
        SkipWithoutTools();

        var path = await Mp3Tagged("old.mp3", 2, Frame22("TT2", [0, .. "Old"u8]), Frame22("TMT", [0, .. "CD"u8]));

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);
        Assert.Equal("CD", System.Text.Encoding.UTF8.GetString(FrameBody(path, "TMED")[1..]).TrimEnd('\0'));
    }

    /// <summary>A web link ended with a NUL, as 11 here are, is still the same link whether ATL keeps it or not.</summary>
    [Fact]
    public async Task AnUndescribedWebLinkSurvivesAWrite()
    {
        SkipWithoutTools();

        var path = await Mp3Tagged("wxxx.mp3", 3, Frame("WXXX", [0, 0, .. "https://example.org/band"u8, 0]));

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);
        Assert.Equal("https://example.org/band", System.Text.Encoding.Latin1.GetString(FrameBody(path, "WXXX")[2..]).TrimEnd('\0'));
    }

    /// <summary>
    /// The old ReplayGain frame keeps its bytes beside the new ReplayGain field,
    /// as on 2 files here — and the field takes the catalogue's value, which
    /// replaces a file's own (the owner's choice).
    /// </summary>
    [Fact]
    public async Task OldAndNewReplayGainBothSurviveAWrite()
    {
        SkipWithoutTools();

        // With the status flag the real ones carry: drop it if the audio changes.
        byte[] rgad = [0, 0, 0, 0, 0x2E, 0x5D, 0, 0];
        var old = Frame("RGAD", rgad);
        old[8] = 0x40;

        var path = await Mp3Tagged(
            "gain.mp3",
            3,
            old,
            Frame("TXXX", [0, .. "replaygain_track_gain"u8, 0, .. "-9.30 dB"u8]),
            Frame("PRIV", new byte[64]));
        var audio = DecodedAudioHash(path);

        // ATL writes it a byte short and with no padding, so putting it back
        // grows the tag and moves the audio: which has to come through whole.
        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);
        Assert.Equal(rgad, FrameBody(path, "RGAD"));
        Assert.Equal(audio, DecodedAudioHash(path));

        using var written = TagLib.File.Create(Path.Combine(_root, path.Value));
        var gains = ((TagLib.Id3v2.Tag)written.GetTag(TagLib.TagTypes.Id3v2, false))
            .GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
            .Where(frame => frame.Description.Equals("replaygain_track_gain", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(gains);
        Assert.All(gains, frame => Assert.Equal("-6.23 dB", frame.Text[0]));
    }

    /// <summary>An unsynchronised tag holding a file identifier refuses: nothing here can take ATL's NUL off it.</summary>
    [Fact]
    public async Task AnUnsynchronisedTagWithAnIdentifierRefusesTheWrite()
    {
        SkipWithoutTools();

        var path = await Mp3Tagged("unsync.mp3", 3, Frame("UFID", [.. "http://musicbrainz.org"u8, 0, .. "11111111-1111-4111-8111-111111111111"u8]));
        var full = Path.Combine(_root, path.Value);
        var bytes = await File.ReadAllBytesAsync(full, Token);
        bytes[5] = 0x80;
        await File.WriteAllBytesAsync(full, bytes, Token);
        var before = Hash(path);

        var result = await Write(Writer(allowMutation: true), path);

        Assert.Equal(TagWriteStatus.VerificationFailed, result.Status);
        Assert.Equal(before, Hash(path));
    }

    /// <summary>The old ID3v1 tag keeps its year and gets a genre it has a number for.</summary>
    [Fact]
    public async Task TheOldTagKeepsItsYearAndGenre()
    {
        SkipWithoutTools();

        // ID3v2.3 with a day, which the writer hands ATL as a full date — the
        // case where ATL empties the old tag's year.
        var path = await Mp3With("old-tag.mp3", "3", "-write_id3v1", "1", "-metadata", "date=1959-08-17", "-metadata", "genre=Cool;Jazz");

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);

        var old = OldTag(path);
        Assert.Equal("1959", System.Text.Encoding.Latin1.GetString(old, 93, 4));
        Assert.Equal(8, old[127]);
    }

    /// <summary>
    /// An ID3v2.2 tag holding only a day, "01 Jan", with the year in the old
    /// ID3v1 tag — eleven files here. The year is the file's date, and the
    /// write that puts it in the modern tag is no change of date.
    /// </summary>
    [Fact]
    public async Task AYearOnlyTheOldTagHeldIsNoChangeOfDate()
    {
        SkipWithoutTools();

        var path = await Mp3Tagged("sloe.mp3", 2, Frame22("TT2", [0, .. "Old"u8]), Frame22("TDA", [0, .. "0101"u8]));
        var full = Path.Combine(_root, path.Value);

        var old = new byte[128];
        "TAG"u8.CopyTo(old);
        "2007"u8.CopyTo(old.AsSpan(93));
        old[127] = 255;
        await File.AppendAllBytesAsync(full, old, Token);

        Assert.Equal(TagWriteStatus.Written, (await WriteKeepingTheDate(path)).Status);

        using var written = TagLib.File.Create(full);
        Assert.Equal(2007u, written.GetTag(TagLib.TagTypes.Id3v2, false).Year);
        Assert.Equal("2007", System.Text.Encoding.Latin1.GetString(OldTag(path), 93, 4));
    }

    /// <summary>An MP3 with no old tag gets one, for whatever reads only that.</summary>
    [Fact]
    public async Task AnMp3WithNoOldTagGetsOne()
    {
        SkipWithoutTools();

        var path = await Mp3With("no-old-tag.mp3", "4");
        Assert.NotEqual("TAG"u8.ToArray(), OldTag(path)[..3]);

        Assert.Equal(TagWriteStatus.Written, (await Write(Writer(allowMutation: true), path)).Status);

        var old = OldTag(path);
        Assert.Equal("TAG"u8.ToArray(), old[..3]);
        Assert.Equal("So What", System.Text.Encoding.Latin1.GetString(old, 3, 30).TrimEnd('\0'));
        Assert.Equal("Kind of Blue", System.Text.Encoding.Latin1.GetString(old, 63, 30).TrimEnd('\0'));
        Assert.Equal("1959", System.Text.Encoding.Latin1.GetString(old, 93, 4));
        Assert.Equal(1, old[126]);
    }

    private byte[] OldTag(LibraryPath path)
    {
        var bytes = File.ReadAllBytes(Path.Combine(_root, path.Value));
        return bytes[^128..];
    }

    /// <summary>The catalogue's answer, with the year left out so the file's own date is kept.</summary>
    private async Task<TagWriteResult> WriteKeepingTheDate(LibraryPath path)
    {
        var writer = Writer(allowMutation: true);
        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered with { Year = null }), Token);
        return await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);
    }

    private async Task<LibraryPath> FlacWith(string name, params string[] metadata)
    {
        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg",
            ["-v", "error", "-y", "-i", Corpus.Flac, "-c", "copy", .. metadata.SelectMany(value => new[] { "-metadata", value }), Path.Combine(_root, name)])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        return new LibraryPath(name);
    }

    /// <summary>An MP3 whose only tag is the ID3v2 frames given, byte for byte.</summary>
    private async Task<LibraryPath> Mp3Tagged(string name, byte version, params byte[][] frames)
    {
        var bare = Path.Combine(_root, "bare-" + name);

        using (var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg", ["-v", "error", "-y", "-i", Corpus.Mp3, "-c", "copy", "-id3v2_version", "0", bare])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        byte[] body = [.. frames.SelectMany(frame => frame)];
        byte[] tag =
        [
            .. "ID3"u8, version, 0, 0,
            (byte)((body.Length >> 21) & 0x7F), (byte)((body.Length >> 14) & 0x7F),
            (byte)((body.Length >> 7) & 0x7F), (byte)(body.Length & 0x7F),
            .. body,
        ];

        await File.WriteAllBytesAsync(Path.Combine(_root, name), [.. tag, .. await File.ReadAllBytesAsync(bare, Token)], Token);
        File.Delete(bare);

        return new LibraryPath(name);
    }

    /// <summary>An ID3v2.3 frame.</summary>
    private static byte[] Frame(string id, params byte[] body) =>
        [.. System.Text.Encoding.Latin1.GetBytes(id), (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, 0, 0, .. body];

    /// <summary>An ID3v2.2 frame: three letters and three bytes of size.</summary>
    private static byte[] Frame22(string id, params byte[] body) =>
        [.. System.Text.Encoding.Latin1.GetBytes(id), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];

    /// <summary>The body of the first ID3v2.4 frame of that name in the file, read off its bytes.</summary>
    private byte[] FrameBody(LibraryPath path, string id)
    {
        var bytes = File.ReadAllBytes(Path.Combine(_root, path.Value));
        var at = bytes.AsSpan().IndexOf(System.Text.Encoding.Latin1.GetBytes(id));
        Assert.True(at > 0, $"No {id} frame.");

        var size = (bytes[at + 4] << 21) | (bytes[at + 5] << 14) | (bytes[at + 6] << 7) | bytes[at + 7];
        return bytes[(at + 10)..(at + 10 + size)];
    }

    /// <summary>A small PNG, made by ffmpeg.</summary>
    private async Task<string> Cover()
    {
        var cover = Path.Combine(_root, "cover.png");

        using var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=teal:s=16x16:d=1", "-frames:v", "1", cover]));

        await ffmpeg!.WaitForExitAsync(Token);
        Assert.Equal(0, ffmpeg.ExitCode);

        return cover;
    }

    /// <summary>Gives an MP3 a MusicBrainz recording id, under the owner given.</summary>
    private void Identify(LibraryPath path, string owner)
    {
        using var file = TagLib.File.Create(Path.Combine(_root, path.Value));
        var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);

        foreach (var frame in tag.GetFrames<TagLib.Id3v2.UniqueFileIdentifierFrame>().ToList()) tag.RemoveFrame(frame);

        tag.AddFrame(new TagLib.Id3v2.UniqueFileIdentifierFrame(
            owner, TagLib.ByteVector.FromString(Recording.ToString(), TagLib.StringType.Latin1)));
        file.Save();
    }

    private (string Owner, string Identifier) Identifier(LibraryPath path)
    {
        using var file = TagLib.File.Create(Path.Combine(_root, path.Value));
        var frame = Assert.Single(((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false))
            .GetFrames<TagLib.Id3v2.UniqueFileIdentifierFrame>());

        return (frame.Owner, frame.Identifier.ToString(TagLib.StringType.Latin1).TrimEnd('\0'));
    }

    /// <summary>A container with nowhere to put these is reported, not failed.</summary>
    [Fact]
    public async Task AContainerThatCannotCarryTheTagsIsReportedRatherThanTreatedAsAFailure()
    {
        SkipWithoutTools();

        File.Copy(Corpus.Flac, Path.Combine(_root, "track.dsf"));

        var writer = Writer(allowMutation: true);
        var path = new LibraryPath("track.dsf");

        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered), Token);
        Assert.Null(plan);

        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Unsupported, result.Status);
    }

    /// <summary>
    /// The caller is handed the new size and timestamp because it must store them.
    /// </summary>
    /// <remarks>
    /// A tag write changes the bytes. A catalogue still holding the old size and
    /// mtime sees the file as modified on the next scan and discards everything
    /// derived from it — over a library-wide run, that is the whole catalogue.
    /// </remarks>
    [Fact]
    public async Task ACommittedWriteReportsTheFilesNewSizeAndTimestamp()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);

        var result = await Write(writer, path);

        Assert.NotNull(result.Committed);

        var onDisk = new FileInfo(Path.Combine(_root, path.Value));

        Assert.Equal(onDisk.Length, result.Committed!.SizeBytes);
        Assert.Equal(new DateTimeOffset(onDisk.LastWriteTimeUtc), result.Committed.LastModifiedUtc);
    }

    /// <summary>
    /// A billing line containing a semicolon is written whole, not resplit.
    /// </summary>
    /// <remarks>
    /// ATL splits and rejoins field values on a separator character, and its
    /// default is <c>';'</c>. An album artist billed
    /// <c>"Dvořák, Ginastera, Sarasate; Hilary Hahn, …"</c> was therefore written
    /// as two values and read back joined by a bare <c>';'</c> — one space short
    /// — which <see cref="TagWriter"/> correctly read as a field it had not meant
    /// to change, and aborted the whole write over. <see cref="TaggingDefaults"/>
    /// is what stops it.
    ///
    /// Not a corner case: MusicBrainz uses <c>"; "</c> as the join phrase that
    /// separates composers from performers, so this refused every classical
    /// release carrying one. Twenty of them in the target library, 489 files,
    /// none of which could be tagged at all — and the failure was total rather
    /// than partial, which is the only reason it was noticed.
    ///
    /// Asserted through ffprobe, like the rest of this class, and compared
    /// <b>ordinally</b> — the entire difference between pass and fail is one
    /// space.
    /// </remarks>
    [Fact]
    public async Task ASemicolonInACreditLineSurvivesTheWrite()
    {
        SkipWithoutTools();

        const string Billed = "Dvořák, Ginastera, Sarasate; Hilary Hahn, Frankfurt Radio Symphony";

        var (writer, path) = Given(Corpus.Flac);

        var plan = await writer.PlanAsync(
            path,
            CatalogueTags.For(Answered with { AlbumArtistCredit = Billed }),
            Token);

        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        Assert.Equal(Billed, Tag(TagsReportedByFfprobe(path), "album_artist"), StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>Kept as a literal so the test asserts the wire value, not the constant.</summary>
    private const string TagWriteServiceEventPrefix = "tagging.catalogue";

    private (TagWriter Writer, LibraryPath Path) Given(string corpusFile, bool allowMutation = true)
    {
        var name = Corpus.CopyInto(_root, corpusFile)[0];
        return (Writer(allowMutation), new LibraryPath(name));
    }

    private TagWriter Writer(bool allowMutation)
    {
        var store = new FileSystemAudioFileStore(_root);

        return new TagWriter(
            store,
            new TagReader(store),
            _events,
            new FixedClock(),
            new TagWriterOptions { AllowFileMutation = allowMutation });
    }

    private async Task<TagWriteResult> Write(TagWriter writer, LibraryPath path)
    {
        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered), Token);
        return await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);
    }

    private static string SourceFor(string extension) => extension switch
    {
        "flac" => Corpus.Flac,
        "mp3" => Corpus.Mp3,
        _ => Corpus.M4a,
    };

    private string Hash(LibraryPath path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, path.Value))));

    /// <summary>SHA-256 of the decoded PCM, so the container may change and the audio may not.</summary>
    private string DecodedAudioHash(LibraryPath path) =>
        RunTool("ffmpeg", ["-v", "error", "-i", Path.Combine(_root, path.Value), "-f", "s16le", "-"], binary: true);

    /// <summary>Every tag ffprobe can see, by the name it sees it under.</summary>
    private Dictionary<string, string> TagsReportedByFfprobe(LibraryPath path, bool stream = false)
    {
        var output = RunTool(
            "ffprobe",
            [
                "-v", "error", "-show_entries", stream ? "stream_tags" : "format_tags",
                "-of", "default=noprint_wrappers=1", Path.Combine(_root, path.Value),
            ],
            binary: false);

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("TAG:", StringComparison.Ordinal)) continue;

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) continue;

            tags[line[4..separator]] = line[(separator + 1)..].Trim();
        }

        return tags;
    }

    private static string RunTool(string tool, string[] arguments, bool binary)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;

        if (binary)
        {
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"{tool} exited {process.ExitCode}: {stderr}");

            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }

        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return stdout;
    }

    private static void SkipWithoutTools()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");
    }

    /// <summary>Records what was appended without needing a database.</summary>
    private sealed class RecordingEventLog : IEventLog
    {
        private readonly List<DomainEvent> _entries = [];

        public IReadOnlyList<DomainEvent> Entries => _entries;

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            _entries.Add(domainEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(
            IReadOnlyCollection<DomainEvent> events,
            CancellationToken cancellationToken = default)
        {
            _entries.AddRange(events);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DomainEvent> ReadSubjectAsync(
            string subjectType,
            string subjectId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in _entries.Where(
                e => e.SubjectType == subjectType && e.SubjectId == subjectId))
            {
                yield return entry;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
