using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Releases and album folders for the attribution rules, built by name.
/// </summary>
/// <remarks>
/// Lengths are given in milliseconds, because a track list of whole seconds
/// proves nothing (see <see cref="EditionProof"/>) and a fixture that forgot that
/// would test the refusal rather than the rule.
/// </remarks>
internal static class AlbumFolderFixtures
{
    public static readonly Mbid Group = Mb("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public static readonly Mbid OtherGroup = Mb("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    public static MusicBrainzRelease Release(
        string id,
        (string Name, int? Ms)[] tracks,
        Mbid? group = null,
        string status = "Official",
        string? format = "CD",
        int? year = null) =>
        new(
            Id: Id(id),
            Title: id,
            ReleasedOn: year is { } known ? new ReleaseDate(known, null, null) : null,
            Country: null,
            Status: status,
            Barcode: null,
            Labels: [],
            ReleaseGroupId: group ?? Group,
            ReleaseGroupTitle: id,
            PrimaryType: "Album",
            SecondaryTypes: [],
            Credits: [],
            Tracks: [.. tracks.Select((track, index) => new MusicBrainzTrack(
                DiscNumber: 1,
                Position: index + 1,
                Number: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title: track.Name,
                Length: track.Ms is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                RecordingId: Recording(track.Name),
                Credits: []))],
            Media: [new MusicBrainzMediumSummary(1, format, tracks.Length)]);

    /// <summary>A track list of these names, each at the given length.</summary>
    public static (string Name, int? Ms)[] Tracks(params (string Name, int Ms)[] tracks) =>
        [.. tracks.Select(track => (track.Name, (int?)track.Ms))];

    /// <summary>Tracks the folder does not hold, to dilute a candidate's coverage.</summary>
    public static (string Name, int? Ms)[] Filler(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(index => ($"{prefix}-{index}", (int?)200_437))];

    /// <summary>
    /// A file of this recording at this measured length, whose browse reached the
    /// groups of every candidate printing it.
    /// </summary>
    public static FolderFile File(
        string name,
        int ms,
        IReadOnlyCollection<MusicBrainzRelease> candidates,
        string? path = null,
        AudioQuality? quality = null,
        int? disc = null,
        int? track = null,
        IReadOnlySet<Mbid>? linked = null) =>
        new(
            FileId(path ?? name),
            path ?? $"Artist/Album/{name}.flac",
            Recording(name),
            candidates
                .Where(release => release.Tracks.Any(printed => printed.RecordingId == Recording(name)))
                .Select(release => release.ReleaseGroupId!.Value)
                .ToHashSet(),
            linked ?? new HashSet<Mbid>(),
            TimeSpan.FromMilliseconds(ms),
            quality,
            disc,
            track);

    /// <summary>A file nothing has identified.</summary>
    public static FolderFile Unidentified(string path) =>
        new(FileId(path), path, null, new HashSet<Mbid>(), new HashSet<Mbid>(), null, null, null, null);

    public static AudioQuality Lossless(int depth, int rate, double seconds) => new()
    {
        Codec = "flac",
        SampleRateHz = rate,
        Channels = 2,
        BitDepth = depth,
        BitrateBps = 900_000,
        IsLossless = true,
        Duration = TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)),
    };

    public static AudioQuality Lossy() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };

    public static Mbid Id(string name) => new(Deterministic(name, 0xA1));

    public static Mbid Recording(string name) => new(Deterministic(name, 0x2E));

    public static MediaFileId FileId(string name) => new(Deterministic(name, 0xF1));

    private static Guid Deterministic(string name, byte salt)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        bytes[0] = salt;
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static Mbid Mb(string value) => new(Guid.Parse(value));
}
