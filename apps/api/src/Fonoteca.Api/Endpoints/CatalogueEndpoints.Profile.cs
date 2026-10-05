using System.Globalization;
using System.Text.RegularExpressions;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * A person's corrections to an artist or an album, and the reading side that
 * lays them over the providers' answers.
 *
 * Rule 4 throughout: the row's columns stay what MusicBrainz and Wikipedia
 * said, and the corrections live in `EditsJson` beside them — see
 * `PersonEdits`. Both pages send their whole form; only what differs from the
 * provider is stored, so putting a value back undoes the edit.
 *
 * What reads them: the artist and album pages, the artist list and the album
 * list. What does not: the tag writer, the credit lines on other pages and the
 * list's sort and filter, which run in SQL over the provider's columns.
 */
public static partial class CatalogueEndpoints
{
    private const int MaxBiographyLength = 50_000;

    private static void MapProfileEndpoints(IEndpointRouteBuilder group)
    {
        group.MapPost("/artists/{id:guid}/edits", EditArtist)
            .WithName("EditArtist")
            .WithSummary("Correct what the catalogue says about an artist.")
            .WithDescription(
                "The whole form, every time. A field equal to what the providers hold is not "
                + "an edit, so sending the provider's value back undoes one. Stored beside the "
                + "providers' answers rather than over them, so no enrichment pass replaces it. "
                + "The tag writer does not read these.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/releases/{id:guid}/edits", EditRelease)
            .WithName("EditRelease")
            .WithSummary("Correct what the catalogue says about an album.")
            .WithDescription(
                "The whole form, every time, as for an artist. The album-level fields — type, "
                + "first release year, review — are kept on this edition, so another edition of "
                + "the same album does not show them.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Ok<EditResponse>, ProblemHttpResult>> EditArtist(
        Guid id,
        ArtistEditRequest request,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var problem = request is null ? "The request body is the whole form." : Invalid(request);

        if (problem is not null || request is null)
        {
            return TypedResults.Problem(
                title: "Not a valid artist",
                detail: problem ?? "The request body is the whole form.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var artistId = new ArtistId(id);

        var artist = await db.Artists
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken)
            .ConfigureAwait(false);

        if (artist is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail: $"The catalogue has no artist with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["name"] = request.Name,
            ["latinName"] = request.LatinName,
            ["sortName"] = request.SortName,
            ["disambiguation"] = request.Disambiguation,
            ["type"] = request.Type,
            ["country"] = request.Country?.Trim().ToUpperInvariant(),
            ["gender"] = request.Gender,
            ["beganYear"] = Number(request.BeganYear),
            ["endedYear"] = Number(request.EndedYear),
            ["ended"] = Flag(request.Ended),
            ["genres"] = Joined(request.Genres),
            ["biography"] = request.Biography,
            ["portrait"] = request.Portrait,
            ["banner"] = request.Banner,
        };

        var edits = PersonEdits.Diff(wanted, ArtistFields(artist));

        artist.EditsJson = PersonEdits.Write(edits);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new EditResponse([.. edits.Keys.Order(StringComparer.Ordinal)]));
    }

    /// <summary>The fields of an album's form that belong to one pressing of it.</summary>
    private static readonly string[] PressingFields =
    [
        "disambiguation", "releasedYear", "releasedMonth", "releasedDay", "country",
        "status", "label", "catalogNumber", "barcode", "formats",
    ];

    /// <summary>The fields of an album's form that belong to the album, stored on its group.</summary>
    internal static readonly string[] AlbumFields =
    [
        "title", "credit", "primaryType", "secondaryTypes", "firstReleaseYear", "review",
    ];

    internal static async Task<Results<Ok<EditResponse>, ProblemHttpResult>> EditRelease(
        Guid id,
        ReleaseEditRequest request,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var problem = request is null ? "The request body is the whole form." : Invalid(request);

        if (problem is not null || request is null)
        {
            return TypedResults.Problem(
                title: "Not a valid album",
                detail: problem ?? "The request body is the whole form.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var releaseId = new ReleaseId(id);

        var release = await db.Releases
            .Include(r => r.ReleaseGroup)
            .FirstOrDefaultAsync(r => r.Id == releaseId, cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail: $"The catalogue has no release with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var credit = await ReleaseCreditLineAsync(db, releaseId, cancellationToken).ConfigureAwait(false);

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["title"] = request.Title,
            ["credit"] = request.Credit,
            ["disambiguation"] = request.Disambiguation,
            ["primaryType"] = request.PrimaryType,
            ["secondaryTypes"] = Joined(request.SecondaryTypes),
            ["firstReleaseYear"] = Number(request.FirstReleaseYear),
            ["releasedYear"] = Number(request.ReleasedYear),
            ["releasedMonth"] = Number(request.ReleasedYear is null ? null : request.ReleasedMonth),
            ["releasedDay"] = Number(
                request.ReleasedYear is null || request.ReleasedMonth is null ? null : request.ReleasedDay),
            ["country"] = request.Country?.Trim().ToUpperInvariant(),
            ["status"] = request.Status,
            ["label"] = request.Label,
            ["catalogNumber"] = request.CatalogNumber,
            ["barcode"] = request.Barcode,
            ["formats"] = request.Formats,
            ["review"] = request.Review,
        };

        var provider = ReleaseFields(release, credit);

        // A pressing's facts are corrected only through the pressing the album
        // is claimed to be — every one of its files under it, the album page's
        // own rule. Through a display edition they keep what they were: the page
        // cannot show them there, and a blank from it would be a deletion nobody
        // asked for.
        var claimed = await db.MediaFiles.AnyAsync(f => f.ReleaseId == releaseId, cancellationToken).ConfigureAwait(false)
            && !await db.MediaFiles
                .AnyAsync(f => f.ReleaseGroupId == release.ReleaseGroupId && f.ReleaseId != releaseId, cancellationToken)
                .ConfigureAwait(false);

        if (!claimed)
        {
            var kept = PersonEdits.Read(release.EditsJson);

            foreach (var field in PressingFields)
            {
                wanted[field] = PersonEdits.Apply(kept, field, provider.GetValueOrDefault(field));
            }
        }

        var edits = PersonEdits.Diff(wanted, provider);

        // The album's own fields on the album, so they stay put when the page
        // starts showing another of its editions; a pressing's on the pressing.
        release.EditsJson = PersonEdits.Write(
            edits.Where(edit => !AlbumFields.Contains(edit.Key)).ToDictionary(StringComparer.Ordinal));

        if (release.ReleaseGroup is { } album)
        {
            album.EditsJson = PersonEdits.Write(
                edits.Where(edit => AlbumFields.Contains(edit.Key)).ToDictionary(StringComparer.Ordinal));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new EditResponse([.. edits.Keys.Order(StringComparer.Ordinal)]));
    }

    /// <summary>What the providers hold for an artist, in the edit map's terms.</summary>
    private static Dictionary<string, string?> ArtistFields(Artist artist) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = artist.Name,
            ["latinName"] = artist.LatinName,
            ["sortName"] = artist.SortName,
            ["disambiguation"] = artist.Disambiguation,
            ["type"] = artist.Type,
            ["country"] = artist.Country,
            ["gender"] = artist.Gender,
            ["beganYear"] = Number(artist.BeganYear),
            ["endedYear"] = Number(artist.EndedYear),
            ["ended"] = Flag(artist.Ended),
            ["genres"] = artist.Genres,
            ["biography"] = artist.BiographyText,
            ["portrait"] = artist.PortraitUrl,
            ["banner"] = artist.BannerUrl,
        };

    /// <summary>What the providers hold for an album, in the edit map's terms.</summary>
    private static Dictionary<string, string?> ReleaseFields(Release release, string? credit) =>
        new(StringComparer.Ordinal)
        {
            // The album's title, which is what every page lays the edit over.
            ["title"] = release.ReleaseGroup?.Title ?? release.Title,
            ["credit"] = credit,
            ["disambiguation"] = release.Disambiguation,
            ["primaryType"] = release.ReleaseGroup?.PrimaryType,
            ["secondaryTypes"] = release.ReleaseGroup?.SecondaryTypes,
            ["firstReleaseYear"] = Number(release.ReleaseGroup?.FirstReleaseYear),
            ["releasedYear"] = Number(release.ReleasedYear),
            ["releasedMonth"] = Number(release.ReleasedMonth),
            ["releasedDay"] = Number(release.ReleasedDay),
            ["country"] = release.Country,
            ["status"] = release.Status,
            ["label"] = release.Label,
            ["catalogNumber"] = release.CatalogNumber,
            ["barcode"] = release.Barcode,
            ["formats"] = release.MediumFormats,
            ["review"] = release.ReleaseGroup?.ReviewText,
        };

    private static async Task<string?> ReleaseCreditLineAsync(
        FonotecaDbContext db,
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        var credits = await db.ArtistCredits
            .AsNoTracking()
            .Where(credit => credit.ReleaseId == releaseId)
            .OrderBy(credit => credit.Position)
            .Select(credit => new
            {
                Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                credit.JoinPhrase,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return CreditLine(credits.Select(credit => (credit.Name, credit.JoinPhrase)));
    }

    /// <summary>The album list's and the album page's summary, with a person's corrections laid over it.</summary>
    private static AlbumSummary WithEdits(AlbumSummary summary, IReadOnlyDictionary<string, string?> edits)
    {
        if (edits.Count == 0) return summary;

        return summary with
        {
            Title = PersonEdits.Apply(edits, "title", summary.Title) ?? summary.Title,
            Artist = PersonEdits.Apply(edits, "credit", summary.Artist),
            // The album's year is when it was first released, so it is that
            // correction that moves it — not one to a pressing's own date.
            Year = Integer(PersonEdits.Apply(edits, "firstReleaseYear", Number(summary.Year))),

            // A pressing's own facts, corrected or not, only where one is claimed.
            Country = summary.EditionId is null ? null : PersonEdits.Apply(edits, "country", summary.Country),
            Status = summary.EditionId is null ? null : PersonEdits.Apply(edits, "status", summary.Status),
            Formats = summary.EditionId is null ? null : PersonEdits.Apply(edits, "formats", summary.Formats),
        };
    }

    /// <summary>The biography to show: a person's where they wrote one, Wikipedia's otherwise.</summary>
    /// <remarks>
    /// A person's text that replaced Wikipedia's keeps Wikipedia's link: an edit
    /// of CC BY-SA prose is still owed its credit.
    /// </remarks>
    private static WrittenText? Written(
        IReadOnlyDictionary<string, string?> edits,
        string field,
        string? text,
        string? url) =>
        edits.TryGetValue(field, out var mine)
            ? mine is null ? null
                : text is null ? new WrittenText(mine, "You", null, ByPerson: true)
                : new WrittenText(mine, "Wikipedia", url, ByPerson: true)
            : text is null ? null : new WrittenText(text, "Wikipedia", url, ByPerson: false);

    private static string? Invalid(ArtistEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "An artist needs a name.";

        foreach (var (label, value) in new[]
        {
            ("Name", request.Name), ("Latin name", request.LatinName), ("Sort name", request.SortName),
            ("Disambiguation", request.Disambiguation), ("Type", request.Type), ("Gender", request.Gender),
        })
        {
            if (value?.Length > 1000) return $"{label} is longer than 1,000 characters.";
        }

        return Country(request.Country)
            ?? Year("Began", request.BeganYear)
            ?? Year("Ended", request.EndedYear)
            ?? List("Genres", request.Genres)
            ?? (request.Biography?.Length > MaxBiographyLength
                ? $"The biography is longer than {MaxBiographyLength:N0} characters."
                : null)
            ?? Link("Profile picture", request.Portrait)
            ?? Link("Banner", request.Banner);
    }

    private static string? Invalid(ReleaseEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return "An album needs a title.";

        foreach (var (label, value, limit) in new[]
        {
            ("Title", request.Title, 1000), ("Credited to", request.Credit, 1000),
            ("Edition note", request.Disambiguation, 1000), ("Type", request.PrimaryType, 100),
            ("Status", request.Status, 50), ("Label", request.Label, 500),
            ("Catalogue number", request.CatalogNumber, 200), ("Barcode", request.Barcode, 50),
            ("Format", request.Formats, 200),
        })
        {
            if (value?.Length > limit) return $"{label} is longer than {limit:N0} characters.";
        }

        if (request.ReleasedMonth is { } month && (month < 1 || month > 12)) return "Month is 1 to 12.";
        if (request.ReleasedDay is { } day && (day < 1 || day > 31)) return "Day is 1 to 31.";

        return Country(request.Country)
            ?? Year("First released", request.FirstReleaseYear)
            ?? Year("Year", request.ReleasedYear)
            ?? List("Also", request.SecondaryTypes)
            ?? (request.Review?.Length > MaxBiographyLength
                ? $"The review is longer than {MaxBiographyLength:N0} characters."
                : null);
    }

    private static string? Country(string? value) =>
        string.IsNullOrWhiteSpace(value) || TwoLetters().IsMatch(value.Trim())
            ? null
            : "Country is a two-letter code.";

    private static string? Year(string label, int? value) =>
        value is null or (>= 1 and <= 9999) ? null : $"{label} is a year.";

    private static string? List(string label, IReadOnlyList<string>? values) =>
        Joined(values)?.Length > 1000 ? $"{label} is longer than 1,000 characters." : null;

    /// <summary>A picture URL a person typed: absolute https — it goes into an <c>img src</c> on an https page.</summary>
    private static string? Link(string label, string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (value.Length <= 1000
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps)
            ? null
            : $"{label} is a web address starting https://.";

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex TwoLetters();

    private static string? Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static int? Integer(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string Flag(bool value) => value ? "true" : "false";

    private static string? Joined(IReadOnlyList<string>? values)
    {
        var kept = values?.Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        return kept is null or { Count: 0 } ? null : string.Join(", ", kept);
    }

    private static string[] Split(string? joined) =>
        joined is { Length: > 0 } ? joined.Split(", ", StringSplitOptions.RemoveEmptyEntries) : [];
}

/// <summary>Everything the artist page shows that the list does not need.</summary>
/// <param name="Name">The name as MusicBrainz holds it, which the page prints under a Latin one.</param>
/// <param name="LatinName">The English alias, or a person's. Null where the name is already Latin.</param>
/// <param name="Banner">A wide photograph, or null — the page blurs the portrait then.</param>
/// <param name="Edited">The fields a person changed, so the page can say so beside each.</param>
/// <param name="PortraitUploaded">
/// Whether the picture in use is one a person uploaded rather than one a
/// provider found. The page offers to put the provider's back only when this is
/// true, because a button that undoes nothing is worse than no button.
/// </param>
/// <param name="BannerUploaded">As <paramref name="PortraitUploaded"/>, for the wide one.</param>
/// <param name="Members">For a group: the artists the catalogue records as members.</param>
/// <param name="MemberOf">For a person: the groups the catalogue records them in.</param>
public sealed record ArtistProfile(
    Guid? Mbid,
    string Name,
    string? LatinName,
    DateTimeOffset? PortraitLookupUtc,
    bool PortraitUploaded,
    string? Banner,
    DateTimeOffset? BannerLookupUtc,
    bool BannerUploaded,
    WrittenText? Biography,
    DateTimeOffset? BiographyLookupUtc,
    DateTimeOffset? DiscographyLookupUtc,
    IReadOnlyList<string> Edited,
    IReadOnlyList<RelatedArtist> Members,
    IReadOnlyList<RelatedArtist> MemberOf);

/// <summary>Prose with its provenance: somebody else's under their licence, or the owner's.</summary>
/// <param name="Source">"Wikipedia", or "You" for what a person wrote from nothing.</param>
/// <param name="ByPerson">A person wrote or edited it; with a <paramref name="Url"/> it was Wikipedia's first.</param>
/// <param name="Url">The article, for the credit line. Null for a person's text.</param>
public sealed record WrittenText(string Text, string Source, string? Url, bool ByPerson);

/// <summary>An artist on another artist's page, as much as a card needs.</summary>
public sealed record RelatedArtist(Guid Id, string Name, string? Portrait);

/// <summary>An artist page's whole form.</summary>
/// <param name="Genres">Most important first.</param>
/// <param name="Biography">Paragraphs separated by a blank line. Empty says "none".</param>
/// <param name="Portrait">An https URL, or empty for none.</param>
/// <param name="Banner">An https URL, or empty to blur the portrait.</param>
public sealed record ArtistEditRequest(
    string Name,
    string? LatinName,
    string? SortName,
    string? Disambiguation,
    string? Type,
    string? Country,
    string? Gender,
    int? BeganYear,
    int? EndedYear,
    bool Ended,
    IReadOnlyList<string> Genres,
    string? Biography,
    string? Portrait,
    string? Banner);

/// <summary>An album page's whole form.</summary>
/// <param name="Credit">The billing line as the sleeve prints it.</param>
/// <param name="ReleasedMonth">Null when unknown — never January. Ignored without a year.</param>
/// <param name="ReleasedDay">Ignored without a month.</param>
public sealed record ReleaseEditRequest(
    string Title,
    string? Credit,
    string? Disambiguation,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int? FirstReleaseYear,
    int? ReleasedYear,
    int? ReleasedMonth,
    int? ReleasedDay,
    string? Country,
    string? Status,
    string? Label,
    string? CatalogNumber,
    string? Barcode,
    string? Formats,
    string? Review);

/// <summary>What is now marked as a person's, after a save.</summary>
public sealed record EditResponse(IReadOnlyList<string> Edited);
