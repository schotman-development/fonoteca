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

    /// <summary>
    /// Somebody wants this record, whether or not the library holds it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Artist.Followed"/>'s counterpart one level down, and the
    /// second fact in this catalogue that is not derived from anything.</b> A
    /// person sets it, nothing can recompute it, and a rescan must never touch
    /// it.
    ///
    /// <b>It is a filter, not an instruction.</b> Nothing searches for a
    /// monitored record, nothing buys one, and no pass reads this column —
    /// acquisition here is still a person who searched, read a track list and
    /// pressed a button, which is the standing rule in <c>CLAUDE.md</c> and on
    /// the acquire screen itself. What it changes is which records that screen
    /// is willing to show: the shelf of gaps grows with every artist followed,
    /// and past a few dozen a list of everything they never released is not a
    /// list anybody reads.
    ///
    /// <b>Default false, and that decides the whole feature.</b> The discography
    /// browse writes a followed artist's entire back catalogue at once, so
    /// defaulting this true would make the shelf exactly as long as it is
    /// without the column — the work merely inverted from choosing what to want
    /// into dismissing what you do not. So the first browse is a baseline that
    /// monitors nothing, and <c>EnrichmentService.FetchDiscographyAsync</c>
    /// monitors what turns up on a <i>later</i> browse: records that appeared
    /// after somebody said they cared. Marking anything older is a deliberate
    /// press on the artist page.
    /// </remarks>
    public bool Monitored { get; set; }

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

    /// <summary>
    /// When this file's fingerprint was last contributed <i>to</i> AcoustID.
    /// </summary>
    /// <remarks>
    /// The other direction from every other column here, and the only one
    /// recording something this application said rather than something it was
    /// told. It is written when a person submits the fingerprint bound to the
    /// recording MBID they filed the file under — never by a pass, for the
    /// reason <see cref="Fonoteca.Domain.Abstractions.IAcoustIdSubmission"/>
    /// states.
    ///
    /// It exists to keep the offer from repeating. Eligibility is "a person
    /// chose this file's recording", which is <see cref="IdentityDecidedUtc"/>
    /// being set — and that does not change when a submission is accepted,
    /// because the import happens out of band and nothing here polls for it.
    /// Without this column the same thirteen fingerprints are offered again on
    /// every visit to the album, and the count beside the button never falls.
    ///
    /// A resubmission is harmless at their end and this does not make it
    /// impossible, only unprompted: clearing the column is how somebody submits
    /// again, in the same spirit as the two decided stamps.
    /// </remarks>
    public DateTimeOffset? AcoustIdSubmittedUtc { get; set; }

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

    /// <summary>
    /// A person said this audio was never released, so nothing will ever place it.
    /// </summary>
    /// <remarks>
    /// The answer to a <i>folder</i> rather than to a file: somebody's own
    /// compilation — tracks pulled off YouTube, a mixtape, a rip of a set that
    /// was never issued as an album or a single. AcoustID has never heard it
    /// because nobody submitted it and nobody is going to, so
    /// <see cref="Unknown"/> is a question that stays open forever and re-asking
    /// spends a turn at the rate limit to be told the same thing again.
    ///
    /// Not <see cref="RejectedByPerson"/>, which is somebody who listened
    /// rejecting the candidates AcoustID <i>did</i> offer. Folding the two would
    /// make any count of "files a person listened to and turned down" quietly
    /// include a folder dismissed in one click, and this is the coarser claim by
    /// a wide margin.
    ///
    /// Written across all three outcomes at once, because the claim answers all
    /// three: no release, therefore no track, therefore nothing to identify
    /// against. <see cref="MediaFile.IdentityDecidedUtc"/> is what protects it.
    /// </remarks>
    Unreleased = 8,

    /// <summary>
    /// A person looked at what a pass decided and said it was wrong.
    /// </summary>
    /// <remarks>
    /// <b>The one refusal nothing refused.</b> Every other value here is a rule
    /// or a person answering an open question; this is a person <i>reopening</i>
    /// a closed one. It exists because a confident wrong answer is invisible:
    /// the files are identified, linked and filed, so they are on no worklist
    /// and no screen asks about them — a live album whose tracks AcoustID
    /// matched to the studio recordings of the same songs reads as a finished
    /// album until somebody plays it.
    ///
    /// <b>It is an open question, so it is in the worklist's own set.</b> That
    /// is the whole point: the folder comes back as something a person can
    /// answer by hand, which is the only thing that can answer it — the audio is
    /// genuinely not in AcoustID, or is in it under the wrong link, and neither
    /// is fixed by asking again.
    ///
    /// <b>No pass may answer it, and the mechanism is the stamps rather than
    /// this value.</b> Reopening deliberately leaves
    /// <see cref="MediaFile.AcoustIdCheckedUtc"/>,
    /// <see cref="MediaFile.RecordingLookupUtc"/> and
    /// <see cref="MediaFile.ReleaseLookupUtc"/> exactly as they were, because
    /// they record that the providers <i>were asked</i> — which stays true, and
    /// is what keeps all three passes off the file. Clearing them would hand the
    /// folder straight back to the rule that got it wrong, on the next run, with
    /// the same evidence and therefore the same answer.
    ///
    /// Not <see cref="RejectedByPerson"/>: that is somebody who read the
    /// candidates and turned them all down, and it is <i>settled</i>. This is
    /// unsettled by definition, and folding the two would make a folder waiting
    /// for an album read as one that has been dealt with.
    /// </remarks>
    ReopenedByPerson = 9,

    /// <summary>
    /// An agent chose which recording this is, through <c>/mcp</c>, on the owner's approval.
    /// </summary>
    /// <remarks>
    /// Not <see cref="IdentifiedByPerson"/>. The owner approved a tool call; nobody
    /// listened to the file, and "you decided this" and "an agent decided this"
    /// want different amounts of suspicion. Every by-a-person value has an agent
    /// twin for that reason, written by the same endpoint when the caller is
    /// <c>AgentCallerContext</c> — it is a claim about who decided, where the
    /// by-a-person values were a claim about whether a rule did.
    /// </remarks>
    IdentifiedByAgent = 10,

    /// <summary>An agent rejected every candidate. See <see cref="IdentifiedByAgent"/>.</summary>
    RejectedByAgent = 11,

    /// <summary>An agent said this folder was never released. See <see cref="IdentifiedByAgent"/>.</summary>
    UnreleasedByAgent = 12,

    /// <summary>
    /// An agent reopened a folder a pass decided. See <see cref="IdentifiedByAgent"/>.
    /// </summary>
    /// <remarks>An open question, like <see cref="ReopenedByPerson"/>, and in the worklist's set for the same reason.</remarks>
    ReopenedByAgent = 13,
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

    /// <summary>
    /// A person named the recording, after the passes could not.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Linked"/>, and the gap between them is real rather than
    /// bookkeeping: <c>Linked</c> promises "linked to a recording, <i>with its
    /// artists</i>", written by a pass that fetched the recording and ran
    /// <c>PrimaryCredits</c> over it. A person filing files under an album gets
    /// the recording's identity out of the release's track list and no artist
    /// graph at all — one release lookup covers thirty files, where enriching
    /// them properly is thirty recording lookups at the rate limit. So the
    /// catalogue knows what these files are and cannot yet say who played on
    /// them, and this is the value that says so.
    ///
    /// It also has to exist for the worklist to empty. The queue is
    /// <c>UnidentifiedOutcomes OR UnlinkedOutcomes</c>, so a file left at
    /// <see cref="NoRecording"/> keeps its place however confidently its identity
    /// was decided.
    /// </remarks>
    LinkedByPerson = 5,

    /// <summary>
    /// A person said this audio was never released. See
    /// <see cref="AcoustIdOutcome.Unreleased"/>, which is written with it.
    /// </summary>
    /// <remarks>
    /// The enrichment leg exists for the reason <see cref="LinkedByPerson"/>
    /// does: the queue is <c>UnidentifiedOutcomes OR UnlinkedOutcomes</c>, so a
    /// file left at <see cref="NoRecording"/> keeps its place on the worklist
    /// however firmly the folder it sits in was dismissed.
    /// </remarks>
    Unreleased = 6,

    /// <summary>
    /// An agent filed this file under an album. See <see cref="AcoustIdOutcome.IdentifiedByAgent"/>.
    /// </summary>
    /// <remarks>On the second enrichment worklist beside <see cref="LinkedByPerson"/>, which it has the same gap as.</remarks>
    LinkedByAgent = 7,

    /// <summary>An agent said this audio was never released. See <see cref="AcoustIdOutcome.IdentifiedByAgent"/>.</summary>
    UnreleasedByAgent = 8,
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

    /// <summary>
    /// A person said these files came from no release at all. See
    /// <see cref="AcoustIdOutcome.Unreleased"/>, which is written with it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="NoReleaseByPerson"/>: that is somebody reading a shortlist
    /// of real candidate editions and saying none of them is the one — a
    /// judgement about a component the pass formed. This is somebody saying no
    /// such edition exists anywhere, about a folder, without a candidate set
    /// having been offered at all.
    /// </remarks>
    Unreleased = 9,

    /// <summary>An agent named the release. See <see cref="AcoustIdOutcome.IdentifiedByAgent"/>.</summary>
    AttributedByAgent = 10,

    /// <summary>An agent said none of the candidate releases fits. See <see cref="AcoustIdOutcome.IdentifiedByAgent"/>.</summary>
    NoReleaseByAgent = 11,

    /// <summary>An agent said these files came from no release. See <see cref="AcoustIdOutcome.IdentifiedByAgent"/>.</summary>
    UnreleasedByAgent = 12,
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

/// <summary>
/// The picture shown for one release, kept rather than fetched on every view.
/// </summary>
/// <remarks>
/// <b>A row with no bytes is an answer:</b> the archive was asked and holds no
/// front cover. Without it every page showing that album would ask again. A
/// row is only written when the archive answered, so an outage retries.
/// </remarks>
public sealed class ReleaseCover
{
    public required ReleaseId ReleaseId { get; init; }

    public byte[]? Bytes { get; set; }

    public string? MediaType { get; set; }

    /// <summary>
    /// Which archive image this is. Null beside bytes means a person uploaded it.
    /// </summary>
    public long? ArchiveImageId { get; set; }

    /// <summary>When the picture last changed; the ETag it is served under.</summary>
    public required DateTimeOffset SavedUtc { get; set; }
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

    /// <summary>
    /// What to print when <see cref="Name"/> is not in Latin script.
    /// </summary>
    /// <remarks>
    /// MusicBrainz's own English alias, chosen by <see cref="LatinNames.Of"/>,
    /// and null for every artist whose name is already Latin — which is 3,029
    /// of this library's 3,051. Every reader resolves
    /// <c>LatinName ?? Name</c>, so a null is "their own name is fine" rather
    /// than a gap somebody has to fill.
    ///
    /// <b>Beside <see cref="Name"/> rather than over it.</b> Overwriting would
    /// have cost no read sites at all and was the tempting version; what it
    /// throws away is the one thing the catalogue is supposed to be — a record
    /// of what MusicBrainz says. It would also reach the tag writer, which
    /// would then rewrite a Japanese pressing's <c>ARTIST</c> frame on a
    /// display preference. A column is SQL-translatable, so the projections
    /// that could not call a helper can read <c>LatinName ?? Name</c> anyway.
    /// </remarks>
    public string? LatinName { get; set; }

    public Mbid? Mbid { get; set; }

    /// <summary>Person, Group, Orchestra, Choir.</summary>
    public string? Type { get; set; }

    public string? Disambiguation { get; set; }

    /// <summary>ISO 3166-1 code of the country MusicBrainz primarily associates them with.</summary>
    public string? Country { get; set; }

    /// <summary>Male, female, non-binary. Set on people, absent on groups.</summary>
    public string? Gender { get; set; }

    /// <summary>Born, or formed.</summary>
    public int? BeganYear { get; set; }

    /// <summary>Died, or dissolved.</summary>
    public int? EndedYear { get; set; }

    /// <summary>
    /// Whether MusicBrainz says the life span is over.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EndedYear"/> because it is a separate claim. A
    /// band everybody knows split up but nobody has dated carries the flag with
    /// no year; a page that reads the year alone reports them as still going.
    /// </remarks>
    public bool Ended { get; set; }

    /// <summary>
    /// MusicBrainz's curated genres, most-voted first, joined with ", ".
    /// </summary>
    /// <remarks>
    /// A delimited string rather than a table, which is
    /// <see cref="ReleaseGroup.SecondaryTypes"/>'s bargain and taken for the same
    /// reason: nothing queries them yet, and a join table for a field that is
    /// only ever printed is four files nobody reads. Faceting the artist list by
    /// genre is when this becomes a table.
    /// </remarks>
    public string? Genres { get; set; }

    /// <summary>
    /// When MusicBrainz was last asked about this artist.
    /// </summary>
    /// <remarks>
    /// <b>The worklist, and it is keyed on the asking rather than on the
    /// answer</b> — the same lesson <c>AcoustIdCheckedUtc</c>,
    /// <c>RecordingLookupUtc</c> and <c>ReleaseLookupUtc</c> each paid for
    /// separately. Keyed on <c>Country IS NULL</c> instead, every artist
    /// MusicBrainz holds no country for — every orchestra, every "Various
    /// Artists", every one-line credit for somebody's uncle — is re-asked about
    /// on every run forever, and the worklist never reaches empty.
    ///
    /// Set when the answer is "no such artist" too, which is also an answer.
    /// Left null only when the lookup did not happen: a transient failure keeps
    /// the row on the worklist, exactly as a failed enrichment keeps a file.
    /// </remarks>
    public DateTimeOffset? LookupUtc { get; set; }

    /// <summary>
    /// A picture of the artist, on somebody else's CDN.
    /// </summary>
    /// <remarks>
    /// Wikidata's <c>P18</c>, as a Wikimedia Commons <c>Special:FilePath</c>
    /// URL — the one thing in this catalogue that is neither a fact about the
    /// music nor a path on this disk. A URL rather than an image because the
    /// browser fetches it directly, the way it already fetches sleeves from the
    /// Cover Art Archive.
    ///
    /// Null is the ordinary answer, and how ordinary depends on who is being
    /// asked about: measured, 26% of the artists an album is billed to have no
    /// photograph anywhere, against <b>52% of the whole catalogue</b> — the
    /// second number being every session player, songwriter and small ensemble
    /// that a credit line drags in. The card falls back to an album and then to
    /// its monogram.
    /// </remarks>
    public string? PortraitUrl { get; set; }

    /// <summary>
    /// When a picture was last looked for.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on the asking, and this is the fifth time.</b>
    /// <c>AcoustIdCheckedUtc</c>, <c>RecordingLookupUtc</c>,
    /// <c>ReleaseLookupUtc</c> and <see cref="LookupUtc"/> each paid for this
    /// separately. Keyed on <see cref="PortraitUrl"/> being null instead, the
    /// 73 artists in 307 that Wikidata holds no image for are asked about on
    /// every run forever and the worklist never empties.
    ///
    /// Separate from <see cref="LookupUtc"/> rather than folded into it, because
    /// they are two different services answering two different questions — and
    /// because the artists this catalogue already holds were all described
    /// before pictures existed. Sharing the stamp would mean re-asking
    /// MusicBrainz about 2,902 artists, at a turn each, to find out what they
    /// look like.
    /// </remarks>
    public DateTimeOffset? PortraitLookupUtc { get; set; }

    /// <summary>
    /// Somebody said they care about this artist, whatever the library holds.
    /// </summary>
    /// <remarks>
    /// <b>The one fact here that is not derived from anything.</b> Every other
    /// column on this row is an answer — from a credit line, from MusicBrainz,
    /// from Wikidata — and every artist in the catalogue is a byproduct of a
    /// file. This is the opposite: it comes from a person, nothing can
    /// recompute it, and a rescan of the whole library must never touch it.
    ///
    /// It is deliberately unrelated to what is held. An artist with two hundred
    /// tracks may be unfollowed — a session player a credit line dragged in —
    /// and a followed artist may have no files at all, which is the case the
    /// feature exists for and the one that breaks things: an artist with no
    /// recordings is dropped from the browse list, so following somebody new
    /// looks like it did nothing. <c>CatalogueEndpoints.GetArtists</c> keeps a
    /// followed artist whatever their track count.
    /// </remarks>
    public bool Followed { get; set; }

    /// <summary>
    /// When MusicBrainz was last asked what this artist has released.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on the asking, and this is the sixth time.</b>
    /// <c>AcoustIdCheckedUtc</c>, <c>RecordingLookupUtc</c>,
    /// <c>ReleaseLookupUtc</c>, <see cref="LookupUtc"/> and
    /// <see cref="PortraitLookupUtc"/> each paid for this separately. Keyed on
    /// "has this artist any credited release groups" instead, every artist
    /// MusicBrainz lists nothing for — and every one whose discography is
    /// genuinely empty — is browsed for again on every run forever.
    ///
    /// Separate from <see cref="Followed"/> rather than cleared by it, so that
    /// unfollowing and re-following does not spend the browse again, and so
    /// that the discography already fetched survives. Re-asking is the
    /// hand-written <c>UPDATE</c> this codebase already documents for
    /// <c>AcoustIdCheckedUtc</c>.
    ///
    /// Set when the answer is "nothing" too. Left null only when the lookup did
    /// not happen, so a transient outage retries and a real answer does not.
    /// </remarks>
    public DateTimeOffset? DiscographyLookupUtc { get; set; }

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
