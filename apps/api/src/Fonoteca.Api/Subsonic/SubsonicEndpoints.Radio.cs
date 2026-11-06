using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Internet radio: the XSPF files in the library's Radio folder
/// (<see cref="RadioStationFile"/>), every account's to list and an admin's to
/// change.
/// </summary>
/// <remarks>
/// <b>A station is its file</b> (the owner's choice), as a static playlist is:
/// its id is the file's name, whatever else writes there is believed, a write
/// is a staged file renamed over it, nothing is written while the library
/// looks unmounted, and a deleted station's file goes to the trash.
///
/// <b>Its logo is the PNG beside it</b> (<see cref="RadioStationFile.LogoPathFor"/>),
/// put there by hand: the protocol carries none, so a station saved from a
/// client has none until somebody adds it. A rename takes the logo along and a
/// delete trashes it with the station. An update keeps the file's name while
/// the station's stays, so an id does not change under an edit.
///
/// An update restates the whole station, so a <c>homepageUrl</c> it leaves out
/// is a station with no home page, as Navidrome reads it.
///
/// <b>The stations on offer are a catalogue that ships with the server</b>
/// (<see cref="CatalogueFolder"/>, the owner's choice): curated stations with
/// their logos, which <c>getRadioCatalogue</c> lists and nothing saves on its
/// own. A person saves one through <c>createInternetRadioStation</c> like any
/// other, and a saved stream the catalogue holds takes the catalogue's logo,
/// country, languages and genres. Those three the protocol carries no
/// parameter for, so an update keeps the file's.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetInternetRadioStations(PlaylistFiles files, CancellationToken cancellationToken)
    {
        List<(string Id, RadioStation Station, bool HasLogo)> stations = [];

        foreach (var path in files.StationPaths())
        {
            if (await files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } file
                && RadioStationFile.Read(file.Text, path) is { } station)
            {
                stations.Add((
                    SubsonicIds.RadioStation(path[(RadioStationFile.Folder.Length + 1)..]),
                    station,
                    File.Exists(files.AbsolutePathFor(RadioStationFile.LogoPathFor(path)))));
            }
        }

        return SubsonicResult.Ok("internetRadioStations", list => list.Children(
            "internetRadioStation",
            stations.OrderBy(entry => entry.Station.Name, StringComparer.OrdinalIgnoreCase),
            WriteStation));
    }

    /// <summary>Where the curated stations the server offers are shipped, beside its binaries.</summary>
    private static readonly string CatalogueFolder = Path.Combine(AppContext.BaseDirectory, "RadioCatalogue");

    private static async Task<IResult> GetRadioCatalogue(CancellationToken cancellationToken)
    {
        var stations = await CatalogueAsync(cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok("radioCatalogue", list => list.Children(
            "internetRadioStation",
            stations.OrderBy(entry => entry.Station.Name, StringComparer.OrdinalIgnoreCase),
            WriteStation));
    }

    /// <summary>Every station in the catalogue, by its id, with whether a logo ships beside it.</summary>
    private static async Task<List<(string Id, RadioStation Station, bool HasLogo)>> CatalogueAsync(CancellationToken cancellationToken)
    {
        List<(string Id, RadioStation Station, bool HasLogo)> stations = [];

        if (!Directory.Exists(CatalogueFolder)) return stations;

        foreach (var path in Directory.EnumerateFiles(CatalogueFolder, "*" + RadioStationFile.Extension))
        {
            var name = Path.GetFileName(path);

            if (RadioStationFile.Read(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), name) is { } station)
            {
                stations.Add((SubsonicIds.CatalogueStation(name), station, File.Exists(Path.ChangeExtension(path, RadioStationFile.LogoExtension))));
            }
        }

        return stations;
    }

    /// <summary>The catalogue's station with this stream and the absolute path of its logo, or null where it has none.</summary>
    private static async Task<(RadioStation Station, string? Logo)?> CatalogueStationAsync(string streamUrl, CancellationToken cancellationToken) =>
        (await CatalogueAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Station.StreamUrl == streamUrl)
            .Select(entry => ((RadioStation, string?)?)(
                entry.Station,
                entry.HasLogo ? Path.ChangeExtension(Path.Combine(CatalogueFolder, SubsonicIds.AsCatalogueStation(entry.Id)!), RadioStationFile.LogoExtension) : null))
            .FirstOrDefault();

    private static async Task<IResult> CreateInternetRadioStation(
        HttpContext http,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;
        if (!files.Mounted()) return StationsUnmounted();

        var sent = StationSent(http);
        var curated = await CatalogueStationAsync(sent.StreamUrl, cancellationToken).ConfigureAwait(false);
        var station = curated is { Station: var offered } ? sent.ShelvedAs(offered) : sent;
        var path = RadioStationFile.PathFor(station.Name);

        return await files.EditAsync(
                async () =>
                {
                    if (StationTaken(files, path, self: null) is { } taken) return taken;
                    if (!await files.WriteLockedAsync(path, RadioStationFile.Write(null, station), bom: false, cancellationToken).ConfigureAwait(false)) return StationNotWritten(path);

                    var logo = files.AbsolutePathFor(RadioStationFile.LogoPathFor(path));

                    if (curated is { Logo: { } shipped } && !File.Exists(logo)) File.Copy(shipped, logo);

                    return SubsonicResult.Ok();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> UpdateInternetRadioStation(
        HttpContext http,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "id", "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;
        if (!files.Mounted()) return StationsUnmounted();

        if (StationPath(Text(http, "id")) is not { } path) return NoSuchStation();

        var sent = StationSent(http);

        return await files.EditAsync(
                async () =>
                {
                    if (await files.ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { } current
                        || RadioStationFile.Read(current.Text, path) is not { } now)
                    {
                        return NoSuchStation();
                    }

                    var target = sent.Name == now.Name ? path : RadioStationFile.PathFor(sent.Name);

                    if (!current.Editable) return SubsonicResult.Error(0, $"'{path}' is not UTF-8, so it is read and never written.");
                    if (StationTaken(files, target, self: path) is { } taken) return taken;

                    var text = RadioStationFile.Write(current.Text, sent.ShelvedAs(now));

                    if (target != path
                        && (CatalogueEndpoints.Move(files, path, target)
                            ?? CatalogueEndpoints.Move(files, RadioStationFile.LogoPathFor(path), RadioStationFile.LogoPathFor(target))) is { } stuck)
                    {
                        return Refusal(stuck)!;
                    }

                    return string.Equals(text, current.Text, StringComparison.Ordinal)
                        || await files.WriteLockedAsync(target, text, current.Bom, cancellationToken).ConfigureAwait(false)
                            ? SubsonicResult.Ok()
                            : StationNotWritten(target);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteInternetRadioStation(
        HttpContext http,
        PlaylistFiles files,
        IOptions<FonotecaOptions> options,
        IClock clock,
        ILogger<PlaylistFiles> logger,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if (Missing(http, "id") is { } missing) return missing;
        if (StationPath(Text(http, "id")) is not { } path) return NoSuchStation();

        var logo = RadioStationFile.LogoPathFor(path);

        return await files.EditAsync(
                () => Task.FromResult(
                    !File.Exists(files.AbsolutePathFor(path)) ? NoSuchStation()
                    : CatalogueEndpoints.Trash(files, options.Value, clock, logger, path) is { } stuck ? Refusal(stuck)!
                    : File.Exists(files.AbsolutePathFor(logo)) && CatalogueEndpoints.Trash(files, options.Value, clock, logger, logo) is { } kept ? Refusal(kept)!
                    : SubsonicResult.Ok()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void WriteStation(ISubsonicWriter writer, (string Id, RadioStation Station, bool HasLogo) entry)
    {
        writer.Attr("id", entry.Id);
        writer.Attr("name", entry.Station.Name);
        writer.Attr("streamUrl", entry.Station.StreamUrl);
        writer.Attr("homePageUrl", entry.Station.HomePageUrl);
        writer.Attr("countryCode", entry.Station.CountryCode);

        // The logo beside the file, served by getCoverArt under the station's own id.
        if (entry.HasLogo) writer.Attr("coverArt", entry.Id);

        // Lists are child elements in XML, so they come after every attribute.
        if (entry.Station.LanguageCodes.Count > 0) writer.Strings("languageCodes", entry.Station.LanguageCodes);
        if (entry.Station.Genres.Count > 0) writer.Strings("genres", entry.Station.Genres);
    }

    private static RadioStation StationSent(HttpContext http) =>
        new(Text(http, "name")!.Trim(), Text(http, "streamUrl")!, Text(http, "homepageUrl"));

    /// <summary>The station file an id names, or null where it names none: one file at the top of the Radio folder.</summary>
    private static string? StationPath(string? id) =>
        StationFile(SubsonicIds.AsRadioStation(id)) is { } file ? $"{RadioStationFile.Folder}/{file}" : null;

    /// <summary>The catalogue's station file an id names, as an absolute path, or null where it names none.</summary>
    private static string? CatalogueStationPath(string? id) =>
        StationFile(SubsonicIds.AsCatalogueStation(id)) is { } file ? Path.Combine(CatalogueFolder, file) : null;

    /// <summary>The name when it is one visible XSPF file in a single folder, else null.</summary>
    private static string? StationFile(string? name) =>
        name is not null
        && !name.StartsWith('.')
        && !name.Any(char.IsControl)
        && name.AsSpan().IndexOfAny('/', '\\') < 0
        && string.Equals(Path.GetExtension(name), RadioStationFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : null;

    /// <summary>Error 0 where another station's file has this path, as a case-blind share would see it; null where none does.</summary>
    private static SubsonicResult? StationTaken(PlaylistFiles files, string path, string? self) =>
        files.StationPaths().Any(other => string.Equals(other, path, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(other, self, StringComparison.OrdinalIgnoreCase))
            ? SubsonicResult.Error(0, $"A station has that name: '{path}' is already one.")
            : null;

    /// <summary>Error 0 for a name that is blank, overlong or not one line, or an address that is not a web one; null when all fit.</summary>
    private static SubsonicResult? Unfit(HttpContext http)
    {
        var name = Text(http, "name")!.Trim();

        if (name.Length is 0 or > RadioStationFile.NameLength || name.Any(character => char.IsControl(character) || character is '\uFFFE' or '\uFFFF'))
            return SubsonicResult.Error(0, $"A station's name is one line of at most {RadioStationFile.NameLength} characters.");

        foreach (var parameter in (string[])["streamUrl", "homepageUrl"])
        {
            if (Text(http, parameter) is { } url && !RadioStationFile.WebAddress(url))
                return SubsonicResult.Error(0, $"{parameter} must be an http or https address of at most {RadioStationFile.UrlLength} characters.");
        }

        return null;
    }

    private static SubsonicResult NoSuchStation() => SubsonicResult.Error(70, "No such station.");

    private static SubsonicResult StationsUnmounted() =>
        SubsonicResult.Error(0, "The library is not mounted: its root holds nothing but the Playlists and Radio folders, so nothing is written there.");

    private static SubsonicResult StationNotWritten(string path) =>
        SubsonicResult.Error(0, $"'{path}' could not be written; the log says why.");
}
