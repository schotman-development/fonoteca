using System.Text.Json.Serialization;

namespace Fonoteca.Tagging;

/// <summary>
/// The undo journal's payload for one write, however many fields it touched.
/// </summary>
/// <remarks>
/// The AcoustID pass writes one
/// field and the catalogue pass writes up to sixteen; one shape for both is
/// what lets the two share the write sequence.
///
/// The rules that shape it are unchanged, and both are load-bearing.
/// <see cref="TagFieldWrite.Previous"/> is nullable and stays that way — null
/// means the field was absent, so reversing means <i>removing</i> it, while an
/// empty string would mean it existed and was blank. And artwork is counted and
/// hashed, never carried: three megabytes of cover art times 7,735 rows is
/// roughly 23 GB of JSONB to record a handful of strings.
/// </remarks>
public sealed record TagWritePayload
{
    public required string Path { get; init; }

    /// <summary>Every field the write touched, in the order the plan lists them.</summary>
    public required IReadOnlyList<TagFieldWrite> Changes { get; init; }

    /// <summary>Why the write was refused or abandoned. Null on a committed write.</summary>
    public string? Reason { get; init; }

    public required int Pictures { get; init; }

    public required IReadOnlyList<string> PictureDigests { get; init; }

    /// <summary>Which library wrote, and which one confirmed it. Both, always (ADR 0002).</summary>
    public required string Writer { get; init; }

    /// <inheritdoc cref="Writer"/>
    public required string Verifier { get; init; }
}

/// <summary>One field, as this container spells it, and what became of it.</summary>
public sealed record TagFieldWrite
{
    public required string Field { get; init; }

    /// <summary>What the field held before. Null means it was not present.</summary>
    public required string? Previous { get; init; }

    /// <summary>What was written, or would have been.</summary>
    public required string? Written { get; init; }
}

/// <summary>
/// Source-generated, matching the providers. A tagging pass serialises one of
/// these per file across a library of 100,000.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(TagWritePayload))]
public sealed partial class TaggingJson : JsonSerializerContext;
