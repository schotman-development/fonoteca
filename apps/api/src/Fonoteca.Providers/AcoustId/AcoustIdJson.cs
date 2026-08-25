using System.Text.Json.Serialization;

namespace Fonoteca.Providers.AcoustId;

/*
 * The wire shape of https://api.acoustid.org/v2/lookup, and nothing else.
 *
 * Kept separate from the domain records these become because the two answer
 * different questions. This file answers "what does AcoustID send", which is
 * their decision and can change under us; Fonoteca.Domain.Abstractions answers
 * "what does identification need", which is ours. Parsing straight into the
 * domain types would fuse them, and the first schema change would then be a
 * change to the catalogue's vocabulary.
 *
 * Every property is nullable and every list is optional, because they are: a
 * result with no MusicBrainz links omits `recordings` entirely rather than
 * sending an empty array.
 */

internal sealed record AcoustIdResponse
{
    /// <summary>"ok" or "error".</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error")]
    public AcoustIdErrorBody? Error { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<AcoustIdResultBody>? Results { get; init; }
}

internal sealed record AcoustIdErrorBody
{
    /// <summary>Their own error numbering. 4 is "invalid API key", 5 is a fault on their side.</summary>
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed record AcoustIdResultBody
{
    /// <summary>The AcoustID track cluster id.</summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("recordings")]
    public IReadOnlyList<AcoustIdRecordingBody>? Recordings { get; init; }
}

internal sealed record AcoustIdRecordingBody
{
    /// <summary>The MusicBrainz recording MBID.</summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; init; }

    /// <summary>Submissions linking this cluster to this recording. Present only with <c>meta=sources</c>.</summary>
    [JsonPropertyName("sources")]
    public int Sources { get; init; }
}

/// <summary>
/// The wire shape of <c>/v2/submit</c>, which shares only its error document.
/// </summary>
/// <remarks>
/// Their acknowledgement, not their verdict. A submission comes back
/// <c>pending</c> and is imported out of band, so the ids here are the only
/// handle on it afterwards — nothing in this response says whether the claim
/// was believed, and <c>/v2/submission_status</c> is where that would be asked.
/// </remarks>
internal sealed record AcoustIdSubmitResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error")]
    public AcoustIdErrorBody? Error { get; init; }

    [JsonPropertyName("submissions")]
    public IReadOnlyList<AcoustIdSubmissionBody>? Submissions { get; init; }
}

internal sealed record AcoustIdSubmissionBody
{
    /// <summary>Their submission id. A long, not a Guid — this is a queue position.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>"pending" on the way in; "imported" once it has been processed.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
/// Source-generated serialisation for the shapes above.
/// </summary>
/// <remarks>
/// A generated reader rather than the reflection-based one. Identification runs
/// once per file over a library of 100,000, so the per-call reflection cost is
/// paid 100,000 times — and it keeps the door open to publishing this trimmed
/// or ahead-of-time compiled later without having to find every
/// <c>JsonSerializer</c> call first.
/// </remarks>
[JsonSourceGenerationOptions(
    // Their error responses put `status` after `error`, and one endpoint's
    // examples show keys in a different order again. Nothing here depends on
    // order, but numbers arriving as strings would be a silent null.
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AcoustIdResponse))]
[JsonSerializable(typeof(AcoustIdSubmitResponse))]
internal sealed partial class AcoustIdJsonContext : JsonSerializerContext;
