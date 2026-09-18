namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Which of an artist's release groups count as missing from the library, and
/// which are noise.
/// </summary>
/// <remarks>
/// <b>The browse answers a different question from the one a person asks.</b>
/// MusicBrainz lists every release group credited to an artist — and for anyone
/// with a long career that is overwhelmingly not albums. A prolific artist's
/// browse comes back with the studio records somewhere inside a list of
/// greatest-hits packages assembled by licensees, live bootlegs, tribute
/// compilations they appear on for one track, remix EPs and karaoke editions.
/// Shown raw, "not in your library" is a list nobody can act on and the feature
/// is worse than nothing.
///
/// So the browse is stored whole and the cut is made here, at read time. That is
/// deliberate and it is this codebase's existing bargain, stated in
/// <c>CLAUDE.md</c> as <i>what is cached is answers, never rankings</i>: the
/// release groups are facts MusicBrainz stated, this is a rule about them, and a
/// rule that changes must not require re-asking the provider for every artist in
/// the catalogue at a turn each.
///
/// Everything here is pure — a description of a release group in, a decision
/// out. The caller has already established that no file in the library sits
/// under this group; the only question left is whether that absence is worth
/// printing.
/// </remarks>
public static class Discography
{
    /// <summary>
    /// True when not owning this release group is worth telling somebody about.
    /// </summary>
    /// <param name="group">
    /// What MusicBrainz says this release group is. The caller has already
    /// decided the library holds nothing under it.
    /// </param>
    /// <remarks>
    /// <b>The error to prefer is the one a person can see.</b> A studio album
    /// wrongly hidden is a gap nobody is ever offered and nothing on the screen
    /// says it was withheld; a compilation wrongly shown is one obviously silly
    /// row. That asymmetry is the same one <see cref="UpgradeScan"/> settles in
    /// its own words about <c>.m4a</c> — an unnecessary row costs a glance, a
    /// missing one costs the feature.
    /// </remarks>
    public static bool IsGap(ReleaseGroupFacts group)
    {
        // TODO(you): the rule. See the notes on ReleaseGroupFacts for exactly
        // what MusicBrainz puts in each field, and the remarks above for which
        // way to err.
        //
        // The questions worth deciding, roughly in order of how much they
        // change the list:
        //
        //   - PrimaryType. "Album" is the obvious keep. Is an EP a gap? A
        //     Single? Broadcast and Other are usually radio sessions and odds
        //     and ends. Null means MusicBrainz has not typed it at all, which
        //     is commoner on obscure artists than you would like — so refusing
        //     everything untyped quietly hides exactly the catalogue nobody
        //     else has curated.
        //
        //   - SecondaryTypes. This is where the noise lives: "Compilation",
        //     "Live", "Soundtrack", "Remix", "DJ-mix", "Demo", "Interview",
        //     "Mixtape/Street", "Audiobook", "Spokenword". A group can carry
        //     several at once. Note a record can be PrimaryType "Album" AND
        //     secondary "Compilation" — that is precisely the greatest-hits
        //     package, and it is the single biggest source of rows you do not
        //     want.
        //
        //   - Live is the genuinely contentious one. For most artists a live
        //     album is a real record they made on purpose; for a heavily
        //     bootlegged one it is fifty rows of the same tour.
        //
        //   - FirstReleaseYear is null on unreleased and announced records.
        //     Worth showing, or worth waiting until it has a date?
        //
        // Return true to show the row, false to hide it.
        //
        // What follows is a placeholder so the screen runs, NOT a considered
        // answer. Replace the body; the tests in DiscographyTests are written
        // against whatever you decide.
        if (group.SecondaryTypes.Any(Noise.Contains)) return false;

        return group.PrimaryType is null or "Album" or "EP";
    }

    /// <summary>
    /// Secondary types that are somebody else's record rather than a gap in
    /// yours. Placeholder — see <see cref="IsGap"/>.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compilation", "Live", "Remix", "DJ-mix", "Demo", "Interview", "Audiobook", "Spokenword",
    };
}

/// <summary>
/// What MusicBrainz states about one release group, as the browse returns it.
/// </summary>
/// <param name="Title">The group's title. Never empty.</param>
/// <param name="PrimaryType">
/// <c>Album</c>, <c>EP</c>, <c>Single</c>, <c>Broadcast</c>, <c>Other</c> — or
/// null, which means nobody has typed it rather than that it is none of them.
/// </param>
/// <param name="SecondaryTypes">
/// Zero or more of <c>Compilation</c>, <c>Live</c>, <c>Soundtrack</c>,
/// <c>Remix</c>, <c>DJ-mix</c>, <c>Demo</c> and friends. Independent of
/// <paramref name="PrimaryType"/> — an anthology is typically both
/// <c>Album</c> and <c>Compilation</c>.
/// </param>
/// <param name="FirstReleaseYear">
/// The year of the earliest release in the group, or null when MusicBrainz
/// holds no date — an unreleased or newly announced record.
/// </param>
public readonly record struct ReleaseGroupFacts(
    string Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int? FirstReleaseYear);
