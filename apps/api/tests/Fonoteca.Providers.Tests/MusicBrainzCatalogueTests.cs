using Fonoteca.Fixtures;
using System.Net;
using System.Reflection;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.MusicBrainz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The MusicBrainz adapter, against responses recorded from the live service.
/// </summary>
/// <remarks>
/// The fixtures under <c>Responses/</c> are verbatim WS/2 documents, captured
/// on 2026-08-08 and not tidied. That is the point: they carry the details a
/// hand-written fixture would smooth away — empty strings where a hand-written
/// one would put null, a partial date, twelve releases for one recording, a
/// track whose printed number is a string.
///
/// The parsing under test is <c>MetaBrainz.MusicBrainz</c>'s, not ours. What is
/// ours is the mapping to the catalogue's vocabulary, and the wiring that has
/// to be right for MusicBrainz not to block the address.
/// </remarks>
public sealed class MusicBrainzCatalogueTests : IDisposable
{
    private const string Contact = "https://example.invalid/fonoteca";

    private static readonly Mbid RecordingId =
        new(Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff"));

    private static readonly Mbid ReleaseId =
        new(Guid.Parse("db85c244-53e7-441c-bab0-52c9c0d27450"));

    private static readonly Mbid SymphonyRecordingId =
        new(Guid.Parse("85db2cdf-80c9-4aa2-9789-19328dde47ed"));

    private static readonly Mbid SymphonyWorkId =
        new(Guid.Parse("70729fa3-654b-4a0b-85ec-5c0ba3b3fb80"));

    private static readonly Mbid CollaborationId =
        new(Guid.Parse("b161074b-1b43-4559-bd6f-0a106a2c7547"));

    /// <summary>
    /// The recording that proved the lookup's cap: 25 releases from
    /// <c>inc=releases</c>, 40 from a browse of the same recording.
    /// </summary>
    private static readonly Mbid JukeboxRecordingId =
        new(Guid.Parse("c0035654-9b35-482d-ae42-5b3455c9b661"));

    /// <summary>A person MusicBrainz knows the end of. Genres arrive alphabetically.</summary>
    private static readonly Mbid PettyId =
        new(Guid.Parse("5ca3f318-d028-4151-ac73-78e2b2d6cdcc"));

    /// <summary>
    /// A Russian composer, whose 94 aliases are the whole of the Latin-name rule.
    /// </summary>
    private static readonly Mbid TchaikovskyId =
        new(Guid.Parse("9ddd7abc-9e1b-471d-8031-583bc6bc8be9"));

    /// <summary>A person MusicBrainz does not know the end of, because there is not one.</summary>
    private static readonly Mbid WinwoodId =
        new(Guid.Parse("885f90ef-6bd9-409a-b2df-e165e553c68e"));

    /// <summary>A person, whose <c>member of band</c> relations all point outwards.</summary>
    private static readonly Mbid KnopflerId =
        new(Guid.Parse("e49f69da-17d5-4c5c-bac0-dadcb0e588f5"));

    /// <summary>The band, whose relations are the identical facts pointing inwards.</summary>
    private static readonly Mbid DireStraitsId =
        new(Guid.Parse("614e3804-7d34-41ba-857f-811bad7c2b7a"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The rule with teeth. MusicBrainz blocks clients that do not identify
    /// themselves, and the requests are composed inside MetaBrainz.MusicBrainz
    /// where no code of ours can add a header — so it goes on the HttpClient,
    /// and this is what proves it survived the trip.
    /// </summary>
    [Fact]
    public async Task EveryRequestIdentifiesTheApplicationAndAContact()
    {
        var (catalogue, stub) = Build(Recorded("recording-lower-your-eyelids.json"));

        await catalogue.GetRecordingAsync(RecordingId, Token);

        var userAgent = Assert.Single(stub.Requests).UserAgent;

        Assert.NotNull(userAgent);
        Assert.Contains("Fonoteca/", userAgent, StringComparison.Ordinal);
        Assert.Contains(Contact, userAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordingLookupAsksForEverythingOneRoundTripCanCarry()
    {
        var (catalogue, stub) = Build(Recorded("recording-lower-your-eyelids.json"));

        await catalogue.GetRecordingAsync(RecordingId, Token);

        var request = Assert.Single(stub.Requests);

        Assert.Contains(RecordingId.Value.ToString(), request.Uri.AbsolutePath, StringComparison.Ordinal);

        // At one request per second, splitting these into four lookups turns
        // identifying one track into four seconds.
        var query = request.Uri.Query;
        Assert.Contains("artists", query, StringComparison.Ordinal);
        Assert.Contains("releases", query, StringComparison.Ordinal);
        Assert.Contains("release-groups", query, StringComparison.Ordinal);
        Assert.Contains("media", query, StringComparison.Ordinal);
        Assert.Contains("isrcs", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordingIsMappedToTheCataloguesVocabulary()
    {
        var (catalogue, _) = Build(Recorded("recording-lower-your-eyelids.json"));

        var recording = await catalogue.GetRecordingAsync(RecordingId, Token);

        Assert.NotNull(recording);
        Assert.Equal(RecordingId, recording.Id);
        Assert.Equal("Lower Your Eyelids to Die With the Sun", recording.Title);
        Assert.Equal(TimeSpan.FromMilliseconds(637_333), recording.Length);

        // MusicBrainz sends "" for an absent disambiguation, which is not the
        // same shape as absent and would leak an empty string into the catalogue.
        Assert.Null(recording.Disambiguation);

        var credit = Assert.Single(recording.Credits);
        Assert.Equal("M83", credit.Name);
        Assert.Equal("Group", credit.ArtistType);
        Assert.Equal(new Mbid(Guid.Parse("6d7b7cd4-254b-4c25-83f6-dd20f98ceacd")), credit.ArtistId);

        // The last credit in a list has an empty join phrase, not a null one.
        Assert.Null(credit.JoinPhrase);
    }

    [Fact]
    public async Task EveryReleaseTheRecordingAppearsOnIsCarriedWithItsPosition()
    {
        var (catalogue, _) = Build(Recorded("recording-lower-your-eyelids.json"));

        var recording = await catalogue.GetRecordingAsync(RecordingId, Token);

        Assert.NotNull(recording);

        // Twelve editions of one album. Choosing between them is a later
        // question; losing eleven of them here would make it unanswerable.
        Assert.Equal(12, recording.Appearances.Count);

        var appearance = Assert.Single(recording.Appearances, a => a.ReleaseId == ReleaseId);

        Assert.Equal("Before the Dawn Heals Us", appearance.ReleaseTitle);
        Assert.Equal("FR", appearance.Country);
        Assert.Equal("Official", appearance.Status);
        Assert.Equal("Album", appearance.PrimaryType);
        Assert.Equal(1, appearance.DiscNumber);
        Assert.Equal(15, appearance.TrackPosition);
        Assert.Equal("15", appearance.TrackNumber);

        // The number an incomplete rip is measured against.
        Assert.Equal(15, appearance.TrackCount);
    }

    /// <summary>
    /// A release MusicBrainz only dates to a year must not come back dated to
    /// the 1st of January — the difference decides which edition sorts first.
    /// </summary>
    [Fact]
    public async Task PartialDatesStayPartial()
    {
        var (catalogue, _) = Build(Recorded("recording-lower-your-eyelids.json"));

        var recording = await catalogue.GetRecordingAsync(RecordingId, Token);

        Assert.NotNull(recording);

        var complete = Assert.Single(recording.Appearances, a => a.ReleaseId == ReleaseId);

        Assert.Equal(new ReleaseDate(2005, 1, 24), complete.ReleasedOn);
        Assert.Equal(new DateOnly(2005, 1, 24), complete.ReleasedOn?.ToDateOnly());

        // And a year-only date resolves to nothing rather than to a guess.
        Assert.Null(new ReleaseDate(1969, null, null).ToDateOnly());
        Assert.Equal("1969", new ReleaseDate(1969, null, null).ToString());
    }

    [Fact]
    public async Task AReleaseCarriesItsWholeTrackListInOrder()
    {
        var (catalogue, _) = Build(Recorded("release-before-the-dawn-heals-us.json"));

        var release = await catalogue.GetReleaseAsync(ReleaseId, Token);

        Assert.NotNull(release);
        Assert.Equal("Before the Dawn Heals Us", release.Title);
        Assert.Equal("724356386228", release.Barcode);
        Assert.Equal("Album", release.PrimaryType);

        var label = Assert.Single(release.Labels);
        Assert.Equal("Gooom", label.Name);
        Assert.Equal("Gooom035CD", label.CatalogNumber);

        Assert.Equal(15, release.Tracks.Count);
        Assert.Equal([.. Enumerable.Range(1, 15)], release.Tracks.Select(t => t.Position));
        Assert.All(release.Tracks, track => Assert.Equal(1, track.DiscNumber));

        var last = release.Tracks[^1];
        Assert.Equal("Lower Your Eyelids to Die With the Sun", last.Title);
        Assert.Equal(RecordingId, last.RecordingId);
    }

    /// <summary>
    /// The case the relationship includes exist for. MusicBrainz bills this
    /// recording to nobody who played it, and without <c>artist-rels</c> the
    /// conductor and the orchestra are simply absent from the response.
    /// </summary>
    [Fact]
    public async Task AClassicalRecordingCarriesItsConductorItsOrchestraAndItsWork()
    {
        var (catalogue, stub) = Build(Recorded("recording-symphony-no-40.json"));

        var recording = await catalogue.GetRecordingAsync(SymphonyRecordingId, Token);

        Assert.NotNull(recording);

        var query = Assert.Single(stub.Requests).Uri.Query;
        Assert.Contains("artist-rels", query, StringComparison.Ordinal);
        Assert.Contains("work-rels", query, StringComparison.Ordinal);

        var conductor = Assert.Single(recording.Relations, r => r.Type == "conductor");
        Assert.Equal("Anzor Kinkladze", conductor.Name);
        Assert.Equal("Person", conductor.ArtistType);

        var orchestra = Assert.Single(recording.Relations, r => r.Type == "performing orchestra");
        Assert.Equal("Georgian SIMI Festival Orchestra", orchestra.Name);

        // The work arrives as a stub — enough to look it up, not enough to name
        // the composer, which is why GetWorkAsync exists.
        Assert.Equal(SymphonyWorkId, recording.WorkId);
        Assert.Equal(
            "Symphony no. 40 in G minor, K. 550 “Great”: I. Allegro molto",
            recording.WorkTitle);
    }

    /// <summary>
    /// A relationship whose target is not an artist has nothing the catalogue
    /// can store, and the work link is read on its own.
    /// </summary>
    [Fact]
    public async Task RelationsToAnythingButAnArtistAreDropped()
    {
        var (catalogue, _) = Build(Recorded("recording-symphony-no-40.json"));

        var recording = await catalogue.GetRecordingAsync(SymphonyRecordingId, Token);

        Assert.NotNull(recording);

        // The response carries three relationships; the third is the performance
        // link to the work.
        Assert.Equal(2, recording.Relations.Count);
        Assert.DoesNotContain(recording.Relations, r => r.Type == "performance");
        Assert.All(recording.Relations, r => Assert.NotNull(r.ArtistId));
    }

    /// <summary>
    /// Everything a credit line cannot carry, off an artist document.
    /// </summary>
    /// <remarks>
    /// <b>The genre order is the assertion worth having.</b> MusicBrainz sends
    /// them alphabetically — <c>heartland rock, pop rock, rock, southern
    /// rock</c> — with vote counts <c>1, 1, 3, 1</c>, so a mapper that passed
    /// them through unchanged and one that sorts by votes differ on this exact
    /// document, and the screen only ever prints the first three.
    ///
    /// The dates are the other half: MusicBrainz knows this artist's birthday to
    /// the day and the catalogue deliberately keeps the year, so a mapper that
    /// widened or narrowed differently would show here.
    /// </remarks>
    [Fact]
    public async Task AnArtistCarriesTheirCountryLifeSpanAndGenresMostVotedFirst()
    {
        var (catalogue, stub) = Build(Recorded("artist-tom-petty.json"));

        var artist = await catalogue.GetArtistAsync(PettyId, Token);

        Assert.NotNull(artist);
        Assert.Equal(PettyId, artist.Id);
        Assert.Equal("Tom Petty", artist.Name);
        Assert.Equal("Petty, Tom", artist.SortName);
        Assert.Equal("Person", artist.Type);
        Assert.Equal("US", artist.Country);
        Assert.Equal("Male", artist.Gender);

        // 1950-10-20 and 2017-10-02 on the wire; the year is what is kept.
        Assert.Equal(1950, artist.BeganYear);
        Assert.Equal(2017, artist.EndedYear);
        Assert.True(artist.HasEnded);

        Assert.Equal(
            ["rock", "heartland rock", "pop rock", "southern rock"],
            artist.Genres);

        var request = Assert.Single(stub.Requests);
        Assert.Contains("/artist/", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("genres", request.Uri.Query, StringComparison.Ordinal);

        // Not the discography. Those are thousands of rows, paginated, and the
        // catalogue already knows which of them the library holds.
        Assert.DoesNotContain("releases", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("recordings", request.Uri.Query, StringComparison.Ordinal);

        // Tags are the free-text list, where "seen live" outvotes the music.
        Assert.DoesNotContain("tags", request.Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A living artist has a beginning and no end, and the two facts that say so
    /// are separate on the wire.
    /// </summary>
    /// <remarks>
    /// <c>"end": null</c> beside <c>"ended": false</c>. A mapper reading either
    /// one alone is right about this document and wrong about the other case —
    /// a group known to have split that nobody has dated — so both are carried.
    /// </remarks>
    [Fact]
    public async Task ALivingArtistHasNoEndYearAndIsNotMarkedEnded()
    {
        var (catalogue, _) = Build(Recorded("artist-steve-winwood.json"));

        var artist = await catalogue.GetArtistAsync(WinwoodId, Token);

        Assert.NotNull(artist);
        Assert.Equal(1948, artist.BeganYear);
        Assert.Null(artist.EndedYear);
        Assert.False(artist.HasEnded);

        // Seven genres, four distinct vote counts, and "pop" at 4 leads them.
        Assert.Equal("pop", artist.Genres[0]);
        Assert.Equal(7, artist.Genres.Count);
    }

    /// <summary>
    /// An artist MusicBrainz has merged away is an answer, not a failure — the
    /// same arm every other lookup here takes, and what lets the pass stamp the
    /// row and stop asking.
    /// </summary>
    [Fact]
    public async Task AnArtistThatHasBeenMergedAwayComesBackNull()
    {
        var (catalogue, _) = Build(_ => StubHttpHandler.Json(HttpStatusCode.NotFound, "{}"));

        Assert.Null(await catalogue.GetArtistAsync(PettyId, Token));
    }

    [Fact]
    public async Task AWorkNamesWhoWroteIt()
    {
        var (catalogue, stub) = Build(Recorded("work-symphony-no-40.json"));

        var work = await catalogue.GetWorkAsync(SymphonyWorkId, Token);

        Assert.NotNull(work);
        Assert.Equal(SymphonyWorkId, work.Id);

        var request = Assert.Single(stub.Requests);
        Assert.Contains("/work/", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("artist-rels", request.Uri.Query, StringComparison.Ordinal);

        // Not recordings: a popular work has thousands and they arrive paginated.
        Assert.DoesNotContain("recordings", request.Uri.Query, StringComparison.Ordinal);

        var composer = Assert.Single(work.Relations, r => r.Type == "composer");
        Assert.Equal("Wolfgang Amadeus Mozart", composer.Name);
        Assert.Equal("Person", composer.ArtistType);
    }

    /// <summary>
    /// The other case the credit line gets right and a relationship-only reading
    /// would lose: two artists on the sleeve, with the word between them.
    /// </summary>
    [Fact]
    public async Task ACollaborationKeepsBothCreditsAndTheJoinPhrase()
    {
        var (catalogue, _) = Build(Recorded("recording-nutbush-city-limits.json"));

        var recording = await catalogue.GetRecordingAsync(CollaborationId, Token);

        Assert.NotNull(recording);
        Assert.Equal(["Beth Hart", "Joe Bonamassa"], recording.Credits.Select(c => c.Name));

        // MusicBrainz sends "" for the last join phrase in a list.
        Assert.Equal(" & ", recording.Credits[0].JoinPhrase);
        Assert.Null(recording.Credits[1].JoinPhrase);

        // Kept, and it is the domain rule's job to decide it is not a credit.
        Assert.Single(recording.Relations, r => r.Type == "mix");
    }

    /// <summary>
    /// An MBID that AcoustID still points at can have been merged away hours
    /// ago. That is an answer, not a failure — a batch of 100,000 files cannot
    /// treat it as one.
    /// </summary>
    [Fact]
    public async Task AnUnknownIdentifierIsNullRatherThanAnException()
    {
        var (catalogue, _) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.NotFound,
            """{"error":"Not Found","help":"For usage, please see: https://musicbrainz.org/development/mmd"}"""));

        Assert.Null(await catalogue.GetRecordingAsync(RecordingId, Token));
    }

    [Fact]
    public async Task ARateLimitedResponseIsReportedAsUnavailable()
    {
        var (catalogue, stub) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.ServiceUnavailable,
            """{"error":"Your requests are exceeding the allowable rate limit."}"""));

        var failure = await Assert.ThrowsAsync<ProviderUnavailableException>(
            async () => await catalogue.GetRecordingAsync(RecordingId, Token));

        Assert.Equal("MusicBrainz", failure.Provider);

        // Retried, because a rate limit is the most likely thing this ever is.
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task WithoutAContactNothingIsSent()
    {
        var (catalogue, stub) = Build(
            Recorded("recording-lower-your-eyelids.json"),
            options => options.Contact = string.Empty);

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await catalogue.GetRecordingAsync(RecordingId, Token));

        Assert.Contains("Fonoteca:MusicBrainzContact", failure.Message, StringComparison.Ordinal);

        // The whole point: an unidentified request works once per address.
        Assert.Empty(stub.Requests);
    }

    /// <summary>
    /// A browse is followed to its end, not to the end of its first page.
    /// </summary>
    /// <remarks>
    /// The fixtures are two consecutive pages of a real browse, both announcing
    /// <c>release-count: 40</c> while carrying three releases each. Stopping at
    /// the first would look entirely successful and quietly hide 37 of the
    /// candidate albums a file might have come from.
    /// </remarks>
    /// <summary>
    /// A followed artist's discography is paged to the end, and mapped whole.
    /// </summary>
    /// <remarks>
    /// The same trap as the release browse below, one entity along: a prolific
    /// artist has more release groups than a page holds, and stopping at the
    /// first would report a discography that quietly ends in the 1980s — which
    /// reads as "you own everything since" rather than as a bug.
    ///
    /// <b>The documents here are synthetic, and that is a deviation worth
    /// naming.</b> Every other MusicBrainz fixture in this project is a verbatim
    /// recording under <c>Responses/</c>, for the reason that file's remarks
    /// give. These are inline because musicbrainz.org answered
    /// <c>"The MusicBrainz web server is currently busy"</c> to every attempt to
    /// record one and there is no mirror to fall back on — and a hand-written
    /// file placed among the recorded ones would be a lie about its provenance.
    /// The shape is WS/2's own; replacing it with a real capture is worth doing
    /// the next time the server answers.
    ///
    /// What it pins beyond the paging: the year is narrowed from a full date,
    /// a year-only date survives, an absent date is null rather than a guess,
    /// secondary types are carried through in MusicBrainz's own spelling
    /// because <c>Discography.IsGap</c> is written against those exact strings,
    /// and an untyped group stays untyped.
    /// </remarks>
    [Fact]
    public async Task ADiscographyBrowseIsPagedToTheEndAndMappedWhole()
    {
        var artist = new Mbid(Guid.Parse("58e325d5-54fd-4e98-b39a-3aa6bc319273"));

        var page1 = """
            {"release-group-count":6,"release-group-offset":0,"release-groups":[
              {"id":"11111111-1111-1111-1111-111111111111","title":"Dire Straits",
               "primary-type":"Album","secondary-types":[],
               "first-release-date":"1978-10-07","disambiguation":""},
              {"id":"22222222-2222-2222-2222-222222222222","title":"Money for Nothing",
               "primary-type":"Album","secondary-types":["Compilation"],
               "first-release-date":"1988","disambiguation":""}
            ]}
            """;

        var page2 = """
            {"release-group-count":6,"release-group-offset":2,"release-groups":[
              {"id":"33333333-3333-3333-3333-333333333333","title":"ExtendedancEPlay",
               "primary-type":"EP","secondary-types":[],
               "first-release-date":"1983-01","disambiguation":""},
              {"id":"44444444-4444-4444-4444-444444444444","title":"Something Unreleased",
               "primary-type":null,"secondary-types":[],
               "first-release-date":"","disambiguation":""}
            ]}
            """;

        // Both pages announce six and carry two, so the offset never reaches the
        // claimed total and it is the empty page that ends the loop — which is
        // the guard worth testing. Counts that agreed with their contents would
        // terminate on arithmetic alone and never exercise it.
        var empty = """{"release-group-count":6,"release-group-offset":4,"release-groups":[]}""";
        var served = 0;

        var (catalogue, stub) = Build(_ =>
        {
            var index = served++;

            return StubHttpHandler.Json(
                HttpStatusCode.OK,
                index switch { 0 => page1, 1 => page2, _ => empty });
        });

        var browse = await catalogue.BrowseReleaseGroupsForArtistAsync(artist, Token);
        var groups = browse.Groups;

        Assert.Equal(4, groups.Count);

        // Six announced, four delivered: what arrived is kept and the cut is said.
        Assert.False(browse.Complete);
        Assert.Equal(3, stub.Requests.Count);

        // Offsets advance by what arrived. MetaBrainz omits an offset of zero.
        Assert.DoesNotContain("offset=", stub.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=2", stub.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=4", stub.Requests[2].Uri.Query, StringComparison.Ordinal);

        // Include.None, so nothing is asked for beyond the browse itself — the
        // whole reason this call is cheap enough to make per followed artist.
        Assert.DoesNotContain("inc=", stub.Requests[0].Uri.Query, StringComparison.Ordinal);

        Assert.Equal(
            ["Dire Straits", "Money for Nothing", "ExtendedancEPlay", "Something Unreleased"],
            groups.Select(group => group.Title));

        // A full date narrows to its year; a year-only date survives; a partial
        // month keeps the year; an absent date is null and not a guess.
        Assert.Equal([1978, 1988, 1983, null], groups.Select(group => group.FirstReleaseYear));

        // MusicBrainz's own spellings, because the gap rule matches on them.
        Assert.Equal(["Compilation"], groups[1].SecondaryTypes);
        Assert.Empty(groups[0].SecondaryTypes);

        Assert.Equal("EP", groups[2].PrimaryType);
        Assert.Null(groups[3].PrimaryType);
    }

    /// <summary>
    /// A browse that delivered what it counted is complete, which is what licenses
    /// the enrichment pass to read a missing group as one MusicBrainz dropped.
    /// </summary>
    [Fact]
    public async Task ADiscographyBrowseThatDeliversItsCountIsComplete()
    {
        var page = """
            {"release-group-count":1,"release-group-offset":0,"release-groups":[
              {"id":"11111111-1111-1111-1111-111111111111","title":"Carencro",
               "primary-type":"Album","secondary-types":[],
               "first-release-date":"2004-08-03","disambiguation":""}
            ]}
            """;

        var (catalogue, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, page));

        var browse = await catalogue.BrowseReleaseGroupsForArtistAsync(
            new Mbid(Guid.Parse("58e325d5-54fd-4e98-b39a-3aa6bc319273")),
            Token);

        Assert.True(browse.Complete);
        Assert.Equal(["Carencro"], browse.Groups.Select(group => group.Title));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ABrowseIsPagedToTheEndRatherThanTrustedAtItsFirstPage()
    {
        var pages = new[]
        {
            ReadFixture("browse-releases-page1.json"),
            ReadFixture("browse-releases-page2.json"),
        };

        // The second page is served twice: MusicBrainz answers an offset past
        // the end with an empty list, which is what actually terminates a browse
        // whose count and contents disagree.
        var empty = """{"release-count":40,"release-offset":6,"releases":[]}""";
        var served = 0;

        var (catalogue, stub) = Build(_ =>
        {
            var index = served++;
            return StubHttpHandler.Json(
                HttpStatusCode.OK,
                index < pages.Length ? pages[index] : empty);
        });

        var candidates = await catalogue.BrowseReleasesForRecordingAsync(JukeboxRecordingId, Token);

        Assert.Equal(6, candidates.Count);
        Assert.Equal(3, stub.Requests.Count);

        // Offsets advance by what arrived, not by what was asked for. The first
        // page carries none: MetaBrainz omits an offset of zero.
        Assert.DoesNotContain("offset=", stub.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=3", stub.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=6", stub.Requests[2].Uri.Query, StringComparison.Ordinal);

        // Both pages are present, in order, with no page boundary visible.
        Assert.Equal(
            ["Don't Rock the Jukebox", "The Ultimate Country Collection", "Picked to Click"],
            candidates.Select(c => c.Title).Distinct().Select(t => t.Split(':')[0]).ToArray());
    }

    /// <summary>
    /// The counts a shortlist is built from survive the mapping.
    /// </summary>
    /// <remarks>
    /// A candidate's whole job is to be rejected cheaply. Coverage cannot exceed
    /// the share of a release's tracks that the library already holds, so the
    /// track count is what lets a forty-track compilation contributing one song
    /// be ruled out without ever fetching its track list — and a zero here would
    /// silently rule out everything instead.
    /// </remarks>
    [Fact]
    public async Task ACandidateCarriesTheTrackCountsThatRuleItOut()
    {
        // One page then nothing: `Recorded` would serve the same page at every
        // offset, and a browse that pages to exhaustion would read it fourteen
        // times over.
        var (catalogue, _) = Build(Once(ReadFixture("browse-releases-page2.json")));

        var candidates = await catalogue.BrowseReleasesForRecordingAsync(JukeboxRecordingId, Token);

        var compilation = candidates.Single(c => c.Title == "The Ultimate Country Collection");

        Assert.Equal(40, compilation.TrackCount);
        Assert.Equal(2, compilation.Media.Count);
        Assert.All(compilation.Media, medium => Assert.Equal("CD", medium.Format));
        Assert.Equal([1, 2], compilation.Media.Select(m => m.Position).ToArray());

        // Status and date are the stated tie-break between editions that fit
        // equally well, so a promotional pressing has to be distinguishable.
        var promotion = candidates.Single(c => c.Status == "Promotion");
        Assert.Equal(1991, promotion.ReleasedOn?.Year);
        Assert.Null(promotion.ReleasedOn?.Month);
    }

    /// <summary>
    /// Configuration that would get the address blocked is refused rather than
    /// obeyed. The public instance is the only server this applies to.
    /// </summary>
    [Fact]
    public void TheOfficialServerIsRecognisedRegardlessOfCase()
    {
        Assert.True(new MusicBrainzOptions().IsOfficialServer);
        Assert.True(new MusicBrainzOptions { Server = new Uri("https://MusicBrainz.org") }.IsOfficialServer);
        Assert.False(new MusicBrainzOptions { Server = new Uri("http://mirror.lan:5000") }.IsOfficialServer);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>
    /// Serves one page and then an empty one, which is how a browse ends.
    /// </summary>
    /// <remarks>
    /// <see cref="Recorded"/> answers every request with the same body, and a
    /// browse reads until the pages run out — so a recorded page whose
    /// <c>release-count</c> exceeds its contents would be served over and over
    /// until the offset caught up with the count.
    /// </remarks>
    private static Func<RecordedRequest, HttpResponseMessage> Once(string page)
    {
        var served = 0;
        return _ => StubHttpHandler.Json(
            HttpStatusCode.OK,
            served++ == 0 ? page : """{"release-count":40,"release-offset":3,"releases":[]}""");
    }

    private static Func<RecordedRequest, HttpResponseMessage> Recorded(string fixture)
    {
        var body = ReadFixture(fixture);
        return _ => StubHttpHandler.Json(HttpStatusCode.OK, body);
    }

    private static string ReadFixture(string name)
    {
        var assembly = typeof(MusicBrainzCatalogueTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Missing embedded fixture '{resource}'. Available: "
                + string.Join(", ", assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (IMusicBrainzCatalogue Catalogue, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<MusicBrainzOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddMusicBrainz(options =>
        {
            options.Contact = Contact;
            // The real interval is a second. Tests assert that requests are
            // gated, not that they are gated for exactly that long.
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
            configure?.Invoke(options);
        });

        services.AddHttpClient(MusicBrainzOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IMusicBrainzCatalogue>(), stub);
    }
    /// <summary>
    /// A person's bands come back; the same relation read on the band does not.
    /// </summary>
    /// <remarks>
    /// <b>The direction is the whole of <c>ToBands</c>, and these two documents
    /// are what tell a mapper that reads it from one that does not.</b>
    /// MusicBrainz states <c>member of band</c> once and serves it from both
    /// artists: Mark Knopfler's document carries six, every one <c>forward</c>
    /// with the group on the far end, and Dire Straits' carries nine, every one
    /// <c>backward</c> with a member there instead.
    ///
    /// Read without checking, the band's own document reports its nine members
    /// as nine groups Dire Straits belongs to — which would put every band's
    /// albums onto its members' shelves and none onto its own, silently, on a
    /// page that looks populated either way.
    ///
    /// Verbatim WS/2 documents, fetched with the includes the application asks
    /// for. Note what else they pin: the person's list contains the band this
    /// library actually needs, so a mapper that dropped forward relations by
    /// mistake fails here rather than at the shelf.
    /// </remarks>
    [Fact]
    public async Task OnlyTheMemberSideOfABandRelationIsRead()
    {
        var (person, _) = Build(Recorded("artist-mark-knopfler.json"));
        var knopfler = await person.GetArtistAsync(KnopflerId, Token);

        Assert.NotNull(knopfler);
        Assert.Contains(DireStraitsId, knopfler.Bands);

        // Six on the wire, all forward, none of them himself.
        Assert.Equal(6, knopfler.Bands.Count);
        Assert.DoesNotContain(KnopflerId, knopfler.Bands);

        var (group, _) = Build(Recorded("artist-dire-straits.json"));
        var band = await group.GetArtistAsync(DireStraitsId, Token);

        Assert.NotNull(band);

        // Nine `member of band` relations in that document and not one of them
        // is a band Dire Straits belongs to.
        Assert.Empty(band.Bands);
    }

    /// <summary>The membership relations are asked for, and nothing heavier is.</summary>
    /// <remarks>
    /// The include is what makes the whole feature possible at no extra request,
    /// and it is one word in a const two files away from the shelf that needs
    /// it. Pinned beside the exclusions the artist lookup already keeps, so
    /// dropping it fails a test rather than quietly emptying every band shelf on
    /// the next enrichment run.
    /// </remarks>
    /// <summary>
    /// The aliases a Latin display name is chosen from, off a real document.
    /// </summary>
    /// <remarks>
    /// <b>The stub bypasses the mapper, which is why this exists</b> — the same
    /// reason <c>artist-tom-petty.json</c> does. Every other test of the Latin
    /// name rule hands <c>LatinNames.Of</c> a hand-built list, so nothing
    /// exercised <c>ToAliases</c> or proved the three fields it reads survive
    /// the wire at all.
    ///
    /// This document earns its place three times over. It carries <b>94</b>
    /// aliases where the fixtures beside it carry none; its Russian alias is
    /// <c>primary="true"</c>, so a rule ranking on the flag before the script
    /// answers <c>Пётр Чайковский</c>; and it files <c>Chaikovsky</c> as a
    /// <c>Search hint</c>, which sorts before the right answer ordinally and is
    /// what the last rung would otherwise reach for.
    /// </remarks>
    [Fact]
    public async Task AnArtistsAliasesSurviveTheWireAndNameTheLatinOne()
    {
        var (catalogue, _) = Build(Recorded("artist-tchaikovsky.json"));

        var artist = await catalogue.GetArtistAsync(TchaikovskyId, Token);

        Assert.NotNull(artist);
        Assert.Equal("Пётр Ильич Чайковский", artist.Name);
        Assert.NotEmpty(artist.Aliases);

        // All three fields, because the rule ranks on all three.
        var chosen = Assert.Single(
            artist.Aliases.Where(a => a.Name == "Pyotr Ilyich Tchaikovsky"));

        Assert.True(chosen.IsEnglish);
        Assert.True(chosen.Primary);
        Assert.True(chosen.IsName);

        Assert.Contains(artist.Aliases, a => a.Name == "Пётр Чайковский" && a.Primary);
        Assert.Contains(artist.Aliases, a => a.Name == "Chaikovsky" && !a.IsName);

        Assert.Equal(
            "Pyotr Ilyich Tchaikovsky",
            LatinNames.Of(artist.Name, artist.Aliases));
    }

    [Fact]
    public async Task TheArtistLookupAsksForBandMembershipAndStillNotForADiscography()
    {
        var (catalogue, stub) = Build(Recorded("artist-mark-knopfler.json"));

        await catalogue.GetArtistAsync(KnopflerId, Token);

        var request = Assert.Single(stub.Requests);

        Assert.Contains("artist-rels", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("genres", request.Uri.Query, StringComparison.Ordinal);

        // The third rider, and the one a browse list's Latin names depend on.
        // Dropped, every non-Latin artist keeps their own name and nothing
        // fails — the column simply stays null and no screen says why.
        Assert.Contains("aliases", request.Uri.Query, StringComparison.Ordinal);

        Assert.DoesNotContain("releases", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("recordings", request.Uri.Query, StringComparison.Ordinal);
    }

}
