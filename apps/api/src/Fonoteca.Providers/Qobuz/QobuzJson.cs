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

    /// <summary>
    /// The barcode, which is the only key a provider-sourced release has.
    /// </summary>
    /// <remarks>
    /// ADR 0011's key: <c>Releases.Barcode</c> exists, is indexed and is
    /// deliberately not unique, so an album Qobuz names can be written without an
    /// MBID and later matched to a MusicBrainz release that shares the barcode —
    /// at which point the row gains its MBID without a file moving.
    ///
    /// Measured on <c>artist/get?extra=albums</c>: present on every row returned.
    /// Absent on some back-catalogue and digital-only titles, which is the
    /// honest failure — an unkeyed release a re-fetch duplicates — rather than
    /// inventing one.
    /// </remarks>
    [JsonPropertyName("upc")]
    public string? Upc { get; init; }
}

internal sealed record QobuzArtistBody
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>
    /// The artist's press photograph, in five sizes.
    /// </summary>
    /// <remarks>
    /// Absent on plenty of artists — Qobuz carry a picture for the people they
    /// sell records by and not for every credited session player, which is the
    /// same shape of gap Wikidata has and not the same artists.
    /// </remarks>
    [JsonPropertyName("image")]
    public QobuzImageBody? Image { get; init; }

    /// <summary>
    /// How many albums Qobuz carry by them.
    /// </summary>
    /// <remarks>
    /// The only fact in a search result that is about the artist rather than
    /// about the search, which is what makes it the one usable discriminator
    /// between two rows of the same name — see <c>QobuzPortraits.Picture</c>.
    /// Absent on an <c>artist/get</c>, which is why it is nullable.
    /// </remarks>
    [JsonPropertyName("albums_count")]
    public int? AlbumsCount { get; init; }

    /// <summary>
    /// What this artist has released, on an <c>artist/get?extra=albums</c>.
    /// </summary>
    /// <remarks>
    /// Absent on a search row and on a plain <c>artist/get</c>, hence nullable.
    /// The rows inside carry no <c>tracks</c> — measured — which the album mapper
    /// already tolerates, so one album shape serves both calls rather than two
    /// that could disagree about a field.
    /// </remarks>
    [JsonPropertyName("albums")]
    public QobuzAlbumListBody? Albums { get; init; }
}

internal sealed record QobuzArtistSearchBody
{
    [JsonPropertyName("artists")]
    public QobuzArtistListBody? Artists { get; init; }
}

internal sealed record QobuzArtistListBody
{
    [JsonPropertyName("items")]
    public IReadOnlyList<QobuzArtistBody>? Items { get; init; }
}

/// <summary>
/// A picture at several widths — an album's sleeve, or an artist's photograph.
/// </summary>
/// <remarks>
/// One record for both because Qobuz send the same shape for both, and the
/// squareness that makes it work for a sleeve is most of why the artist half is
/// worth having: Wikidata's P18 is "an image of the subject", which for a band
/// is very often a wide concert photograph — in a 112px circle that is a
/// stadium, with the band eight pixels of it. Qobuz's are cropped to the faces,
/// because they are the pictures on their own artist pages.
///
/// <c>extralarge</c> arrives for artists and is deliberately not read: that URL
/// 403s at their CDN. <c>large</c> is the biggest one that resolves, and for an
/// artist it is not a fixed size — see <c>SearchArtistsAsync</c>.
/// </remarks>
internal sealed record QobuzImageBody
{
    [JsonPropertyName("medium")]
    public string? Medium { get; init; }

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

    /// <summary>
    /// How many the service holds, against how many came back.
    /// </summary>
    /// <remarks>
    /// Worth reading rather than counting <see cref="Items"/>: one artist
    /// measured at <b>166</b> albums against a five-row page, so a caller that
    /// trusted the list length would report a discography as complete after
    /// seeing three percent of it. The same distinction
    /// <c>QobuzAlbum.TrackCount</c> already draws for a cut track list.
    /// </remarks>
    [JsonPropertyName("total")]
    public int? Total { get; init; }
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
[JsonSerializable(typeof(QobuzArtistSearchBody))]
[JsonSerializable(typeof(QobuzArtistBody))]
[JsonSerializable(typeof(QobuzFileUrlBody))]
[JsonSerializable(typeof(QobuzErrorBody))]
internal sealed partial class QobuzJsonContext : JsonSerializerContext;
