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
}
