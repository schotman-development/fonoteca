using System.Text.Json.Serialization;

namespace Fonoteca.Tagging;

/// <summary>
/// The undo journal's payload for one AcoustID write.
/// </summary>
/// <remarks>
/// Stored as JSONB in <c>DomainEvents.PayloadJson</c>, which is deliberately
/// opaque to the log itself so a new event type never needs a migration.
///
/// Two things about the shape are load-bearing:
///
/// <b><see cref="Previous"/> is nullable and stays that way.</b> Null means the
/// field was absent, so reversing the write means <i>removing</i> it; an empty
/// string would mean it existed and was blank, so reversing means restoring a
/// blank. Collapsing the two makes undo subtly wrong forever, and nothing would
/// ever notice.
///
/// <b>Artwork is counted and hashed, never carried.</b> Three megabytes of cover
/// art times 7,735 rows is roughly 23 GB of JSONB to record a 36-character
/// change. The digests prove the pictures survived; they never need to restore
/// them, because a write that lost artwork is aborted before it commits.
/// </remarks>
public sealed record AcoustIdWritePayload
{
    public required string Path { get; init; }

    /// <summary>The field name as this container spells it — see <see cref="AcoustIdTagField"/>.</summary>
    public required string Field { get; init; }

    /// <summary>What the field held before. Null means it was not present.</summary>
    public required string? Previous { get; init; }

    /// <summary>What was written, or would have been.</summary>
    public required string? Written { get; init; }

    /// <summary>Why the write was refused or abandoned. Null on a committed write.</summary>
    public string? Reason { get; init; }

    public required int Pictures { get; init; }

    public required IReadOnlyList<string> PictureDigests { get; init; }

    /// <summary>Which library wrote, and which one confirmed it. Both, always (ADR 0002).</summary>
    public required string Writer { get; init; }

    /// <inheritdoc cref="Writer"/>
    public required string Verifier { get; init; }
}

/// <summary>
/// Source-generated, matching the providers. A tagging pass serialises one of
/// these per file across a library of 100,000.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AcoustIdWritePayload))]
public sealed partial class TaggingJson : JsonSerializerContext;
