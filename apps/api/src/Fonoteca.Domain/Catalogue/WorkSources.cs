namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Who linked a recording to its work (<see cref="Recording.WorkSource"/>)
/// where it was a person or an agent rather than MusicBrainz or Apple.
/// </summary>
/// <remarks>
/// <b>A person's link gives way to nothing</b> (rule 4): MusicBrainz's work
/// replaces Apple's piece, and a person's or an agent's is replaced only by
/// their next answer, or taken back, which hands the recording to MusicBrainz
/// and Apple again. Measured on 2026-11-03: of 96 held tracks of concert
/// pieces on classical albums that performed no work, MusicBrainz held the
/// works for 89 and linked none of those recordings to them, and Apple had
/// the piece for one album, missed by its lookup.
///
/// <b>Only a MusicBrainz work is named</b>, the owner's choice: the piece it is
/// part of, its place there and its composers come with it, as for a link
/// MusicBrainz states. A piece no source knows stays unnamed until it is
/// added to MusicBrainz.
/// </remarks>
public static class WorkSources
{
    /// <summary>A person's link, from a signed-in request.</summary>
    public const string Person = "person";

    /// <summary>An agent's link, through <c>/mcp</c>.</summary>
    public const string Agent = "agent";
}
