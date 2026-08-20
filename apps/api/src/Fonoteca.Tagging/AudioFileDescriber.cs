using ATL;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using AtlTrack = ATL.Track;
using TagLibFile = TagLib.File;

namespace Fonoteca.Tagging;

/// <summary>
/// Asks a file what it is, for somebody who has to decide what it is.
/// </summary>
/// <remarks>
/// <b>The read that answers "which of these recordings is this file", rather
/// than the read that authorises a write.</b> Everything else in this project
/// opens an audio file to decide something — <see cref="TagReader"/> to find an
/// AcoustID, <see cref="AcoustIdTagWriter"/> to verify one it just wrote — and
/// each of those is allowed to fail loudly, because a wrong answer becomes a
/// wrong byte. Nothing is decided from this one. It is printed.
///
/// That difference is the whole design:
///
/// <b>It never throws.</b> Not for a missing file, an unmounted volume, a
/// truncated FLAC or a parser that dereferences null. The manual matching screen
/// exists precisely because these files are the awkward ones, and a screen that
/// 500s on the file you opened it for is worse than one that says "the decoder
/// would not read this" — which is itself a useful thing to learn about a file
/// nothing could identify.
///
/// <b>The numbers come from a decoder and the words come from a tag library,
/// and they are separate reads on purpose.</b> Neither tag library is qualified
/// to measure audio: asked about a VBR MP3 with no Xing header one of them
/// answers 64 kbps and 5:35 where the audio is 128 kbps and 2:58, and asked
/// about a FLAC truncated to a fifth of its bytes it reports the original
/// duration off an intact STREAMINFO and computes 3 kbps against what is left.
/// Those numbers are written into <see cref="AudioQuality"/>, which decides
/// which duplicate to keep. So <see cref="IAudioProbe"/> measures and the tag
/// libraries are asked only what they are good at. See that interface for the
/// measurements.
///
/// <b>TagLib# reads the tags, not ATL.</b> Reversed from the write path,
/// because TagLib# resolves the same fact across containers through named
/// accessors — <c>ALBUMARTIST</c> in a Vorbis comment, <c>TPE2</c> in ID3v2,
/// <c>aART</c> in MP4 — and because it is the library that does <i>not</i> fall
/// over on the forty FLACs in the target library carrying a prepended,
/// unsynchronised ID3v2 header. Those are disproportionately the files a person
/// ends up looking at here.
///
/// <b>ATL is still opened, third and optionally, for its
/// <c>AdditionalFields</c>.</b> That is where <c>MUSICBRAINZ_TRACKID</c>,
/// <c>ACOUSTID_ID</c>, <c>ISRC</c> and every other non-standard key live, and on
/// a library somebody has tagged before, one of those settles the question
/// outright — a recording MBID in the file is a better answer than any score on
/// the screen. Its failure costs the extra fields and nothing else.
///
/// <b>Values are truncated and the list is capped.</b> Embedded lyrics and
/// base64 cover art both live in ordinary tag fields, and this response goes to
/// a browser.
/// </remarks>
public sealed class AudioFileDescriber(IAudioFileStore files, IAudioProbe probe)
{
    private readonly IAudioFileStore _files =
        files ?? throw new ArgumentNullException(nameof(files));

    private readonly IAudioProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>Longest tag value returned. Lyrics and base64 artwork live in tag fields.</summary>
    private const int MaxValueLength = 300;

    /// <summary>Most tags returned. Past this it is a data dump rather than evidence.</summary>
    private const int MaxTags = 60;

    /// <summary>
    /// Fields carried whatever they are called in the container.
    /// </summary>
    /// <remarks>
    /// Read through TagLib#'s named accessors rather than out of a raw field map
    /// so that one spelling serves every container: the same fact is
    /// <c>ALBUMARTIST</c> in a Vorbis comment, <c>TPE2</c> in ID3v2 and
    /// <c>aART</c> in MP4, and a person matching a file should not have to know
    /// which of the three they are looking at.
    /// </remarks>
    private static readonly (string Name, Func<TagLib.Tag, string?> Read)[] Named =
    [
        ("TITLE", tag => tag.Title),
        ("ARTIST", tag => Join(tag.Performers)),
        ("ALBUM", tag => tag.Album),
        ("ALBUMARTIST", tag => Join(tag.AlbumArtists)),
        ("TRACK", tag => Position(tag.Track, tag.TrackCount)),
        ("DISC", tag => Position(tag.Disc, tag.DiscCount)),
        ("DATE", tag => tag.Year == 0 ? null : tag.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("GENRE", tag => Join(tag.Genres)),
        ("COMPOSER", tag => Join(tag.Composers)),
        ("CONDUCTOR", tag => tag.Conductor),
        ("PUBLISHER", tag => tag.Publisher),
        ("ISRC", tag => tag.ISRC),

        // The four that can end the question on their own. A file that already
        // names a MusicBrainz recording does not need a fingerprint scored
        // against six candidates — it needs somebody to notice the tag.
        ("MUSICBRAINZ_TRACKID", tag => tag.MusicBrainzTrackId),
        ("MUSICBRAINZ_ALBUMID", tag => tag.MusicBrainzReleaseId),
        ("MUSICBRAINZ_RELEASEGROUPID", tag => tag.MusicBrainzReleaseGroupId),
        ("MUSICBRAINZ_ARTISTID", tag => tag.MusicBrainzArtistId),

        ("COMMENT", tag => tag.Comment),
    ];

    /// <summary>What this file says about itself, as much of it as survives.</summary>
    public async Task<AudioFileReading> DescribeAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default)
    {
        var tags = new List<TagValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AudioProbeReading? measurement = null;

        // Two independent reads, so two independent failures — a container the
        // decoder refuses may still have readable tags, and a tag parser that
        // dies says nothing about the audio. Joined rather than collapsed,
        // because "which half failed" is the useful part.
        var notes = new List<string>(2);

        try
        {
            measurement = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);

            if (measurement is null)
            {
                notes.Add("No audio stream could be found in this file.");
            }
            else if (measurement.Complaint is { } complaint)
            {
                notes.Add($"The decoder read this stream and complained: {complaint}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The contract is "never throws", and it has to mean it.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            // Not just AudioProbeFailedException, and the difference is the
            // whole promise. A path that resolves outside the library root, a
            // container declaring an absurd duration, output the deserialiser
            // cannot map — none of those is the probe's own exception type, and
            // every one of them would 500 the single endpoint documented as
            // unable to fail on the file, for exactly the class of broken file
            // this screen exists for.
            notes.Add($"The audio could not be measured: {Reason(cause)}");
        }

        try
        {
            var stream = await _files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var file = TagLibFile.Create(new StreamFileAbstraction(path.Value, stream));

                if (file.Tag is { } tag)
                {
                    foreach (var (name, read) in Named)
                    {
                        Add(tags, seen, name, Value(read, tag));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Documented above: nothing is decided from this read.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            notes.Add($"The tags could not be read: {Reason(cause)}");
        }

        try
        {
            var stream = await _files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var track = new AtlTrack(stream, ExtensionOf(path));

                foreach (var (key, value) in
                    track.AdditionalFields.OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    Add(tags, seen, key, value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Same contract: the extra fields are optional.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Deliberately silent. The named fields above are the ones a person
            // reads; a second sentence about a second library refusing the same
            // unreadable file is one fact reported twice, and on a file whose
            // standard tags came back fine it would report a gap nobody can see.
        }

        return new AudioFileReading
        {
            Quality = measurement?.Quality,
            Duration = measurement?.Quality.Duration,
            DecodedCleanly = measurement?.DecodedCleanly ?? false,
            Tags = tags,
            Note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    private static void Add(List<TagValue> tags, HashSet<string> seen, string name, string? value)
    {
        if (tags.Count >= MaxTags) return;
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!seen.Add(name)) return;

        tags.Add(new TagValue(name, Truncate(value.Trim(), MaxValueLength)));
    }

    /// <summary>
    /// One accessor's answer, or nothing if it throws.
    /// </summary>
    /// <remarks>
    /// Per field rather than around the whole loop: TagLib#'s named accessors
    /// parse on access, so one malformed frame otherwise costs every field after
    /// it in the list.
    /// </remarks>
    private static string? Value(Func<TagLib.Tag, string?> read, TagLib.Tag tag)
    {
        try
        {
            return read(tag);
        }
#pragma warning disable CA1031 // A field that will not parse is a field this does not print.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static string? Join(string[]? values) =>
        values is { Length: > 0 }
            ? string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value)))
            : null;

    /// <summary>"7" alone, or "7 of 12" when the container states the total.</summary>
    private static string? Position(uint number, uint total) => (number, total) switch
    {
        (0, _) => null,
        (var n, 0) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        var (n, t) => $"{n} of {t}",
    };

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";

    /// <summary>The exception's own sentence, short enough to print in a row.</summary>
    private static string Reason(Exception cause) =>
        Truncate(cause.Message.ReplaceLineEndings(" ").Trim(), 160);

    private static string ExtensionOf(LibraryPath path)
    {
        var extension = Path.GetExtension(path.Value);
        return string.IsNullOrEmpty(extension) ? ".bin" : extension;
    }
}
