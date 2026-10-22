using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Internet radio: <see cref="RadioStation"/>, every account's to list and an
/// admin's to change.
/// </summary>
/// <remarks>
/// An update restates the whole station, so a <c>homepageUrl</c> it leaves out
/// is a station with no home page, as Navidrome reads it.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetInternetRadioStations(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var stations = await db.RadioStations
            .AsNoTracking()
            .OrderBy(station => station.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("internetRadioStations", list => list.Children("internetRadioStation", stations, WriteStation));
    }

    private static async Task<IResult> CreateInternetRadioStation(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;

        db.RadioStations.Add(new RadioStation
        {
            Id = Guid.CreateVersion7(),
            Name = Text(http, "name")!,
            StreamUrl = Text(http, "streamUrl")!,
            HomePageUrl = Text(http, "homepageUrl"),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SubsonicResult.Ok();
    }

    private static async Task<IResult> UpdateInternetRadioStation(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "id", "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;
        if (await StationAsync(http, db, cancellationToken).ConfigureAwait(false) is not { } station) return NoSuchStation();

        station.Name = Text(http, "name")!;
        station.StreamUrl = Text(http, "streamUrl")!;
        station.HomePageUrl = Text(http, "homepageUrl");

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SubsonicResult.Ok();
    }

    private static async Task<IResult> DeleteInternetRadioStation(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if (Missing(http, "id") is { } missing) return missing;
        if (await StationAsync(http, db, cancellationToken).ConfigureAwait(false) is not { } station) return NoSuchStation();

        db.RadioStations.Remove(station);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SubsonicResult.Ok();
    }

    private static void WriteStation(ISubsonicWriter station, RadioStation row)
    {
        station.Attr("id", row.Id.ToString("N"));
        station.Attr("name", row.Name);
        station.Attr("streamUrl", row.StreamUrl);
        station.Attr("homePageUrl", row.HomePageUrl);
    }

    private static async Task<RadioStation?> StationAsync(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken) =>
        Guid.TryParse(Text(http, "id"), out var id)
            ? await db.RadioStations.FindAsync([id], cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Error 0 for a name the table cannot hold or an address that is not a web one; null when both fit.</summary>
    private static SubsonicResult? Unfit(HttpContext http)
    {
        if (Text(http, "name")!.Length > RadioStation.NameLength)
            return SubsonicResult.Error(0, $"A station's name is at most {RadioStation.NameLength} characters.");

        foreach (var name in (string[])["streamUrl", "homepageUrl"])
        {
            if (Text(http, name) is { } url && !WebAddress(url))
                return SubsonicResult.Error(0, $"{name} must be an http or https address of at most {RadioStation.UrlLength} characters.");
        }

        return null;
    }

    private static bool WebAddress(string url) =>
        url.Length <= RadioStation.UrlLength
        && Uri.TryCreate(url, UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    private static SubsonicResult NoSuchStation() => SubsonicResult.Error(70, "No such station.");
}
