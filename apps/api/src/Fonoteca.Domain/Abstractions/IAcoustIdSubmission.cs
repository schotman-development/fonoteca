using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// "This audio is that recording" — told to AcoustID, rather than asked of it.
/// </summary>
/// <remarks>
/// Deliberately not a method on <see cref="IAcoustIdLookup"/>, and the
/// direction is the whole reason. A lookup is a question about one file that
/// costs a turn at the rate limit and nothing else. A submission is a claim
/// written into a database every other tagger in the world reads, it is
/// attributed to the operator's own account, and there is no call here that can
/// take it back. Two interfaces make that visible at every call site, and make
/// it possible to give something the ability to ask without the ability to
/// assert — which is what the three passes get.
///
/// <b>Nothing automatic may call this.</b> Identification decides what a file is
/// <i>from AcoustID's own answers</i>, so a pass that submitted its conclusions
/// would be feeding the provider its opinion back and then reading the echo as
/// corroboration — and the files where it would fire are exactly the ones no
/// rule could settle. Every submission this application makes is a person who
/// has read the pairing pressing a button.
/// </remarks>
public interface IAcoustIdSubmission
{
    /// <summary>
    /// Offers each fingerprint to AcoustID as evidence for a recording.
    /// </summary>
    /// <remarks>
    /// A batch, because their API takes one — <c>fingerprint.0</c>,
    /// <c>fingerprint.1</c> — so a thirteen-track album is one request and one
    /// turn at the gate rather than thirteen of each.
    /// </remarks>
    /// <returns>
    /// One receipt per accepted submission. AcoustID queues them and imports
    /// asynchronously, so every receipt says <c>pending</c>; nothing here waits
    /// for the import.
    /// </returns>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">A key or a fingerprint was refused.</exception>
    Task<IReadOnlyList<AcoustIdSubmissionReceipt>> SubmitAsync(
        IReadOnlyList<AcoustIdSubmissionItem> items,
        CancellationToken cancellationToken = default);
}

/// <summary>One "this fingerprint is this recording" claim.</summary>
/// <remarks>
/// The fingerprint and its duration travel together for
/// <see cref="AudioFingerprint"/>'s reason, and the recording rides with them
/// for the same one: the three are meaningless apart, and a shape that let a
/// caller pair file A's fingerprint with file B's recording would be a mismatch
/// nothing downstream — here or at AcoustID — could ever detect.
/// </remarks>
public readonly record struct AcoustIdSubmissionItem(AudioFingerprint Fingerprint, Mbid Recording);

/// <summary>AcoustID's acknowledgement of one queued submission.</summary>
/// <remarks>
/// Their response also carries an <c>index</c> back-referencing the item in the
/// batch, and it is deliberately not kept. Nothing here needs to know which file
/// became which submission: the caller stamps every file in the batch with the
/// same time, and the ids exist so a person can be told what was accepted and,
/// if it ever matters, look one up against <c>/v2/submission_status</c> by hand.
/// </remarks>
public sealed record AcoustIdSubmissionReceipt(long Id, string Status);
