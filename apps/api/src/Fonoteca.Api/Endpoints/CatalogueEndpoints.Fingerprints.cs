using System.Linq.Expressions;
using System.Text.Json;
using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Giving a fingerprint back to AcoustID, once a person has said what it is.
/// </summary>
/// <remarks>
/// <b>Every other provider call in this application is a question. This is the
/// only answer.</b> Which is why it is one endpoint, reached by one button, and
/// why nothing in <c>Fonoteca.Jobs</c>, no <c>BackgroundService</c> and none of
/// the three passes may ever call it — see
/// <see cref="IAcoustIdSubmission"/> for the argument. A claim written into a
/// database the whole world reads is not the kind of work that should be able
/// to start on a timer.
///
/// It is the last step of a path whose earlier steps already exist, and it is
/// short because of that. Two kinds of file reach it, and they are unidentified
/// for opposite reasons. A live recording nobody has ever fingerprinted comes
/// back <see cref="AcoustIdOutcome.Unknown"/> — that outcome's own remarks say
/// the state "invites submitting it" — and stays unidentifiable however often it
/// is asked, because the answer is not in AcoustID to find. A file AcoustID
/// recognises but MusicBrainz links to nothing comes back
/// <see cref="EnrichmentOutcome.NoRecording"/>, and is the larger group by far.
/// Both are missing the same thing, which is the <i>link</i>, and both are
/// answered the same way — by a person entering the release in MusicBrainz,
/// filing the files under it here, and then offering the audio back:
///
/// <list type="number">
/// <item>the person adds the show to MusicBrainz themselves — Fonoteca writes
/// nothing there, and that edit needs their account and their judgement;</item>
/// <item>they paste the release MBID into the match dialog and seat the files,
/// which is <c>FileFilesUnderRelease</c> and gives every file a
/// <see cref="Recording"/> read off the slot, and an
/// <see cref="MediaFile.IdentityDecidedUtc"/> stamp saying a person chose
/// it;</item>
/// <item>they press this, which sends each file's stored fingerprint bound to
/// that recording.</item>
/// </list>
///
/// So the request needs no body and takes no candidates: the pairing was
/// decided in step 2 and written down, and this endpoint's whole job is to
/// repeat it to somebody else. What it must never do is invent one.
///
/// <b>It does not close the loop for the files it submits, and that is not a
/// gap.</b> Those files already carry the recording the person chose — the
/// stamp that made them eligible is the same one that settles them; the
/// contribution is for the next library to rip this show, and for this one if
/// the audio is ever replaced. AcoustID imports out of band and nothing here
/// waits or polls, so the receipts all read <c>pending</c>.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>Event type for a person contributing fingerprints back to AcoustID.</summary>
    private const string FingerprintsSubmittedEventType = "acoustid.fingerprints.submitted";

    /// <summary>What the event log calls the album whose fingerprints went out.</summary>
    private const string FingerprintSubject = "acoustid-contribution";

    /// <summary>
    /// A file worth offering to AcoustID.
    /// </summary>
    /// <remarks>
    /// <b>The condition is that a person decided the recording, not that the
    /// audio is unknown.</b> That distinction is the whole rule and the first
    /// draft of it got it backwards, so it is worth stating plainly: what a
    /// submission carries is <c>fingerprint + mbid</c>, and its content is the
    /// <i>link</i> between them. Whether AcoustID has heard the audio is a
    /// different question from whether it holds that link.
    ///
    /// Keying on <c>AcoustId IS NULL</c> — "AcoustID has never heard this" —
    /// therefore excludes the case where contributing is worth most. A file
    /// AcoustID recognises perfectly well but MusicBrainz links to no recording
    /// is <see cref="EnrichmentOutcome.NoRecording"/>, which is <b>454 of the
    /// 697 open files</b> on the library this was built against: they carry a
    /// cluster, that cluster names nothing, and a person has just supplied the
    /// name. That is precisely news. It is also what Picard does — it offers a
    /// submission whenever the chosen recording is not already linked to the
    /// file's AcoustID, not only for audio nobody has fingerprinted.
    ///
    /// <see cref="MediaFile.IdentityDecidedUtc"/> is the honest expression of
    /// it, and it is the same column both person-decisions already write. It
    /// also enforces the rule <see cref="IAcoustIdSubmission"/> states: a pass
    /// derived its recording <i>from</i> AcoustID's own answer, so submitting
    /// one back would be feeding the provider its opinion and reading the echo
    /// as a second source. Only a claim a person made is worth making.
    ///
    /// The rest are what the claim is made of — a recording MBID to name, a
    /// fingerprint to name it with — and
    /// <see cref="MediaFile.AcoustIdSubmittedUtc"/>, which stops the offer
    /// repeating, since nothing above it changes when a submission is accepted.
    /// A rejection (<see cref="AcoustIdOutcome.RejectedByPerson"/>) falls out
    /// on the recording being null: somebody who listened and refused every
    /// candidate has told us there is nothing to assert.
    ///
    /// <b>Some of what this selects, AcoustID already holds.</b> Where a person
    /// answered through the recording chooser, the cluster written was the one
    /// naming their choice — so the link exists and the submission is a
    /// duplicate. Telling that apart means reading the cached cluster evidence
    /// per row and asking whether any of it names the chosen recording, which is
    /// a JSON document rather than a column. It is not worth it: AcoustID
    /// discards a duplicate claim, the cost is one row in a batch, and the
    /// alternative is a predicate the screen cannot explain in a sentence.
    /// ponytail: duplicates tolerated; read AcoustIdMatchesJson if it ever costs
    /// something measurable.
    ///
    /// One expression, used by the count on the album page and by the submit
    /// itself, because two copies of this would drift and the visible symptom
    /// would be a button offering a number it then does not send.
    /// </remarks>
    private static readonly Expression<Func<MediaFile, bool>> Contributable =
        file => file.IdentityDecidedUtc != null
            && file.AcoustIdSubmittedUtc == null
            && file.Fingerprint != null
            && file.Fingerprint != ""
            && file.FingerprintDuration > TimeSpan.Zero
            && file.Recording!.Mbid != null;

    private static void MapFingerprintEndpoints(IEndpointRouteBuilder group)
    {
        group.MapPost("/releases/{id:guid}/fingerprints", ContributeFingerprints)
            .WithName("ContributeFingerprints")
            .WithSummary("Send this album's unknown fingerprints to AcoustID.")
            .WithDescription(
                "For files whose recording a person chose by hand, which is the only kind worth "
                + "sending: a pass took its answer from AcoustID in the first place. Submits each "
                + "stored fingerprint bound to that recording, so the link becomes everyone's. "
                + "Never automatic — nothing in Fonoteca submits anything unless somebody presses "
                + "this. Nothing on disk is touched and no tag is written.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Ok<FingerprintContributionResponse>, ProblemHttpResult>>
        ContributeFingerprints(
            Guid id,
            FonotecaDbContext db,
            IAcoustIdSubmission acoustId,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var title = await db.Releases
            .Where(release => release.Id == releaseId)
            .Select(release => release.Title)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (title is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail: $"The catalogue has no release with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Taken rather than read, like every other decision here. The pass this
        // races is identification: it writes AcoustId on exactly these rows, and
        // a file it settled in the gap would be submitted as unknown audio
        // seconds after AcoustID told us what it was.
        if (!gate.TryEnter(DecisionWorkKind, out var lease))
        {
            return TypedResults.Problem(
                title: "The library is busy",
                detail:
                    $"A {gate.ActiveKind ?? "pass"} is running, and it may settle exactly the files "
                    + "this would submit. Try again once it has finished.",
                statusCode: StatusCodes.Status409Conflict);
        }

        using var held = lease;

        var rows = await db.MediaFiles
            .Include(file => file.Recording)
            .Where(file => file.ReleaseId == releaseId)
            .Where(Contributable)
            .OrderBy(file => file.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "Nothing to contribute",
                detail:
                    $"No file under “{title}” has a recording somebody chose by hand, a "
                    + "fingerprint to send and no submission on record already. Either these "
                    + "were contributed before, or the passes placed them from AcoustID's own "
                    + "answers — in which case AcoustID is where the link came from.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var items = rows
            .Select(file => new AcoustIdSubmissionItem(
                new AudioFingerprint(file.Fingerprint!, file.FingerprintDuration!.Value),
                file.Recording!.Mbid!.Value))
            .ToList();

        IReadOnlyList<AcoustIdSubmissionReceipt> receipts;

        try
        {
            receipts = await acoustId.SubmitAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            // A batch this endpoint built that the client will not accept — a
            // fingerprint of nothing but whitespace, a duration that rounds to
            // zero. The predicate above rules out every shape reachable from
            // `fpcalc`, so arriving here means a hand-edited or corrupt row; the
            // catch is on the class rather than on those two, because the next
            // such row will be a third shape and a bare 500 with no problem
            // document is the least useful answer available.
            return TypedResults.Problem(
                title: "A file could not be described to AcoustID",
                detail: error.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (ProviderRejectedException error)
        {
            // Kept apart from the one below, which is the point of there being
            // two subclasses. This one is "somebody must change something" — an
            // unset Fonoteca:AcoustIdUserKey is the ordinary case and is thrown
            // before a byte leaves — and answering it 503 would tell the screen
            // to try again later, which is the one thing that can never work.
            // 500 because the fault is this installation's configuration rather
            // than anything the caller sent; the message names the setting.
            return TypedResults.Problem(
                title: "AcoustID refused the request",
                detail: error.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (ProviderUnavailableException error)
        {
            // Nothing is written. A stamp saved before the send would suppress
            // the offer for a submission that never left, which is the one
            // failure mode with no symptom — the button simply stops appearing.
            return TypedResults.Problem(
                title: "AcoustID did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        // Sent first, recorded second — the identification pass's rule about the
        // file and the row, for the same reason. Crash in the gap and the offer
        // is made again, which costs a duplicate submission AcoustID discards.
        // The reverse loses the contribution silently.
        foreach (var row in rows)
        {
            row.AcoustIdSubmittedUtc = now;
        }

        await events.AppendAsync(
            DomainEvent.Create(
                FingerprintsSubmittedEventType,
                FingerprintSubject,
                id.ToString(),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new FingerprintContributionPayload
                    {
                        Release = id,
                        Title = title,
                        Submitted = rows.Count,
                        Submissions = [.. receipts.Select(receipt => receipt.Id)],
                    },
                    MatchingJson.Default.FingerprintContributionPayload),
                correlationId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new FingerprintContributionResponse(
            id,
            rows.Count,
            receipts.Count,
            $"{rows.Count} fingerprint{(rows.Count == 1 ? "" : "s")} sent to AcoustID, bound to the "
            + "recordings these files are filed under. They are imported in the background, so "
            + "nothing changes here — the audio becomes identifiable for anyone who rips it next."));
    }
}

/// <summary>What went to AcoustID, and what it acknowledged.</summary>
/// <remarks>
/// <c>Submitted</c> and <c>Accepted</c> are both here because they can differ:
/// the first is how many claims were sent, the second how many receipts came
/// back. They match against the real service, and a screen that printed only
/// one of them could not show the day they stop.
/// </remarks>
public sealed record FingerprintContributionResponse(
    Guid Release,
    int Submitted,
    int Accepted,
    string Detail);
