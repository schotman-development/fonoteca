using System.Text.Json.Serialization;
using Fonoteca.Api.Logging;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Api.Library;

/// <summary>
/// Runs the passes one after another: scan, identify, enrich, attribute, probe,
/// analyse and, last, the tag write.
/// </summary>
/// <remarks>
/// <b>It chains; it decides nothing.</b> Every step is the service the button
/// already starts, so the gate, the refusals and the journal are theirs. The
/// flow only waits for one to release the gate before it asks for the next.
///
/// <b>The first refusal or failure ends it</b> and names the step: a pass that
/// could not start (the gate is held, the volume is unmounted) or one that
/// recorded an error. Carrying on would run the tag write over a catalogue a
/// pass just failed to build, which is the one thing the order exists to avoid.
/// Questions a person must answer (refused albums) are not failures; the flow
/// finishes and the tag write covers what the catalogue answers.
///
/// <b>The tag write is a step like the others</b> (ADR 0021) and obeys
/// <c>Fonoteca:AllowFileMutation</c> as it always has: with that off the step
/// is a complete dry run. A caller that wants the catalogue built and the
/// files left alone leaves it out with <c>writeTags: false</c>.
///
/// <b>One flow at a time</b>, and it holds nothing between steps, so another
/// pass may take the gate in the gap and the next step then refuses and ends
/// the flow rather than queueing behind it.
/// </remarks>
public sealed class LibraryFlowService(
    LibraryScanService scans,
    IdentificationService identification,
    EnrichmentService enrichment,
    ReleaseAttributionService attribution,
    ProbeService probes,
    AnalysisService analysis,
    TagWriteService tags,
    LibraryWorkGate gate,
    IHostApplicationLifetime lifetime,
    ILogger<LibraryFlowService> logger)
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    private readonly Lock _sync = new();

    private CancellationTokenSource? _cancellation;
    private LibraryFlowStatus? _last;

    /// <summary>The flow in flight, or the last one that ran; null before the first.</summary>
    public LibraryFlowStatus? Status
    {
        get
        {
            lock (_sync) return _last;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _cancellation is not null;
        }
    }

    /// <summary>Starts a flow, or returns null when one is already running.</summary>
    public LibraryFlowStatus? Start(bool writeTags, string actor)
    {
        lock (_sync)
        {
            if (_cancellation is not null) return null;

            var steps = StepsFor(writeTags, actor);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

            _cancellation = cancellation;
            _last = new LibraryFlowStatus(
                Guid.CreateVersion7().ToString("N")[..12],
                LibraryFlowState.Running,
                null,
                [.. steps.Select(step => step.Name)],
                [],
                null);

            var started = _last;

            _ = Task.Run(() => RunAsync(started, steps, cancellation), CancellationToken.None);

            return started;
        }
    }

    /// <summary>Asks the flow to stop; the pass it is on finishes its file. A scan runs to its end.</summary>
    public bool Cancel()
    {
        lock (_sync)
        {
            if (_cancellation is null) return false;

            _cancellation.Cancel();
            return true;
        }
    }

    private async Task RunAsync(
        LibraryFlowStatus started,
        IReadOnlyList<FlowStep> steps,
        CancellationTokenSource cancellation)
    {
        var finished = started;

        try
        {
            foreach (var step in steps)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Publish(finished = finished with { Current = step.Name });

                if (await RunStepAsync(step, cancellation.Token).ConfigureAwait(false) is { } failure)
                {
                    Publish(finished = finished with { State = LibraryFlowState.Failed, Current = step.Name, Error = failure });
                    return;
                }

                Publish(finished = finished with { Done = [.. finished.Done, step.Name] });
            }

            Publish(finished with { State = LibraryFlowState.Completed, Current = null });
        }
        catch (OperationCanceledException)
        {
            Publish(finished with { State = LibraryFlowState.Cancelled });
        }
#pragma warning disable CA1031 // A background flow must never take the process down.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            Log.LibraryFlowAborted(logger, cause);
            Publish(finished with { State = LibraryFlowState.Failed, Error = cause.Message });
        }
        finally
        {
            lock (_sync) _cancellation = null;
            cancellation.Dispose();
        }
    }

    /// <returns>Null when the step ran to its end, else why it did not.</returns>
    private async Task<string?> RunStepAsync(FlowStep step, CancellationToken cancellationToken)
    {
        if (await step.Start().ConfigureAwait(false) is { } refused) return refused;

        try
        {
            while (step.IsRunning())
            {
                await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            step.Cancel();

            // The pass finishes its file and releases the gate; waiting here keeps
            // the flow "running" until the library is really free.
            while (step.IsRunning()) await Task.Delay(PollEvery, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        // A pass reports itself finished a moment before it hands the gate back; the
        // next step would be refused in that gap.
        while (gate.IsBusy) await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);

        return step.LastError();
    }

    private void Publish(LibraryFlowStatus status)
    {
        lock (_sync)
        {
            _last = status;

            // Freed with the final status, so a caller that sees it finished can start the next.
            if (status.State != LibraryFlowState.Running) _cancellation = null;
        }
    }

    private FlowStep[] StepsFor(bool writeTags, string actor)
    {
        FlowStep[] passes =
        [
            new(
                "scan",
                async () => gate.IsBusy
                    ? $"The scan did not run: {gate.ActiveKind} is running."
                    : (await scans.ScanAsync(lifetime.ApplicationStopping).ConfigureAwait(false)).Status switch
                    {
                        LibraryScanStatus.Completed => null,
                        var other => $"The scan did not run: {other}.",
                    },
                () => false,
                () => null,
                () => { }),
            new(
                "identify",
                () => Refusal(identification.Start().Status is IdentificationStatus.Started, "identify"),
                () => identification.IsRunning,
                () => identification.LastError,
                () => identification.Cancel()),
            new(
                "enrich",
                () => Refusal(enrichment.Start().Status is EnrichmentStatus.Started, "enrich"),
                () => enrichment.IsRunning,
                () => enrichment.LastError,
                () => enrichment.Cancel()),
            new(
                "attribute",
                () => Refusal(attribution.Start().Status is AttributionStatus.Started, "attribute"),
                () => attribution.IsRunning,
                () => attribution.LastError,
                () => attribution.Cancel()),
            new(
                "probe",
                () => Refusal(probes.Start().Status is ProbeStatus.Started, "probe"),
                () => probes.IsRunning,
                () => probes.LastError,
                () => probes.Cancel()),
            new(
                "analyse",
                () => Refusal(analysis.Start().Status is AnalysisStatus.Started, "analyse"),
                () => analysis.IsRunning,
                () => analysis.LastError,
                () => analysis.Cancel()),
        ];

        if (!writeTags) return passes;

        return
        [
            .. passes,
            new(
                "tags",
                () => Refusal(tags.Start(TagWriteScope.Library, actor).Status is TagWriteStartStatus.Started, "tags"),
                () => tags.IsRunning,
                () => tags.LastError,
                () => tags.Cancel()),
        ];
    }

    private static Task<string?> Refusal(bool started, string pass) =>
        Task.FromResult<string?>(started ? null : $"The {pass} pass did not start: the library is busy or its root is missing.");
}

/// <summary>One step of the flow, as the functions the flow needs from its pass.</summary>
internal sealed record FlowStep(
    string Name,
    Func<Task<string?>> Start,
    Func<bool> IsRunning,
    Func<string?> LastError,
    Action Cancel);

[JsonConverter(typeof(JsonStringEnumConverter<LibraryFlowState>))]
public enum LibraryFlowState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <param name="Steps">Every step this flow will run, in order.</param>
/// <param name="Done">The steps that ran to their end.</param>
/// <param name="Current">The step running now, or the one that failed.</param>
/// <param name="Error">Why a failed flow stopped.</param>
public sealed record LibraryFlowStatus(
    string JobId,
    LibraryFlowState State,
    string? Current,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Done,
    string? Error);
