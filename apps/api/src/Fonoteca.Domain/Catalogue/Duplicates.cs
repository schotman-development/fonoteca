namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What the library holds more than once: an album in two folders, or a song
/// twice in one folder.
/// </summary>
/// <remarks>
/// <b>Two questions, the owner's choice of which.</b> A song on its album and
/// again on a best-of is not one of them: that is two records printing one
/// recording, and both are wanted. Bit-identical audio across folders is not
/// either, for the same reason — five sets here are an album and a compilation
/// cut from one master.
///
/// <b>Answers, never actions.</b> Nothing here removes anything; a person reads
/// the list and trashes the copy they choose. <see cref="HeldCopy.Better"/> is
/// a claim made only where it holds on every count, never a tie broken by
/// path, so an unmarked list means "look", not "either".
/// </remarks>
public static class Duplicates
{
    /// <summary>Every album held in more than one album folder, each folder with its files.</summary>
    /// <remarks>
    /// A folder is an album's when the album is most of the folder <i>and</i>
    /// the folder holds most of what the library has of the album — the
    /// upgrade list's test for naming a folder. Both ways, because two rips of
    /// one album each hold all of it, while a reissue box filed across four
    /// album folders has each of them holding a quarter, which is one album
    /// misfiled rather than four copies.
    /// </remarks>
    public static IReadOnlyList<AlbumHeldTwice> Albums(IEnumerable<HeldFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var all = files.ToList();

        var held = all
            .Where(file => file.Album is not null)
            .GroupBy(file => file.Album!.Value)
            .ToDictionary(album => album.Key, album => album.Select(SongKey).Distinct().Count());

        return
        [
            .. all
                .GroupBy(file => AlbumFolder.Of(file.Path))
                .Where(folder => folder.Key.Length > 0)
                .Select(folder => (Folder: folder, Album: AlbumOf([.. folder], held)))
                .Where(named => named.Album is not null)
                .GroupBy(named => named.Album!.Value)
                .Where(album => album.Count() > 1)
                .Select(album => new AlbumHeldTwice(album.Key, Ranked([.. album.Select(named => named.Folder)])))
                .OrderBy(album => album.Folders[0].Folder, StringComparer.Ordinal),
        ];
    }

    /// <summary>Every song held more than once inside one album folder.</summary>
    /// <remarks>
    /// A song is its recording, or for a file nothing has identified, its
    /// audio. <b>A release that prints a recording twice is not a duplicate</b>:
    /// both files that sit on their own slot of the pressing stay off the list,
    /// so only files beyond the slots they fill count. The two folders here
    /// holding a recording twice are both this: a 12-inch version and a ballet
    /// number, each printed twice by its pressing.
    ///
    /// ponytail: a folder held to its album alone has no slots, so an album
    /// printing a song twice lists there; none here. Count the tracks its
    /// editions print if one turns up.
    /// </remarks>
    public static IReadOnlyList<SongHeldTwice> Songs(IEnumerable<HeldFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return
        [
            .. files
                .Where(file => file.Recording is not null || file.AudioHash is not null)
                .GroupBy(file => (Folder: AlbumFolder.Of(file.Path), Song: file.Recording?.Value.ToString() ?? file.AudioHash))
                .Where(song => song.Count() > 1 && song.Count() > song.Where(file => file.Track is not null).Select(file => file.Track).Distinct().Count())
                .Select(song =>
                {
                    var copies = song.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

                    return new SongHeldTwice(
                        song.Key.Folder,
                        copies[0].Recording,
                        copies,
                        BetterCopy(copies)?.Path,
                        SameAudio: copies.All(file => file.AudioHash is not null && file.AudioHash == copies[0].AudioHash));
                })
                .OrderBy(song => song.Folder, StringComparer.Ordinal)
                .ThenBy(song => song.Copies[0].Path, StringComparer.Ordinal),
        ];
    }

    /// <summary>The copy that beats every other: intact where they are not, else better by the rule that keeps a duplicate.</summary>
    /// <remarks>
    /// Against every other copy rather than the runner-up, and never by quality
    /// against a copy that decodes and that nothing has measured: that one may
    /// be the lossless copy the probe has not reached yet.
    /// </remarks>
    private static HeldFile? BetterCopy(IReadOnlyList<HeldFile> copies) =>
        copies.FirstOrDefault(copy => copies.All(other => ReferenceEquals(copy, other)
            || Damaged(copy) < Damaged(other)
            || (Damaged(copy) == Damaged(other)
                && Decided(copy) is not null
                && Decided(other) is not null
                && AudioQuality.Compare(Decided(copy), Decided(other)) > 0)));

    /// <summary>The folders best first, the one that is at least as good on every count and better on one marked.</summary>
    /// <remarks>
    /// The counts are songs held, files the probe found damaged, and the worst
    /// file's quality — the worst, since a folder of FLACs with one MP3 in it
    /// is not as good as a folder of FLACs. A folder with a file that decodes
    /// and that nothing has measured cannot be compared, so no folder is
    /// called better while one of them has one.
    /// </remarks>
    private static IReadOnlyList<HeldCopy> Ranked(IReadOnlyList<IGrouping<string, HeldFile>> folders)
    {
        var copies = folders
            .Select(folder => new HeldCopy(
                folder.Key,
                [.. folder.OrderBy(file => file.Path, StringComparer.Ordinal)],
                folder.Select(SongKey).Distinct().Count(),
                folder.Count(file => Damaged(file) == 1),
                folder.Count(file => Damaged(file) == 0 && Decided(file) is null),
                Worst(folder),
                Better: false))
            .ToList();

        var better = copies.FirstOrDefault(copy => copies.All(other => ReferenceEquals(copy, other) || Beats(copy, other)));

        return
        [
            .. copies
                .Select(copy => ReferenceEquals(copy, better) ? copy with { Better = true } : copy)
                .OrderByDescending(copy => copy.Better)
                .ThenBy(copy => copy.Folder, StringComparer.Ordinal),
        ];
    }

    private static bool Beats(HeldCopy copy, HeldCopy other)
    {
        if (copy.Unmeasured > 0 || other.Unmeasured > 0) return false;

        var quality = AudioQuality.Compare(copy.Worst, other.Worst);

        return copy.Songs >= other.Songs
            && copy.Damaged <= other.Damaged
            && quality >= 0
            && (copy.Songs > other.Songs || copy.Damaged < other.Damaged || quality > 0);
    }

    /// <summary>The album most of the folder is held to, where the folder also holds most of it.</summary>
    private static ReleaseGroupId? AlbumOf(IReadOnlyList<HeldFile> folder, Dictionary<ReleaseGroupId, int> held) =>
        folder
            .Where(file => file.Album is not null)
            .GroupBy(file => file.Album!.Value)
            .Where(album => album.Count() * 2 > folder.Count && album.Select(SongKey).Distinct().Count() * 2 > held[album.Key])
            .Select(album => (ReleaseGroupId?)album.Key)
            .FirstOrDefault();

    /// <summary>The worst measured quality among the files that decode; null where none does.</summary>
    private static AudioQuality? Worst(IEnumerable<HeldFile> folder) =>
        folder
            .Where(file => Damaged(file) == 0)
            .Select(Decided)
            .OfType<AudioQuality>()
            .OrderBy(quality => quality, Comparer<AudioQuality>.Create(AudioQuality.Compare))
            .FirstOrDefault();

    private static string SongKey(HeldFile file) => file.Recording?.Value.ToString() ?? file.Path;

    private static int Damaged(HeldFile file) =>
        file.Integrity is IntegrityState.Corrupt or IntegrityState.Truncated or IntegrityState.Unreadable ? 1 : 0;

    private static AudioQuality? Decided(HeldFile file) => AudioAnalysis.Decided(file.Quality, file.Measurement);
}

/// <summary>A file as the duplicate rules read it.</summary>
public sealed record HeldFile(
    string Path,
    ReleaseGroupId? Album,
    RecordingId? Recording,
    TrackId? Track,
    string? AudioHash,
    AudioQuality? Quality,
    AudioMeasurement? Measurement,
    IntegrityState Integrity,
    long SizeBytes);

/// <summary>One album held in several album folders, the better one first where there is one.</summary>
public sealed record AlbumHeldTwice(ReleaseGroupId Album, IReadOnlyList<HeldCopy> Folders);

/// <param name="Songs">Distinct songs in the folder: recordings, and each unidentified file as itself.</param>
/// <param name="Damaged">Files the probe found corrupt, truncated or unreadable.</param>
/// <param name="Unmeasured">Files that decode and that nothing has measured.</param>
/// <param name="Worst">The worst measured quality among the files that decode; null where none does.</param>
/// <param name="Better">At least as good as every other folder on every count, and better on one.</param>
public sealed record HeldCopy(
    string Folder,
    IReadOnlyList<HeldFile> Files,
    int Songs,
    int Damaged,
    int Unmeasured,
    AudioQuality? Worst,
    bool Better);

/// <summary>One song held more than once in one album folder.</summary>
/// <param name="Recording">Null where the copies are one unidentified audio.</param>
/// <param name="Better">The path of the copy that beats every other, if one does.</param>
/// <param name="SameAudio">Every copy's audio is bit for bit the same.</param>
public sealed record SongHeldTwice(
    string Folder,
    RecordingId? Recording,
    IReadOnlyList<HeldFile> Copies,
    string? Better,
    bool SameAudio);
