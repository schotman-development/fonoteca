using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// A song's level over time, for a seek bar drawn as its waveform: Fonoteca's
/// own <c>getWaveform</c>, advertised as <c>fonotecaWaveform</c>, since the
/// protocol has no such call.
/// </summary>
/// <remarks>
/// The levels the analyse pass stored (<see cref="Domain.Catalogue.MediaFile.Waveform"/>),
/// each an RMS amplitude from 0 to 1, evenly spaced over the song. A song that
/// pass has not measured, or measured before it kept a waveform, is not
/// found, and a client keeps its plain bar.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>Places a level is sent to: a ten-thousandth of full scale, finer than any bar is drawn.</summary>
    private const int WaveformDecimals = 4;

    private static async Task<IResult> GetWaveform(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var levels = SubsonicIds.AsSong(Text(http, "id")) is { } fileId
            ? await db.MediaFiles
                .AsNoTracking()
                .Where(row => row.Id == fileId)
                .Select(row => row.Waveform)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (levels is not { Length: > 0 }) return SubsonicResult.Error(70, "No such waveform.");

        return SubsonicResult.Ok("waveform", waveform =>
            waveform.Numbers("level", levels.Select(level => Math.Round((double)level, WaveformDecimals))));
    }
}
