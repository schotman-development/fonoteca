using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Api.Matching;

/// <summary>
/// AcoustID's answer about one file, on its way to and from a column.
/// </summary>
/// <remarks>
/// <b>A storage shape rather than the domain type, and that is the point of the
/// file.</b> <see cref="AcoustIdMatch"/> holds an <c>Mbid</c>, which is a
/// <c>readonly record struct</c> over a <c>Guid</c>; serialised directly it
/// becomes <c>{"value":"…"}</c>, and every future reader of this column would
/// have to know that. Flattening it here costs one mapping in each direction and
/// leaves a document somebody can read in psql.
///
/// It is deliberately the provider's answer and <b>not</b> our ranking of it.
/// <c>RecordingCandidates</c> and <c>AcoustIdSelection</c> collapse this in two
/// different directions and both have changed since they were written — caching
/// either one's output would be caching a rule, and a rule that changes leaves
/// its cache quietly wrong with nothing to notice it. Clusters, scores and the
/// links between them are facts; what to do with them is not.
/// </remarks>
public sealed record AcoustIdEvidence
{
    public required IReadOnlyList<AcoustIdCluster> Clusters { get; init; }

    /// <summary>
    /// The stored document.
    /// </summary>
    /// <remarks>
    /// An empty answer serialises to an empty cluster list rather than to
    /// nothing: "we asked and AcoustID knows this audio under no cluster" is a
    /// fact worth a week of not asking again, and the caller decides whether it
    /// has one to record. Nothing here is nullable, so a column left null means
    /// nobody asked.
    /// </remarks>
    public static string Serialise(IReadOnlyList<AcoustIdMatch> matches)
    {
        var evidence = new AcoustIdEvidence
        {
            Clusters =
            [
                .. matches.Select(match => new AcoustIdCluster
                {
                    AcoustId = match.AcoustId,
                    Score = match.Score,
                    Recordings =
                    [
                        .. match.Recordings.Select(link => new AcoustIdLink
                        {
                            Mbid = link.Id.Value,
                            Sources = link.Sources,
                        }),
                    ],
                }),
            ],
        };

        return JsonSerializer.Serialize(evidence, MatchingJson.Default.AcoustIdEvidence);
    }

    /// <summary>
    /// The stored document as the domain sees it, or null if it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null on malformed JSON rather than an exception, and the caller treats
    /// that as a cache miss. A cache is not allowed to be able to break the
    /// thing it makes faster — the worst a corrupt entry may cost is the request
    /// it was there to save.
    /// </remarks>
    public static IReadOnlyList<AcoustIdMatch>? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        AcoustIdEvidence? stored;

        try
        {
            stored = JsonSerializer.Deserialize(json, MatchingJson.Default.AcoustIdEvidence);
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null) return null;

        return
        [
            .. stored.Clusters.Select(cluster => new AcoustIdMatch(
                cluster.AcoustId,
                cluster.Score,
                [
                    .. cluster.Recordings.Select(link =>
                        new AcoustIdRecordingRef(new Mbid(link.Mbid), link.Sources)),
                ])),
        ];
    }
}

/// <summary>One AcoustID cluster, as stored.</summary>
public sealed record AcoustIdCluster
{
    public required Guid AcoustId { get; init; }

    public required double Score { get; init; }

    public required IReadOnlyList<AcoustIdLink> Recordings { get; init; }
}

/// <summary>One recording a cluster is linked to, as stored.</summary>
public sealed record AcoustIdLink
{
    public required Guid Mbid { get; init; }

    /// <summary>How many submissions back this link. A score without it is half the evidence.</summary>
    public required int Sources { get; init; }
}
