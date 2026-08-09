namespace Fonoteca.Domain.Catalogue;

/*
 * THE ENTITY GRAPH.
 *
 * Shaped like MusicBrainz, not like a filesystem. This is the one decision in
 * the whole scaffold that would otherwise force a rewrite later, so it is made
 * up front even though nothing populates it yet.
 *
 * A flat artist -> album -> track schema cannot answer "every recording of this
 * composition", "everything this engineer worked on", or "this performance
 * across seven releases" — and those questions are the entire difference
 * between a file browser and a library manager.
 *
 * Only half of this is forward-looking. The Recording <-> MediaFile split is
 * already mandatory for the dedupe requirement, because "the same recording in
 * five encodings" IS the core problem statement. Modelling the rest of the
 * graph while we are here is the cheap part.
 */

/// <summary>A composition: the abstract piece, independent of any performance.</summary>
/// <remarks>
/// Sparse by design. Most pop releases never populate this; classical and jazz
/// libraries live or die by it. Absence is normal, not an error.
/// </remarks>
public sealed class Work
{
    public required WorkId Id { get; init; }
    public required string Title { get; set; }
    public Mbid? Mbid { get; set; }

    /// <summary>e.g. "Symphony", "Song". Free text mirroring MusicBrainz work types.</summary>
    public string? Type { get; set; }

    public ICollection<Recording> Recordings { get; init; } = [];
    public ICollection<Relationship> Relationships { get; init; } = [];
}

/// <summary>
/// A specific captured performance. The dedupe anchor, and what AcoustID identifies.
/// </summary>
/// <remarks>
/// The pivot of the whole model. One recording may exist as many files (a FLAC
/// rip, a 320kbps MP3, a hi-res download) and appear on many releases (album,
/// compilation, remaster). Neither of those is a duplicate in any meaningful
/// sense — they are versions and appearances, which is precisely what a naive
/// schema cannot express.
/// </remarks>
public sealed class Recording
{
    public required RecordingId Id { get; init; }
    public required string Title { get; set; }
    public Mbid? Mbid { get; set; }

    /// <summary>Canonical length. Individual files may differ slightly across encodings.</summary>
    public TimeSpan? Duration { get; set; }

    public WorkId? WorkId { get; set; }
    public Work? Work { get; set; }

    /// <summary>Every file that holds this recording — the version set for dedupe.</summary>
    public ICollection<MediaFile> Files { get; init; } = [];

    /// <summary>Every release this recording appears on.</summary>
    public ICollection<Track> Tracks { get; init; } = [];

    public ICollection<ArtistCredit> Credits { get; init; } = [];
    public ICollection<Relationship> Relationships { get; init; } = [];
}

/// <summary>An album as an idea, across all its editions.</summary>
public sealed class ReleaseGroup
{
    public required ReleaseGroupId Id { get; init; }
    public required string Title { get; set; }
    public Mbid? Mbid { get; set; }

    /// <summary>Album, EP, Single, Compilation, Live, Soundtrack.</summary>
    public string? PrimaryType { get; set; }

    public int? FirstReleaseYear { get; set; }

    public ICollection<Release> Releases { get; init; } = [];
    public ICollection<ArtistCredit> Credits { get; init; } = [];
}

/// <summary>A specific published edition: this pressing, this remaster, this region.</summary>
public sealed class Release
{
    public required ReleaseId Id { get; init; }
    public required string Title { get; set; }
    public Mbid? Mbid { get; set; }

    public ReleaseGroupId? ReleaseGroupId { get; set; }
    public ReleaseGroup? ReleaseGroup { get; set; }

    public string? Country { get; set; }
    public DateOnly? ReleasedOn { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string? Barcode { get; set; }

    /// <summary>Expected track count, for detecting an incomplete rip.</summary>
    public int? TrackCount { get; set; }

    public ICollection<Track> Tracks { get; init; } = [];
    public ICollection<ArtistCredit> Credits { get; init; } = [];
}

/// <summary>A recording's position on a release. The join, with its own identity.</summary>
public sealed class Track
{
    public required TrackId Id { get; init; }

    public required ReleaseId ReleaseId { get; init; }
    public Release? Release { get; set; }

    public required RecordingId RecordingId { get; init; }
    public Recording? Recording { get; set; }

    public required int Position { get; set; }
    public int DiscNumber { get; set; } = 1;

    /// <summary>Track title as printed on this release, which can differ from the recording's.</summary>
    public string? Title { get; set; }
}

/// <summary>Bytes on disk. One recording may have many.</summary>
public sealed class MediaFile
{
    public required MediaFileId Id { get; init; }

    /// <summary>Library-relative path. Unique: one row per file.</summary>
    public required string Path { get; set; }

    public RecordingId? RecordingId { get; set; }
    public Recording? Recording { get; set; }

    public required long SizeBytes { get; set; }
    public required DateTimeOffset LastModifiedUtc { get; set; }

    /// <summary>
    /// Hash of the file's bytes. Detects "has this file changed" cheaply, and
    /// nothing more — re-tagging a file changes it, while the audio is identical.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Hash of the DECODED audio, ignoring tags and container.
    /// </summary>
    /// <remarks>
    /// This is what makes "same audio, different tags" and "re-encoded losslessly"
    /// recognisable as identical. For FLAC it is nearly free: the MD5 of the
    /// unencoded audio is already in the STREAMINFO header, so most of a library
    /// yields it without decoding a single frame.
    /// </remarks>
    public string? AudioHash { get; set; }

    /// <summary>Chromaprint fingerprint — cross-encoding identity, and the AcoustID lookup key.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// The span <see cref="Fingerprint"/> was computed over.
    /// </summary>
    /// <remarks>
    /// Stored with it, never apart from it. A lookup takes both — the fingerprint
    /// covers only the leading two minutes, so duration is what separates a track
    /// from a twelve-minute extended mix that opens identically — which means a
    /// fingerprint kept without its duration cannot be used for anything and the
    /// column above would be write-only.
    /// </remarks>
    public TimeSpan? FingerprintDuration { get; set; }

    /// <summary>The AcoustID cluster this file's audio belongs to, once one is known.</summary>
    public AcoustId? AcoustId { get; set; }

    /// <summary>
    /// When AcoustID was last asked about this file — set even when the answer
    /// was "never heard of it".
    /// </summary>
    /// <remarks>
    /// This, not <see cref="AcoustId"/>, is what "files with no AcoustID yet"
    /// means. Selecting on a null identifier would re-fingerprint and re-ask
    /// about every unidentifiable file on every pass, forever, at a third of a
    /// second each: a library's worth of bootlegs and field recordings that
    /// AcoustID will never know, asked again every time. Recording that we asked
    /// makes the worklist shrink to empty instead.
    ///
    /// Kept as a timestamp rather than a flag so the question can be reopened
    /// deliberately — AcoustID's database grows, and "re-ask about everything
    /// last checked before six months ago" stays one indexed query.
    /// </remarks>
    public DateTimeOffset? AcoustIdCheckedUtc { get; set; }

    /// <summary>
    /// When the AcoustID was last confirmed present in the file's own tags.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="AcoustIdCheckedUtc"/>. "We know what
    /// this is" and "the file says what it is" are different facts, and keeping
    /// them apart is what lets a run with <c>Fonoteca:AllowFileMutation</c> off
    /// be a complete dry run: it fills in everything expensive, leaves this null,
    /// and the run after the flag is flipped writes the tags without spending a
    /// single request.
    /// </remarks>
    public DateTimeOffset? AcoustIdTaggedUtc { get; set; }

    /// <summary>What the last identification attempt concluded. For display, not for the worklist.</summary>
    public AcoustIdOutcome AcoustIdOutcome { get; set; } = AcoustIdOutcome.NotAttempted;

    public AudioQuality? Quality { get; set; }

    public IntegrityState Integrity { get; set; } = IntegrityState.Unchecked;
    public DateTimeOffset? LastScannedUtc { get; set; }
    public DateTimeOffset? LastVerifiedUtc { get; set; }
}

/// <summary>
/// What identification concluded about a file.
/// </summary>
/// <remarks>
/// Not the worklist — <c>AcoustIdCheckedUtc IS NULL</c> is. This exists so the
/// four ways of not being identified can be told apart when reporting: "nobody
/// has ever submitted this audio" invites submitting it, "two clusters were too
/// close to call" invites a human look, "nothing matched well enough" invites
/// looking at the audio itself, and "the decoder refused the file" invites
/// checking whether the file is intact. One null column answers none of those.
/// </remarks>
public enum AcoustIdOutcome
{
    NotAttempted = 0,

    /// <summary>A cluster cleared both the score threshold and the margin.</summary>
    Identified = 1,

    /// <summary>AcoustID has never heard this audio.</summary>
    Unknown = 2,

    /// <summary>Two clusters meant different audio and neither won clearly.</summary>
    Ambiguous = 3,

    /// <summary>The decoder could not produce a fingerprint at all.</summary>
    Unfingerprintable = 4,

    /// <summary>
    /// Something matched, but too weakly to write into a file.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Ambiguous"/>, which used to mean both and so
    /// meant neither — and hid the proportions badly. When this was separated,
    /// what had been reported as 951 ambiguous files turned out to be 925
    /// clustering artefacts and 23 genuinely weak matches, which need opposite
    /// follow-ups: the first has a right answer a rule declined to pick, the
    /// second has no answer worth having. Old, noisy or sparsely-submitted audio
    /// lands here, and no margin will help it.
    ///
    /// Numbered after <see cref="Unfingerprintable"/> rather than beside its
    /// sibling because the column stores the number. Rows written before this
    /// existed still read <see cref="Ambiguous"/> until they are asked again.
    /// </remarks>
    BelowThreshold = 5,
}

/// <summary>Result of decode-testing a file.</summary>
public enum IntegrityState
{
    Unchecked = 0,

    /// <summary>Decoded end to end with no errors.</summary>
    Intact = 1,

    /// <summary>Decoder reported errors — bad frames, truncation, CRC mismatch.</summary>
    Corrupt = 2,

    /// <summary>Shorter than its own header claims.</summary>
    Truncated = 3,

    /// <summary>Unreadable, or an unsupported container.</summary>
    Unreadable = 4,
}

/// <summary>A performer or other credited party.</summary>
public sealed class Artist
{
    public required ArtistId Id { get; init; }
    public required string Name { get; set; }

    /// <summary>"Beatles, The" — for sorting, distinct from display name.</summary>
    public string? SortName { get; set; }

    public Mbid? Mbid { get; set; }

    /// <summary>Person, Group, Orchestra, Choir.</summary>
    public string? Type { get; set; }

    public string? Disambiguation { get; set; }

    public ICollection<ArtistCredit> Credits { get; init; } = [];
}

/// <summary>
/// An artist's credit on something, with an ordered position and a join phrase.
/// </summary>
/// <remarks>
/// Not a plain many-to-many. "Miles Davis feat. John Coltrane" has an order and
/// the literal word "feat." between the names — collapsing that into a string
/// on the parent loses the ability to navigate to either artist, and collapsing
/// it into an unordered link loses the billing.
/// </remarks>
public sealed class ArtistCredit
{
    public required Guid Id { get; init; }

    public required ArtistId ArtistId { get; init; }
    public Artist? Artist { get; set; }

    /// <summary>Which entity this credits. Exactly one of these is set.</summary>
    public RecordingId? RecordingId { get; set; }

    public ReleaseId? ReleaseId { get; set; }
    public ReleaseGroupId? ReleaseGroupId { get; set; }

    /// <summary>Billing order, 0-based.</summary>
    public required int Position { get; set; }

    /// <summary>Text joining this credit to the next: " feat. ", " &amp; ", ", ".</summary>
    public string? JoinPhrase { get; set; }

    /// <summary>Name as credited here, when it differs from the artist's canonical name.</summary>
    public string? CreditedAs { get; set; }
}

/// <summary>
/// A typed link between two entities.
/// </summary>
/// <remarks>
/// This is what makes the non-performer credits queryable: composer, conductor,
/// engineer, producer, remixer, "is a cover of", "was sampled by". Without it,
/// that information can only live in a free-text tag, which is to say it cannot
/// be asked questions.
/// </remarks>
public sealed class Relationship
{
    public required Guid Id { get; init; }

    public required string SourceType { get; init; }
    public required Guid SourceId { get; init; }
    public required string TargetType { get; init; }
    public required Guid TargetId { get; init; }

    /// <summary>e.g. "composer", "conductor", "engineer", "cover-of".</summary>
    public required string Type { get; init; }

    /// <summary>Instrument or role qualifier, e.g. "trumpet", "assistant".</summary>
    public string? Attribute { get; set; }

    public WorkId? WorkId { get; set; }
    public RecordingId? RecordingId { get; set; }
}
