using System.Text.Json.Serialization;

namespace Fonoteca.Providers.Qobuz;

/*
 * The wire shape of the Qobuz JSON API, and nothing else. Same split as
 * AcoustIdJson: this file answers "what does Qobuz send", the records at the
 * bottom of QobuzClient answer "what does a download need".
 *
 * Everything is nullable because everything is optional in practice. A Qobuz
 * album carries an `artist` on some responses and only a `performer` on others,
 * a single-track release omits `media_count`, and an unstreamable region omits
 * the whole `tracks` block rather than sending an empty one.
 */

internal sealed record QobuzAlbumBody
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("artist")]
    public QobuzArtistBody? Artist { get; init; }

    [JsonPropertyName("release_date_original")]
    public string? ReleaseDate { get; init; }

    /// <summary>How many discs. Absent on a single-disc release.</summary>
    [JsonPropertyName("media_count")]
    public int? MediaCount { get; init; }

    [JsonPropertyName("tracks_count")]
    public int? TracksCount { get; init; }

    /// <summary>Whether the account may stream it at all — region and subscription tier.</summary>
    [JsonPropertyName("streamable")]
    public bool? Streamable { get; init; }

    [JsonPropertyName("hires")]
    public bool? HiRes { get; init; }

    [JsonPropertyName("maximum_bit_depth")]
    public int? MaximumBitDepth { get; init; }

    [JsonPropertyName("maximum_sampling_rate")]
    public double? MaximumSamplingRate { get; init; }

    [JsonPropertyName("image")]
    public QobuzImageBody? Image { get; init; }

    [JsonPropertyName("tracks")]
    public QobuzTrackListBody? Tracks { get; init; }
}

internal sealed record QobuzArtistBody
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record QobuzImageBody
{
    [JsonPropertyName("large")]
    public string? Large { get; init; }
}

internal sealed record QobuzTrackListBody
{
    [JsonPropertyName("items")]
    public IReadOnlyList<QobuzTrackBody>? Items { get; init; }
}

internal sealed record QobuzTrackBody
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("track_number")]
    public int TrackNumber { get; init; }

    [JsonPropertyName("media_number")]
    public int? MediaNumber { get; init; }

    [JsonPropertyName("duration")]
    public int? Duration { get; init; }

    /// <summary>The track's own credit, which on a compilation is not the album's.</summary>
    [JsonPropertyName("performer")]
    public QobuzArtistBody? Performer { get; init; }

    [JsonPropertyName("streamable")]
    public bool? Streamable { get; init; }
}

internal sealed record QobuzSearchBody
{
    [JsonPropertyName("albums")]
    public QobuzAlbumListBody? Albums { get; init; }
}

internal sealed record QobuzAlbumListBody
{
    [JsonPropertyName("items")]
    public IReadOnlyList<QobuzAlbumBody>? Items { get; init; }
}

/// <summary>The answer to <c>track/getFileUrl</c>: a signed, time-limited CDN URL.</summary>
internal sealed record QobuzFileUrlBody
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("format_id")]
    public int? FormatId { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("bit_depth")]
    public int? BitDepth { get; init; }

    [JsonPropertyName("sampling_rate")]
    public double? SamplingRate { get; init; }

    /// <summary>
    /// Present and true when the account may not have this encoding.
    /// </summary>
    /// <remarks>
    /// The trap this exists to catch: Qobuz answer a refused request with
    /// <b>HTTP 200</b> and a body carrying no <c>url</c> — or, worse, a
    /// thirty-second preview. Trusting the status code alone downloads a
    /// library of samples.
    /// </remarks>
    [JsonPropertyName("restrictions")]
    public IReadOnlyList<QobuzRestrictionBody>? Restrictions { get; init; }

    /// <summary>Set when what came back is a preview rather than the track.</summary>
    [JsonPropertyName("sample")]
    public bool? Sample { get; init; }
}

internal sealed record QobuzRestrictionBody
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }
}

/// <summary>Their error envelope, which arrives with a non-2xx status.</summary>
internal sealed record QobuzErrorBody
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(
    // Qobuz are inconsistent about quoting numbers — `duration` and the format
    // ids come back quoted on some endpoints and bare on others. This lets a
    // numeric property read either; it is not a defence against a property
    // changing its type, which still throws and is reported as a bad response.
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(QobuzAlbumBody))]
[JsonSerializable(typeof(QobuzSearchBody))]
[JsonSerializable(typeof(QobuzFileUrlBody))]
[JsonSerializable(typeof(QobuzErrorBody))]
internal sealed partial class QobuzJsonContext : JsonSerializerContext;
