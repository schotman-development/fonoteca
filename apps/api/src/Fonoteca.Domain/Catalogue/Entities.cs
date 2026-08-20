using Fonoteca.Domain.Abstractions;

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

    /// <summary>
    /// Live, Compilation, Remix, Soundtrack — comma-separated, because a group
    /// can carry several and none of them is worth its own table yet.
    /// </summary>
    /// <remarks>
    /// What tells a rip of an album from a rip of an anthology after the fact.
    /// Attribution does not gate on it — a compilation is a real release and
    /// somebody owns it — but a reviewer asking why forty tracks landed
    /// somewhere unexpected wants to see "Compilation" without another lookup.
    /// </remarks>
    public string? SecondaryTypes { get; set; }

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

    /// <summary>
    /// When this edition came out, to whatever precision MusicBrainz knows.
    /// </summary>
    /// <remarks>
    /// A <see cref="ReleaseDate"/> rather than a <see cref="DateOnly"/>, and the
    /// distinction is not pedantry: MusicBrainz dates a great many releases to a
    /// year alone, and a <c>DateOnly</c> column can hold none of them. Half the
    /// author's library is in that position — <i>Sloe Gin</i> 2007, <i>Royal
    /// Tea</i> 2020 — so the old column silently discarded the date for every one
    /// of them, and the alternative, widening 2007 to the 1st of January, invents
    /// a claim nobody made and then sorts a reissue ahead of the original on the
    /// strength of it.
    ///
    /// Stored as three plain columns and read back through here, which also
    /// makes the year something the database can sort and filter on — the one
    /// part of a release date anybody browsing albums actually asks for.
    /// </remarks>
    public ReleaseDate? Released
    {
        get => ReleasedYear is { } year ? new ReleaseDate(year, ReleasedMonth, ReleasedDay) : null;
        set
        {
            ReleasedYear = value?.Year;
            ReleasedMonth = value?.Month;
            ReleasedDay = value?.Day;
        }
    }

    /// <summary>The year, present whenever anything about the date is.</summary>
    public int? ReleasedYear { get; set; }

    /// <summary>The month, when MusicBrainz knows it. Null means unknown, never January.</summary>
    public int? ReleasedMonth { get; set; }

    public int? ReleasedDay { get; set; }

    /// <summary>Official, Promotion, Bootleg, Pseudo-Release.</summary>
    /// <remarks>
    /// The stated tie-break when two editions fit a set of files identically,
    /// and the first thing to look at when an attribution seems wrong.
    /// </remarks>
    public string? Status { get; set; }

    /// <summary>MusicBrainz's own note, where two editions share a title.</summary>
    public string? Disambiguation { get; set; }

    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string? Barcode { get; set; }

    /// <summary>Expected track count, for detecting an incomplete rip.</summary>
    public int? TrackCount { get; set; }

    /// <summary>How many discs, so a one-of-two rip is visible as one.</summary>
    public int? DiscCount { get; set; }

    /// <summary>
    /// CD, Digital Media, 12" Vinyl — joined with "+" for a mixed release.
    /// </summary>
    /// <remarks>
    /// Carried because it is the honest explanation for a partial rip: a
    /// <c>CD+DVD-Video</c> release is missing half its track list on any library
    /// that holds only the audio, and without the format that reads as damage.
    /// </remarks>
    public string? MediumFormats { get; set; }

    public ICollection<Track> Tracks { get; init; } = [];
    public ICollection<ArtistCredit> Credits { get; init; } = [];
    public ICollection<MediaFile> Files { get; init; } = [];
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

    /// <summary>
    /// The number as printed, which is not always the position: "A1", "12a".
    /// </summary>
    public string? Number { get; set; }

    /// <summary>Track title as printed on this release, which can differ from the recording's.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// How long this track runs <i>on this release</i>.
    /// </summary>
    /// <remarks>
    /// Not the same number as <see cref="Recording.Duration"/>, and the
    /// difference is the whole reason attribution can tell one edition from
    /// another. A recording has one canonical length; each release prints its
    /// own, and a remaster's differs from the original's by a second or two.
    /// Comparing a file's measured length against <i>this</i> is what picked the
    /// 2015 remaster of <i>Off the Wall</i> over three earlier pressings that
    /// covered the track list equally well.
    /// </remarks>
    public TimeSpan? Length { get; set; }
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

    /// <summary>
    /// AcoustID's whole answer about this file, kept rather than thrown away.
    /// </summary>
    /// <remarks>
    /// <b>The evidence, which the catalogue used to record no trace of.</b>
    /// Identification stored its verdict and discarded the clusters it reached
    /// it from, so working out what 951 withheld files actually were meant
    /// re-asking AcoustID about every one of them — 951 turns at the rate limit
    /// to recover something the application had already been told. This column
    /// is that mistake not being made twice.
    ///
    /// Written by whatever asked: the identification pass, the enrichment pass,
    /// and the candidates endpoint when it finds this stale. The shape is the
    /// provider's own answer, flattened — clusters, scores, and the recording
    /// MBIDs each one links to — and deliberately not our ranking of it.
    /// A ranking is a rule, rules change, and a cache of a rule's output would
    /// go quietly wrong the moment one did.
    ///
    /// Stored as JSONB and read back by <c>AcoustIdEvidence</c>. Roughly a
    /// kilobyte a file, which is a hundred megabytes across a library of
    /// 100,000 and less after TOAST compresses it — against a lookup budget of
    /// one request per 340ms, that is not a close trade.
    /// </remarks>
    public string? AcoustIdMatchesJson { get; set; }

    /// <summary>
    /// When <see cref="AcoustIdMatchesJson"/> was taken.
    /// </summary>
    /// <remarks>
    /// Beside the payload rather than derived from
    /// <see cref="AcoustIdCheckedUtc"/>, which answers a different question: that
    /// one says when a verdict was last reached, and a verdict can be reached
    /// from a cached answer. Reading freshness off it would let a week-old
    /// answer look like it arrived a moment ago.
    /// </remarks>
    public DateTimeOffset? AcoustIdMatchesUtc { get; set; }

    /// <summary>
    /// The rendered candidate set for this file — both providers' answers, assembled.
    /// </summary>
    /// <remarks>
    /// A second cache rather than a bigger first one, because the two are
    /// somebody else's answer and <i>our</i> assembly of two answers, and they
    /// go stale for different reasons. This one costs up to six MusicBrainz
    /// recording lookups to build — the heaviest request this application makes,
    /// measured at 10.3 seconds cold — so it is filled the first time somebody
    /// opens the question rather than by a pass: pre-building it for every
    /// refused file would put thousands of gated requests inside a run to save a
    /// wait nobody may ever have.
    ///
    /// Holds a serialised <c>RecordingCandidatesResponse</c>, which is the exact
    /// document the endpoint answers with. Storing the wire shape is what keeps
    /// a cache hit a single read and a deserialise.
    /// </remarks>
    public string? RecordingCandidatesJson { get; set; }

    /// <summary>When <see cref="RecordingCandidatesJson"/> was assembled.</summary>
    public DateTimeOffset? RecordingCandidatesUtc { get; set; }

    /// <summary>
    /// When a person settled this file's identity by hand, if one ever did.
    /// </summary>
    /// <remarks>
    /// <b>A guard, not a display column.</b> Every worklist in this application
    /// is a timestamp being null, and every one of them is deliberately
    /// re-openable: <c>AcoustIdCheckedUtc</c> and <c>RecordingLookupUtc</c> are
    /// cleared by hand after a rule changes, so a library can be re-asked
    /// without re-fingerprinting. That is the right behaviour for an answer a
    /// rule produced and the wrong one for an answer a person produced — a
    /// re-ask would quietly overwrite the decision with the same refusal the
    /// person was answering, and there would be no trace that it had.
    ///
    /// So the two passes exclude rows carrying this, in the query and in the
    /// partial index behind it. Clearing it is how a person changes their mind,
    /// and it has to be as deliberate as the decision was.
    ///
    /// Separate from <see cref="AcoustIdOutcome"/> even though the outcome
    /// already names the two person-made verdicts, for the reason
    /// <see cref="AcoustIdTaggedUtc"/> is separate from
    /// <see cref="AcoustIdCheckedUtc"/>: the outcome says what was concluded and
    /// this says who is entitled to overwrite it, and folding them together
    /// would make the guard a list of enum values that grows every time an
    /// outcome is added.
    /// </remarks>
    public DateTimeOffset? IdentityDecidedUtc { get; set; }

    /// <summary>
    /// When this file was last asked what recording it holds.
    /// </summary>
    /// <remarks>
    /// The enrichment worklist, and a timestamp rather than
    /// <c>RecordingId IS NULL</c> for exactly the reason
    /// <see cref="AcoustIdCheckedUtc"/> is not <c>AcoustId IS NULL</c>: a library
    /// contains audio AcoustID knows and MusicBrainz has since merged away, and
    /// keying the worklist on the answer re-asks about every one of them on every
    /// pass forever, at a third of a second each.
    /// </remarks>
    public DateTimeOffset? RecordingLookupUtc { get; set; }

    /// <summary>What the last enrichment attempt concluded. For display, not for the worklist.</summary>
    public EnrichmentOutcome EnrichmentOutcome { get; set; } = EnrichmentOutcome.NotAttempted;

    /// <summary>The edition this file came from, once one has been decided.</summary>
    public ReleaseId? ReleaseId { get; set; }
    public Release? Release { get; set; }

    /// <summary>Where on that edition — the disc, the position, the printed number.</summary>
    public TrackId? TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>
    /// The album, when the edition could not be settled but the album could.
    /// </summary>
    /// <remarks>
    /// Set on its own only for <see cref="ReleaseAttributionOutcome.GroupOnly"/>,
    /// where the editions that fitted disagreed about which disc and position the
    /// music sits at. Naming one of them would write a track number the evidence
    /// contradicts, so the specific claim is dropped and the general one kept.
    /// Otherwise it mirrors the chosen release's group, so "everything on this
    /// album" is one query whether or not the pressing is known.
    /// </remarks>
    public ReleaseGroupId? ReleaseGroupId { get; set; }
    public ReleaseGroup? ReleaseGroup { get; set; }

    /// <summary>
    /// When a release was last decided for this file, answer or not.
    /// </summary>
    /// <remarks>
    /// The worklist column, for the reason <see cref="AcoustIdCheckedUtc"/> is:
    /// selecting on a null <see cref="ReleaseId"/> would re-ask about every
    /// unattributable file on every pass, and the files that fit nothing are
    /// exactly the ones whose candidate sets are largest and slowest to fetch.
    /// </remarks>
    public DateTimeOffset? ReleaseLookupUtc { get; set; }

    /// <summary>What the last attribution attempt concluded, and how firmly.</summary>
    public ReleaseAttributionOutcome AttributionOutcome { get; set; } =
        ReleaseAttributionOutcome.NotAttempted;

    /// <summary>
    /// How many other editions fitted this file exactly as well as the one chosen.
    /// </summary>
    /// <remarks>
    /// Zero when the answer was unambiguous. Non-zero is not an error — it is a
    /// coin flip that has been recorded as one, which is the only way a reviewer
    /// can tell a decided answer from a defaulted one after the fact.
    /// </remarks>
    public int EditionAlternatives { get; set; }

    /// <summary>
    /// When a person settled this file's album by hand, if one ever did.
    /// </summary>
    /// <remarks>
    /// <see cref="IdentityDecidedUtc"/>'s counterpart, and it exists for exactly
    /// the same reason. Clearing <see cref="ReleaseLookupUtc"/> by hand is the
    /// documented way to re-attribute a library after a rule change, and that
    /// one UPDATE would otherwise hand every answered component straight back to
    /// the rule that could not answer it — overwriting the decision with the
    /// same refusal the person was answering, leaving no trace that it had.
    ///
    /// So the attribution pass excludes rows carrying this, in its worklist and
    /// in the partial index behind it. Clearing it is how somebody changes their
    /// mind, and it has to be as deliberate as the decision was.
    /// </remarks>
    public DateTimeOffset? ReleaseDecidedUtc { get; set; }

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

    /// <summary>
    /// A person read the candidate set and chose which recording this is.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Identified"/>, and the distinction is the point: that one
    /// means a rule cleared a score and a margin, and this one means it did not
    /// and somebody decided anyway. Reading them back as the same fact would
    /// lose the only thing that distinguishes an answer that can be recomputed
    /// from an answer that cannot — and would make a report of how well
    /// identification performs quietly count the files it failed on.
    ///
    /// Always accompanied by <see cref="MediaFile.IdentityDecidedUtc"/>, which
    /// is what actually protects the row.
    /// </remarks>
    IdentifiedByPerson = 6,

    /// <summary>
    /// A person read the candidate set and said none of it is this audio.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Unknown"/>, which is AcoustID never having heard
    /// the audio. Here AcoustID answered, offered recordings, and a person who
    /// listened to the file rejected all of them — which is a stronger claim
    /// than either the rule or the provider is in a position to make, and the
    /// only one that legitimately closes an <see cref="Ambiguous"/> or
    /// <see cref="BelowThreshold"/> question without an identity.
    /// </remarks>
    RejectedByPerson = 7,
}

/// <summary>
/// What enrichment concluded about a file.
/// </summary>
/// <remarks>
/// The sibling of <see cref="AcoustIdOutcome"/> and there for the same reason:
/// four ways of having no artist that invite four different follow-ups. "The
/// cluster names no recording" is a gap in AcoustID's links that submitting to
/// them would fix; "MusicBrainz has no such recording" is an MBID merged away
/// since AcoustID last saw it; "the lookup failed" is transient and will retry.
/// One null <c>RecordingId</c> answers none of those, and on a library where
/// most files resolve, the ones that do not are the only interesting rows.
/// </remarks>
public enum EnrichmentOutcome
{
    NotAttempted = 0,

    /// <summary>Linked to a recording, with its artists.</summary>
    Linked = 1,

    /// <summary>
    /// AcoustID knows the audio but links it to no MusicBrainz recording.
    /// </summary>
    /// <remarks>
    /// Ordinary rather than broken. An AcoustID cluster is a fingerprint
    /// grouping; whether anybody has connected it to MusicBrainz is a separate
    /// act of curation, and plenty of clusters are waiting for one.
    /// </remarks>
    NoRecording = 2,

    /// <summary>The recording MBID exists in AcoustID but no longer in MusicBrainz.</summary>
    RecordingNotFound = 3,

    /// <summary>A provider did not answer. Stays on the worklist.</summary>
    LookupFailed = 4,
}

/// <summary>What was decided about a file, and how firmly.</summary>
public enum ReleaseAttributionOutcome
{
    /// <summary>Nothing has asked yet.</summary>
    NotAttempted = 0,

    /// <summary>One release fitted, and nothing else fitted as well.</summary>
    Attributed = 1,

    /// <summary>
    /// Several editions fitted identically and agreed on where this track sits,
    /// so one was chosen by the stated tie-break and the rest counted.
    /// </summary>
    AttributedAmbiguously = 2,

    /// <summary>
    /// The album is known and the pressing is not, because the editions that
    /// fitted disagree about this track's disc or position.
    /// </summary>
    GroupOnly = 3,

    /// <summary>Releases existed, and none of them explained this file well enough.</summary>
    NoConfidentFit = 4,

    /// <summary>MusicBrainz holds no release containing this recording at all.</summary>
    NoCandidate = 5,

    /// <summary>The lookup did not answer. Transient; the file stays on the worklist.</summary>
    LookupFailed = 6,

    /// <summary>
    /// A person named the release, after the rule refused to.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Attributed"/>, for the reason
    /// <see cref="AcoustIdOutcome.IdentifiedByPerson"/> is not
    /// <see cref="AcoustIdOutcome.Identified"/>: one means a fit cleared the
    /// coverage floor and the drift gate, the other means it did not and
    /// somebody decided anyway. Folded together, any report of how attribution
    /// performs would quietly count the components it failed on.
    /// </remarks>
    AttributedByPerson = 7,

    /// <summary>
    /// A person said none of the candidate releases is the one these files came from.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoConfidentFit"/> in the same way
    /// <see cref="AcoustIdOutcome.RejectedByPerson"/> is from
    /// <see cref="AcoustIdOutcome.Unknown"/>: the rule declining to believe any
    /// candidate is weaker than a person who looked at them saying so. It closes
    /// the question, where the refusal leaves it open forever.
    /// </remarks>
    NoReleaseByPerson = 8,
}

/// <summary>
/// The candidate albums for one refused component, kept rather than thrown away.
/// </summary>
/// <remarks>
/// <b>The evidence half of an attribution refusal, and the mistake
/// <c>MediaFile.AcoustIdMatchesJson</c> already exists because of, one pass
/// along.</b> The attribution pass browses every recording in a component,
/// fetches the track lists worth fetching and ranks the lot — then records the
/// verdict and discards the candidates. Putting that question back to a person
/// therefore meant doing all of it again from nothing: measured against a live
/// mirror, 30 browses plus 8 lookups, up to two minutes and 38 turns at the rate
/// limit, for an answer the application had already computed.
///
/// <b>Its own table, because its key is not a file.</b> Attribution's unit is a
/// component — a set of files that share a candidate set and are decided together
/// — and the component's identity is the one <see cref="MediaFile.ReleaseLookupUtc"/>
/// its files share. There is no row for that anywhere else, and stamping the
/// same document onto every file in a 59-file component would store it 59 times.
///
/// <b>Written only where somebody may ask.</b> A component the pass filed
/// confidently is not a question, so it gets no row; the table's size is the
/// number of open album questions, which on the target library is 117.
/// </remarks>
public sealed class ReleaseCandidateSet
{
    /// <summary>
    /// The component: the <see cref="MediaFile.ReleaseLookupUtc"/> its files share.
    /// </summary>
    /// <remarks>
    /// The key, and stored to whole microseconds like every other timestamp here
    /// — the value is written by the pass and read back by an endpoint, and a
    /// tick PostgreSQL rounded away is a component nobody can look up.
    /// </remarks>
    public required DateTimeOffset ComponentUtc { get; init; }

    /// <summary>
    /// The assembled document, as the endpoint answers with it.
    /// </summary>
    /// <remarks>
    /// The wire shape verbatim, for the reason <see cref="MediaFile.RecordingCandidatesJson"/>
    /// stores one: a cache hit is then a read and a deserialise, with no second
    /// type to keep in step with the first.
    /// </remarks>
    public required string DocumentJson { get; set; }

    /// <summary>
    /// How many files were waiting on this component when the document was built.
    /// </summary>
    /// <remarks>
    /// The staleness check, and it catches what a timestamp cannot. A component
    /// is a <i>set</i>, and the set moves under the document: a scan clears
    /// <see cref="MediaFile.ReleaseLookupUtc"/> on a file whose bytes changed,
    /// and a person answering part of a component takes files out of it. Every
    /// coverage figure in the document is computed against the set that existed
    /// when it was written, so a different count means the numbers describe a
    /// component that no longer exists — a miss, not a failure.
    /// </remarks>
    public required int Files { get; set; }

    /// <summary>When MusicBrainz was asked. Mirrors the document's own <c>AsOfUtc</c>.</summary>
    public required DateTimeOffset GatheredUtc { get; set; }
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

    /// <summary>Billed credits — the printed credit line, with its order.</summary>
    public ICollection<ArtistCredit> Credits { get; init; } = [];

    /// <summary>
    /// Typed links: conductor, ensemble, composer, engineer.
    /// </summary>
    /// <remarks>
    /// The other half of "everything by this artist", and on a classical library
    /// the larger half — MusicBrainz bills a recording to its composer and puts
    /// everyone who played it here.
    /// </remarks>
    public ICollection<Relationship> Relationships { get; init; } = [];
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

    /*
     * Typed foreign keys beside the generic pair above.
     *
     * SourceType/SourceId and TargetType/TargetId are what make this table able
     * to link anything to anything; these are what make the common links
     * queryable. A join from Artists to Relationships cannot be written over a
     * bare Guid column, because the strongly-typed ids are value-converted and
     * EF translates no member access on them into SQL — so "every recording this
     * artist is linked to" would have to be evaluated on the client.
     *
     * One column per end that a browse query actually walks. Exactly one of
     * WorkId and RecordingId is set on the links this application writes, and
     * ArtistId is set on all of them.
     */

    public ArtistId? ArtistId { get; set; }
    public WorkId? WorkId { get; set; }
    public RecordingId? RecordingId { get; set; }
}
