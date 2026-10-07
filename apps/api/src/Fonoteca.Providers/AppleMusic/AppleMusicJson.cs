using System.Text.Json.Serialization;

namespace Fonoteca.Providers.AppleMusic;

/*
 * The wire shape of Apple's search and lookup API, and only the fields read.
 * Everything is nullable: a lookup answers tracks and artists as well as
 * collections, each with its own subset of fields.
 */

internal sealed record ITunesResults
{
    [JsonPropertyName("results")]
    public IReadOnlyList<ITunesResult>? Results { get; init; }
}

internal sealed record ITunesResult
{
    /// <summary>"collection" for a record; "track" and "artist" are the other answers.</summary>
    [JsonPropertyName("wrapperType")]
    public string? WrapperType { get; init; }

    [JsonPropertyName("collectionId")]
    public long? CollectionId { get; init; }

    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    /// <summary>An ISO timestamp, of which only the year is read.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }
}

[JsonSerializable(typeof(ITunesResults))]
internal sealed partial class AppleMusicJsonContext : JsonSerializerContext;
