using Fonoteca.Data;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * The Duplicates screen's list (`Duplicates`). It removes nothing: a person
 * trashes the copy they choose through `POST /api/files/trash`.
 */
public static partial class CatalogueEndpoints
{
    private static void MapDuplicateEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/duplicates", GetDuplicates)
            .WithName("GetDuplicates")
            .WithSummary("Albums held in more than one folder, and songs held twice in one folder.");
    }

    internal static async Task<Ok<DuplicatesResponse>> GetDuplicates(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        // ponytail: every file, grouped in memory, as the upgrade list does.
        var files = await db.MediaFiles
            .AsNoTracking()
            .Select(file => new HeldFile(
                file.Path,
                file.ReleaseGroupId,
                file.RecordingId,
                file.TrackId,
                file.AudioHash,
                file.Quality,
                file.Measurement,
                file.Integrity,
                file.SizeBytes))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var albums = Duplicates.Albums(files);
        var songs = Duplicates.Songs(files);

        var albumIds = albums.Select(album => album.Album).ToList();
        var titles = await db.ReleaseGroups
            .AsNoTracking()
            .Where(group => albumIds.Contains(group.Id))
            .Select(group => new
            {
                group.Id,
                group.Title,
                Credits = group.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new { Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name, credit.JoinPhrase })
                    .ToList(),
            })
            .ToDictionaryAsync(group => group.Id, cancellationToken)
            .ConfigureAwait(false);

        var recordingIds = songs.Where(song => song.Recording is not null).Select(song => song.Recording!.Value).ToList();
        var recordings = await db.Recordings
            .AsNoTracking()
            .Where(recording => recordingIds.Contains(recording.Id))
            .ToDictionaryAsync(recording => recording.Id, recording => recording.Title, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new DuplicatesResponse(
            [
                .. albums.Select(album => new DuplicateAlbum(
                    album.Album.Value,
                    titles[album.Album].Title,
                    CreditLine(titles[album.Album].Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                    [
                        .. album.Folders.Select(folder => new DuplicateFolder(
                            folder.Folder,
                            folder.Files.Count,
                            folder.Songs,
                            folder.Damaged,
                            [
                                .. folder.Files
                                    .GroupBy(file => UpgradeScan.Format(file.Path, AudioAnalysis.Decided(file.Quality, file.Measurement)))
                                    .OrderByDescending(format => format.Count())
                                    .ThenBy(format => format.Key, StringComparer.Ordinal)
                                    .Select(format => format.Key),
                            ],
                            folder.Files.Sum(file => file.SizeBytes),
                            folder.Better)),
                    ])),
            ],
            [
                .. songs.Select(song => new DuplicateSong(
                    song.Folder,
                    song.Recording is { } recording ? recordings[recording] : Path.GetFileNameWithoutExtension(song.Copies[0].Path),
                    song.SameAudio,
                    [
                        .. song.Copies.Select(copy => new DuplicateCopy(
                            copy.Path,
                            UpgradeScan.Format(copy.Path, AudioAnalysis.Decided(copy.Quality, copy.Measurement)),
                            copy.SizeBytes,
                            copy.Integrity is IntegrityState.Corrupt or IntegrityState.Truncated or IntegrityState.Unreadable,
                            copy.Path == song.Better)),
                    ])),
            ]));
    }
}

/// <param name="Albums">Albums held in more than one album folder, by their first folder.</param>
/// <param name="Songs">Songs held more than once inside one album folder, beyond the slots their pressing prints.</param>
public sealed record DuplicatesResponse(IReadOnlyList<DuplicateAlbum> Albums, IReadOnlyList<DuplicateSong> Songs);

/// <param name="Id">The album (release group).</param>
/// <param name="Folders">The better folder first where one is; otherwise by path.</param>
public sealed record DuplicateAlbum(Guid Id, string Title, string? Artist, IReadOnlyList<DuplicateFolder> Folders);

/// <param name="Path">The album folder, library-relative.</param>
/// <param name="Songs">Distinct songs it holds; an unidentified file counts as one.</param>
/// <param name="Damaged">Files the probe found corrupt, truncated or unreadable.</param>
/// <param name="Formats">Its files' formats as the upgrade list prints them, commonest first.</param>
/// <param name="Better">At least as good as every other folder on songs, damage and its worst file's quality, and better on one.</param>
public sealed record DuplicateFolder(
    string Path,
    int Files,
    int Songs,
    int Damaged,
    IReadOnlyList<string> Formats,
    long SizeBytes,
    bool Better);

/// <param name="Folder">The album folder holding every copy.</param>
/// <param name="Title">The recording's, or the first copy's file name where nothing identified it.</param>
/// <param name="SameAudio">Every copy's audio is bit for bit the same.</param>
public sealed record DuplicateSong(string Folder, string Title, bool SameAudio, IReadOnlyList<DuplicateCopy> Copies);

/// <param name="Better">This copy beats every other: intact where they are not, else better measured.</param>
public sealed record DuplicateCopy(string Path, string Format, long SizeBytes, bool Damaged, bool Better);
