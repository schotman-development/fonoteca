using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which artists a recording should be browsable under.
/// </summary>
/// <remarks>
/// The rule behind "show me everything by this artist", and it cannot be
/// answered by the credit line alone. MusicBrainz bills a classical recording to
/// the <i>composer</i> — a Karajan reading of Beethoven's Fifth is credited
/// "Ludwig van Beethoven" — and puts the conductor and the orchestra in
/// relationships, while the composer link is one hop further out still, on the
/// work. A library that reads only <see cref="MusicBrainzRecording.Credits"/>
/// therefore files that recording under a man who died in 1827 and under nobody
/// else, and the orchestra that played it has no page at all.
///
/// So four sources, deliberately, and each is there for a case that breaks
/// without it:
///
/// <list type="bullet">
/// <item><b>The credit line.</b> "Beth Hart &amp; Joe Bonamassa" is two artists
/// and the track belongs to both of them. This is the only source that carries
/// billing order and the join phrase, so it is the only one that can reproduce
/// the printed credit.</item>
/// <item><b>The conductor</b>, and the chorus master beside them.</item>
/// <item><b>Ensembles</b> — orchestras and choirs. Recognised by the
/// <i>artist's</i> type rather than the relation's, because MusicBrainz links
/// the Berliner Philharmoniker with a plain <c>performer</c> relation about as
/// often as with <c>performing orchestra</c>, and a rule that reads only the
/// relation type finds one and misses the other.</item>
/// <item><b>The writers</b>, from the work: composer, writer, lyricist,
/// librettist.</item>
/// </list>
///
/// Everything else is dropped, and two exclusions are decisions rather than
/// omissions:
///
/// <list type="bullet">
/// <item><b>Individual performers are not included.</b> A jazz quintet's four
/// sidemen and a symphony's named soloists would each become an artist with one
/// or two tracks, and on a real library that is most of the artist list. The
/// data is fetched and stored; what this rule decides is who gets a page.
/// Widening it later is a query change, not a re-run.</item>
/// <item><b>Production roles are not credits.</b> Engineer, mix, producer,
/// mastering — the people who made the record rather than the music. Browsing by
/// them is a real question and a different one; folding them in here would put
/// Kevin Shirley in the artist list beside Beth Hart.</item>
/// </list>
///
/// Pure, and in the domain rather than in the enrichment service, for the same
/// reason <see cref="Identification.AcoustIdSelection"/> is: this decides what a
/// file becomes to somebody browsing, and it needs to be arguable and testable
/// without a network.
/// </remarks>
public static class PrimaryCredits
{
    /// <summary>Relations that make an artist a primary credit whatever they are.</summary>
    /// <remarks>
    /// <c>performing orchestra</c> is here as well as in the ensemble test
    /// below, because a relation that says "this orchestra performed" is an
    /// assertion by an editor and outranks whatever type the artist happens to
    /// carry — MusicBrainz has plenty of ensembles typed <c>Group</c>.
    /// </remarks>
    private static readonly HashSet<string> RecordingRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "conductor",
            "chorus master",
            "performing orchestra",
        };

    /// <summary>Relations on a work that name someone who wrote it.</summary>
    private static readonly HashSet<string> WorkRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "composer",
            "writer",
            "lyricist",
            "librettist",
        };

    /// <summary>Relations that only count when the artist is an ensemble.</summary>
    private static readonly HashSet<string> PerformanceRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "performer",
            "instrument",
            "vocal",
        };

    /// <summary>Artist types that make a performer an ensemble rather than a player.</summary>
    private static readonly HashSet<string> EnsembleTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Orchestra",
            "Choir",
        };

    /// <summary>
    /// The artists this recording should appear under, in a stable order.
    /// </summary>
    /// <param name="recording">The recording, with its relations.</param>
    /// <param name="work">
    /// Its work, when it has one and it has been fetched. Null is the ordinary
    /// case — most popular music has no work in MusicBrainz — and simply means no
    /// writers are contributed.
    /// </param>
    /// <remarks>
    /// One artist can arrive from several sources: a conductor is routinely also
    /// the billed credit, and a composer performing their own piece is all three.
    /// They collapse onto one entry per artist, keeping the <i>first</i> role in
    /// the ordering below — billed, then conductor, then ensemble, then writer —
    /// so the strongest claim is the one that shows.
    ///
    /// Ordering is a total function of the input: role, then billing position,
    /// then MBID. Nothing here may depend on dictionary iteration order, or a
    /// rerun over an unchanged library reshuffles every artist page.
    /// </remarks>
    public static IReadOnlyList<PrimaryCredit> From(
        MusicBrainzRecording recording,
        MusicBrainzWork? work = null)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var found = new List<PrimaryCredit>();

        for (var position = 0; position < recording.Credits.Count; position++)
        {
            var credit = recording.Credits[position];
            if (credit.ArtistId is not { } artistId) continue;

            found.Add(new PrimaryCredit(
                ArtistId: artistId,
                Name: credit.Name,
                SortName: credit.SortName,
                ArtistType: credit.ArtistType,
                Disambiguation: credit.Disambiguation,
                Role: CreditRole.Billed,
                Position: position,
                JoinPhrase: credit.JoinPhrase));
        }

        foreach (var relation in recording.Relations)
        {
            var role = RecordingRoleOf(relation);
            if (role is null) continue;

            found.Add(FromRelation(relation, role.Value));
        }

        foreach (var relation in work?.Relations ?? [])
        {
            if (!WorkRoles.Contains(relation.Type)) continue;

            found.Add(FromRelation(relation, CreditRole.Writer));
        }

        // Sorted before de-duplication, so which of an artist's several roles
        // survives is the strongest one rather than the first one parsed.
        found.Sort(static (left, right) =>
        {
            var byRole = left.Role.CompareTo(right.Role);
            if (byRole != 0) return byRole;

            var byPosition = left.Position.CompareTo(right.Position);
            return byPosition != 0 ? byPosition : left.ArtistId.Value.CompareTo(right.ArtistId.Value);
        });

        var seen = new HashSet<Mbid>();
        var credits = new List<PrimaryCredit>(found.Count);

        foreach (var credit in found)
        {
            if (seen.Add(credit.ArtistId)) credits.Add(credit);
        }

        return credits;
    }

    /// <summary>Which role, if any, this recording relation confers.</summary>
    private static CreditRole? RecordingRoleOf(MusicBrainzRelation relation)
    {
        if (relation.ArtistId is null) return null;

        if (RecordingRoles.Contains(relation.Type))
        {
            // "performing orchestra" names an ensemble; "conductor" and "chorus
            // master" name the person in front of it.
            return relation.Type.Equals("performing orchestra", StringComparison.OrdinalIgnoreCase)
                ? CreditRole.Ensemble
                : CreditRole.Conductor;
        }

        var ensemble = PerformanceRoles.Contains(relation.Type)
            && (IsEnsembleType(relation.ArtistType) || IsEnsembleType(relation.Attribute));

        return ensemble ? CreditRole.Ensemble : null;
    }

    /// <remarks>
    /// The attribute is consulted as well as the type because a <c>performer</c>
    /// relation qualified "orchestra" says the same thing about an artist
    /// MusicBrainz has typed <c>Group</c> — which most of them are.
    /// </remarks>
    private static bool IsEnsembleType(string? value) =>
        value is not null && EnsembleTypes.Contains(value);

    private static PrimaryCredit FromRelation(MusicBrainzRelation relation, CreditRole role) =>
        new(
            // Guarded by the callers: a relation with no artist confers no role.
            ArtistId: relation.ArtistId!.Value,
            Name: relation.Name,
            SortName: relation.SortName,
            ArtistType: relation.ArtistType,
            Disambiguation: relation.Disambiguation,
            Role: role,
            // Relations carry no billing order. They sort behind every billed
            // credit by role alone, so the position is only a tie-break among
            // themselves and a constant is the honest value.
            Position: 0,
            JoinPhrase: null);
}

/// <summary>One artist a recording should be browsable under, and why.</summary>
public sealed record PrimaryCredit(
    Mbid ArtistId,

    /// <summary>Name as credited here, which can differ from the artist's own.</summary>
    string Name,

    string? SortName,
    string? ArtistType,
    string? Disambiguation,

    CreditRole Role,

    /// <summary>Billing order for <see cref="CreditRole.Billed"/>, 0 otherwise.</summary>
    int Position,

    /// <summary>Text joining this credit to the next, for billed credits only.</summary>
    string? JoinPhrase);

/// <summary>
/// Why an artist is credited, strongest claim first.
/// </summary>
/// <remarks>
/// The numeric order is load-bearing — it is what
/// <see cref="PrimaryCredits.From"/> sorts on, and therefore which role survives
/// when one artist arrives from several sources. Reordering these members
/// reorders every artist page.
/// </remarks>
public enum CreditRole
{
    /// <summary>On the printed credit line, with billing order and join phrase.</summary>
    Billed = 0,

    /// <summary>Conductor, or chorus master.</summary>
    Conductor = 1,

    /// <summary>An orchestra or a choir.</summary>
    Ensemble = 2,

    /// <summary>Composer, writer, lyricist or librettist of the work.</summary>
    Writer = 3,
}
