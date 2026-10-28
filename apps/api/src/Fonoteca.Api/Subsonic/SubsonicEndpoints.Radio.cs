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
/// <b>Its logo is looked up when it is saved</b> (<see cref="IStationLogos"/>,
/// the owner's choice), since the protocol carries none, and goes in the
/// file. An update keeps the file's own logo while the stream stays the same,
/// so one put there by hand stays, and keeps the file's name while the
/// station's stays, so an id does not change under an edit.
///
/// An update restates the whole station, so a <c>homepageUrl</c> it leaves out
/// is a station with no home page, as Navidrome reads it.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetInternetRadioStations(PlaylistFiles files, CancellationToken cancellationToken)
    {
        List<(string Id, RadioStation Station)> stations = [];

        foreach (var path in files.StationPaths())
        {
            if (await files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } file
                && RadioStationFile.Read(file.Text, path) is { } station)
            {
                stations.Add((SubsonicIds.RadioStation(path[(RadioStationFile.Folder.Length + 1)..]), station));
            }
        }

        return SubsonicResult.Ok("internetRadioStations", list => list.Children(
            "internetRadioStation",
            stations.OrderBy(entry => entry.Station.Name, StringComparer.OrdinalIgnoreCase),
            WriteStation));
    }

    private static async Task<IResult> CreateInternetRadioStation(
        HttpContext http,
        PlaylistFiles files,
        IStationLogos logos,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;
        if (!files.Mounted()) return StationsUnmounted();

        var sent = StationSent(http);
        var path = RadioStationFile.PathFor(sent.Name);

        // Asked again under the lock; here so a name already taken asks radio-browser nothing.
        if (StationTaken(files, path, self: null) is { } early) return early;

        var station = sent with { ImageUrl = await logos.FindAsync(sent, cancellationToken).ConfigureAwait(false) };

        return await files.EditAsync(
                async () => StationTaken(files, path, self: null)
                    ?? (await files.WriteLockedAsync(path, RadioStationFile.Write(null, station), bom: false, cancellationToken).ConfigureAwait(false)
                        ? SubsonicResult.Ok()
                        : StationNotWritten(path)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> UpdateInternetRadioStation(
        HttpContext http,
        PlaylistFiles files,
        IStationLogos logos,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();
        if ((Missing(http, "id", "streamUrl", "name") ?? Unfit(http)) is { } refused) return refused;
        if (!files.Mounted()) return StationsUnmounted();

        if (StationPath(Text(http, "id")) is not { } path
            || await files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } before
            || RadioStationFile.Read(before.Text, path) is not { } was)
        {
            return NoSuchStation();
        }

        var sent = StationSent(http);
        var target = sent.Name == was.Name ? path : RadioStationFile.PathFor(sent.Name);

        if (StationTaken(files, target, self: path) is { } early) return early;

        var found = (was.StreamUrl == sent.StreamUrl ? was.ImageUrl : null)
            ?? await logos.FindAsync(sent, cancellationToken).ConfigureAwait(false);

        return await files.EditAsync(
                async () =>
                {
                    if (await files.ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { } current
                        || RadioStationFile.Read(current.Text, path) is not { } now)
                    {
                        return NoSuchStation();
                    }

                    if (!current.Editable) return SubsonicResult.Error(0, $"'{path}' is not UTF-8, so it is read and never written.");
                    if (StationTaken(files, target, self: path) is { } taken) return taken;

                    var image = now.StreamUrl == sent.StreamUrl && now.ImageUrl is { } own ? own : found;
                    var text = RadioStationFile.Write(current.Text, sent with { ImageUrl = image });

                    if (target != path && CatalogueEndpoints.Move(files, path, target) is { } stuck) return Refusal(stuck)!;

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

        return await files.EditAsync(
                () => Task.FromResult(
                    !File.Exists(files.AbsolutePathFor(path)) ? NoSuchStation()
                    : CatalogueEndpoints.Trash(files, options.Value, clock, logger, path) is { } stuck ? Refusal(stuck)!
                    : SubsonicResult.Ok()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void WriteStation(ISubsonicWriter writer, (string Id, RadioStation Station) entry)
    {
        writer.Attr("id", entry.Id);
        writer.Attr("name", entry.Station.Name);
        writer.Attr("streamUrl", entry.Station.StreamUrl);
        writer.Attr("homePageUrl", entry.Station.HomePageUrl);

        // The logo the file names, served by getCoverArt under the station's own id.
        if (entry.Station.ImageUrl is not null) writer.Attr("coverArt", entry.Id);
    }

    private static RadioStation StationSent(HttpContext http) =>
        new(Text(http, "name")!.Trim(), Text(http, "streamUrl")!, Text(http, "homepageUrl"), ImageUrl: null);

    /// <summary>The station file an id names, or null where it names none: one file at the top of the Radio folder.</summary>
    private static string? StationPath(string? id) =>
        SubsonicIds.AsRadioStation(id) is { } file
        && !file.StartsWith('.')
        && !file.Any(char.IsControl)
        && file.AsSpan().IndexOfAny('/', '\\') < 0
        && string.Equals(Path.GetExtension(file), RadioStationFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? $"{RadioStationFile.Folder}/{file}"
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
