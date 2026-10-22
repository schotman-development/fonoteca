namespace Fonoteca.Data;

/// <summary>
/// An internet radio station, as OpenSubsonic's radio methods keep them.
/// </summary>
/// <remarks>
/// Everyone's, like the playlists (ADR 0018), and an admin's to change — the
/// protocol's rule. Fonoteca never opens either address: a client plays the
/// stream and shows the home page, so each is held to an absolute http or
/// https URL and no further.
/// </remarks>
public sealed class RadioStation
{
    public const int NameLength = 200;

    public const int UrlLength = 2000;

    public required Guid Id { get; init; }

    public required string Name { get; set; }

    public required string StreamUrl { get; set; }

    public string? HomePageUrl { get; set; }
}
