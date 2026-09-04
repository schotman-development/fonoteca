using ATL;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Tagging;

/// <summary>
/// Reads a file's tags with both libraries.
/// </summary>
/// <remarks>
/// ADR 0002's first step, and the reason this project carries two dependencies
/// rather than one. ATL.NET is the writer; TagLib# never writes, and exists only
/// so that a claim about a file has a second, independent witness. Neither is
/// trusted alone: Jellyfin ships both and Lidarr maintains its own TagLib# fork,
/// so neither project trusts one library either.
///
/// Everything goes through <see cref="IAudioFileStore"/> streams, so this
/// project never learns what a filesystem is — the library root stays the
/// adapter's secret, and a path outside it is rejected before a byte is read.
/// </remarks>
public sealed class TagReader(IAudioFileStore files)
{
    private readonly IAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>
    /// The AcoustID this file already claims, or null.
    /// </summary>
    /// <remarks>
    /// The cheap question, asked once per file at the head of the identification
    /// pass. It is what makes "identify the files that have no AcoustID yet"
    /// literally true: 227 files in the target library were tagged by Picard
    /// years ago, and re-deriving what they already state would cost 227
    /// fingerprints and 227 turns at a rate limit to learn nothing.
    ///
    /// Never throws on a file it cannot parse, and that promise is kept with a
    /// catch-all rather than a list of expected types — because the expected
    /// types are not what a tag parser actually raises. ATL throws a
    /// <see cref="NullReferenceException"/> on a FLAC carrying a prepended ID3v2
    /// header, and there are forty of those in the author's library. Here that
    /// costs nothing: a file whose fields cannot be read has no AcoustID as far
    /// as this is concerned, which is the same answer as a file that was never
    /// tagged, and both lead to the same next step — fingerprint it.
    ///
    /// <see cref="ReadAsync"/> deliberately does <i>not</i> take the same view,
    /// because its answer is used to decide what to write.
    /// </remarks>
    public async Task<string?> ReadAcoustIdAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default)
    {
        var field = AcoustIdTagField.For(path);
        if (field is null) return null;

        try
        {
            var stream = await _files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var track = new Track(stream, ExtensionOf(path));
                return Normalise(Lookup(track, field));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Documented above: an unreadable file simply has no AcoustID.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Everything both libraries can see, for the diff and the undo journal.</summary>
    /// <param name="containerAs">
    /// The file whose extension says what container this is, when that is not
    /// <paramref name="path"/> itself.
    /// </param>
    /// <remarks>
    /// <paramref name="containerAs"/> exists for one caller and is not
    /// decoration. Verification reads back a <i>staging</i> file, whose name ends
    /// in <c>.tmp</c> so that nothing can mistake it for library content — and
    /// both tag libraries pick their parser from the extension, so asked about a
    /// <c>.tmp</c> they have no idea what they are looking at. The write knows
    /// exactly what container it just rendered; saying so is more honest than
    /// naming the temporary file after the format and hoping.
    /// </remarks>
    public async Task<TagSnapshot> ReadAsync(
        LibraryPath path,
        LibraryPath? containerAs = null,
        CancellationToken cancellationToken = default)
    {
        var container = containerAs ?? path;

        var stream = await _files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return Describe(new Track(stream, ExtensionOf(container)), AcoustIdTagField.For(container));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Whatever ATL raises means one thing here; see TagReadFailedException.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                throw new TagReadFailedException(path, "ATL", cause);
            }
        }
    }

    /// <summary>
    /// The same reading, taken by TagLib# instead — the independent witness.
    /// </summary>
    /// <remarks>
    /// TagLib# is asked for the AcoustID through its <i>native</i> accessors
    /// rather than a generic map, because that is the whole point: it must
    /// confirm the value is in the frame the format actually specifies, not
    /// merely somewhere in the file under a name we chose.
    /// </remarks>
    /// <inheritdoc cref="ReadAsync(LibraryPath, LibraryPath?, CancellationToken)" path="/param"/>
    public async Task<TagSnapshot> ReadWithVerifierAsync(
        LibraryPath path,
        LibraryPath? containerAs = null,
        CancellationToken cancellationToken = default)
    {
        // TagLib# resolves its parser from the abstraction's Name, so the name it
        // is given has to be the one whose extension means something.
        var container = containerAs ?? path;

        var stream = await _files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                using var file = TagLib.File.Create(new StreamFileAbstraction(container.Value, stream));
                return VerifierReading.Describe(file);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // TagLib# has its own vocabulary for "no"; see TagReadFailedException.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                throw new TagReadFailedException(path, "TagLib#", cause);
            }
        }
    }

    /// <summary>Reads the field, tolerating the case-folding each format applies.</summary>
    internal static string? Lookup(Track track, string field)
    {
        if (track.AdditionalFields.TryGetValue(field, out var exact)) return exact;

        // Vorbis comments come back uppercased, and MP4 strips the
        // "----:com.apple.iTunes:" prefix it added. Matching case-insensitively
        // on the last segment covers both without the caller needing to know.
        foreach (var (key, value) in track.AdditionalFields)
        {
            var name = key.AsSpan();
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name[(colon + 1)..];

            if (name.Equals(field, StringComparison.OrdinalIgnoreCase)) return value;
        }

        return null;
    }

    internal static TagSnapshot Describe(Track track, string? acoustIdField)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in track.AdditionalFields)
        {
            fields[key] = value;
        }

        AddIfPresent(fields, "TITLE", track.Title);
        AddIfPresent(fields, "ARTIST", track.Artist);
        AddIfPresent(fields, "ALBUM", track.Album);
        AddIfPresent(fields, "ALBUMARTIST", track.AlbumArtist);
        AddIfPresent(fields, "DATE", track.Date?.ToString("O"));
        AddIfPresent(fields, "GENRE", track.Genre);
        AddIfPresent(fields, "COMPOSER", track.Composer);
        AddIfPresent(fields, "COMMENT", track.Comment);

        // The numbers, so a catalogue write can be diffed and verified against
        // them. Reported under the same names CatalogueTags uses, which is what
        // lets one desired-value map serve as both the plan's input and the
        // thing this reading is compared with.
        //
        // Zero is not a track number: ATL answers 0 rather than null for several
        // containers with no such tag, and stored as "0" it would make every
        // desired "1" look like a change on every pass.
        AddIfPresent(fields, "TRACKNUMBER", Count(track.TrackNumber));
        AddIfPresent(fields, "TRACKTOTAL", Count(track.TrackTotal));
        AddIfPresent(fields, "DISCNUMBER", Count(track.DiscNumber));
        AddIfPresent(fields, "DISCTOTAL", Count(track.DiscTotal));
        AddIfPresent(fields, "YEAR", Count(track.Year));

        var digests = track.EmbeddedPictures
            .Select(picture => TagSnapshot.Digest(picture.PictureData))
            .ToArray();

        return new TagSnapshot
        {
            AcoustId = acoustIdField is null ? null : Normalise(Lookup(track, acoustIdField)),
            Fields = fields,
            PictureCount = track.EmbeddedPictures.Count,
            PictureDigests = digests,
            DurationSeconds = track.Duration,
            BitrateKbps = track.Bitrate,
        };
    }

    private static void AddIfPresent(SortedDictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) fields[key] = value;
    }

    /// <summary>A count ATL actually holds, or null. Zero counts as null.</summary>
    private static string? Count(int? value) =>
        value is > 0 ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Trimmed and lowercased, so "the same AcoustID" is one string.</summary>
    internal static string? Normalise(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static string ExtensionOf(LibraryPath path)
    {
        var extension = Path.GetExtension(path.Value);
        return string.IsNullOrEmpty(extension) ? ".bin" : extension;
    }
}
