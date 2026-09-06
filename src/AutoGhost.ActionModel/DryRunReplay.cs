namespace AutoGhost.ActionModel;

public sealed record ReplayContext(
    string TargetClientId,
    string ObservedRoleId,
    bool BindingReady,
    bool KillSwitchActive);

public enum DryRunReplayState
{
    Completed,
    Stopped,
    Blocked,
    Failed
}

public sealed record DryRunReplayEvent(
    int Sequence,
    RecordedActionKind Kind,
    int DelayBeforeMs,
    bool DispatchSuppressed,
    string Outcome,
    string Detail);

public sealed record DryRunReplayReport(
    DryRunReplayState State,
    string Reason,
    IReadOnlyList<DryRunReplayEvent> Events);

/// <summary>
/// P2 replay is deliberately simulation-only. This class never calls SendInput,
/// PostMessage, window hooks, drivers, injection APIs, or game memory APIs.
/// </summary>
public sealed class DryRunReplayEngine
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _resumeSignal = CompletedSignal();
    private CancellationTokenSource? _stopSource;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            IsPaused = true;
            _resumeSignal = NewSignal();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            IsPaused = false;
            _resumeSignal.TrySetResult(true);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopSource?.Cancel();
            _resumeSignal.TrySetCanceled();
        }
    }

    public Task<DryRunReplayReport> RunAsync(
        ActionDefinition definition,
        ReplayContext context,
        IProgress<DryRunReplayEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(definition, context, progress, cancellationToken);
    }

    private async Task<DryRunReplayReport> RunCoreAsync(
        ActionDefinition definition,
        ReplayContext context,
        IProgress<DryRunReplayEvent>? progress,
        CancellationToken cancellationToken)
    {
        var validation = ActionDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            return new DryRunReplayReport(DryRunReplayState.Blocked, validation.Reason, Array.Empty<DryRunReplayEvent>());
        }

        if (!context.BindingReady ||
            !string.Equals(context.TargetClientId, definition.TargetClientId, StringComparison.Ordinal) ||
            !string.Equals(context.TargetClientId, context.ObservedRoleId, StringComparison.Ordinal))
        {
            return new DryRunReplayReport(
                DryRunReplayState.Blocked,
                "Replay blocked: target binding is not READY or Role ID != TargetClientId.",
                Array.Empty<DryRunReplayEvent>());
        }

        CancellationTokenSource? linkedSource = null;
        lock (_gate)
        {
            if (IsRunning)
            {
                return new DryRunReplayReport(DryRunReplayState.Blocked, "A dry-run replay is already running.", Array.Empty<DryRunReplayEvent>());
            }

            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stopSource = linkedSource;
            IsRunning = true;
            IsPaused = false;
            _resumeSignal = CompletedSignal();
        }

        var events = new List<DryRunReplayEvent>();
        try
        {
            foreach (var step in definition.Steps)
            {
                try
                {
                    await WaitUntilResumedAsync(linkedSource.Token).ConfigureAwait(true);
                    linkedSource.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    return new DryRunReplayReport(DryRunReplayState.Stopped, "Replay stopped by operator or Kill Switch.", events);
                }

                var semanticDetail = step.Semantic is null
                    ? "No semantic hook; dry-run only."
                    : $"Vision/semantic hook '{step.Semantic.Name}' recorded but not executed in P2 dry-run.";
                var replayEvent = new DryRunReplayEvent(
                    step.Sequence,
                    step.Kind,
                    step.DelayBeforeMs,
                    DispatchSuppressed: true,
                    Outcome: context.KillSwitchActive ? "SUPPRESSED_KILL_SWITCH" : "SUPPRESSED_DRY_RUN",
                    Detail: semanticDetail);
                events.Add(replayEvent);
                progress?.Report(replayEvent);
                await Task.Yield();
            }

            return new DryRunReplayReport(
                DryRunReplayState.Completed,
                context.KillSwitchActive
                    ? "Dry-run completed; all dispatch was suppressed while Kill Switch was ON."
                    : "Dry-run completed; no OS input was emitted.",
                events);
        }
        finally
        {
            lock (_gate)
            {
                IsRunning = false;
                IsPaused = false;
                _stopSource = null;
                _resumeSignal.TrySetResult(true);
            }

            linkedSource.Dispose();
        }
    }

    private Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (_gate)
        {
            signal = _resumeSignal.Task;
        }

        return signal.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.TrySetResult(true);
        return signal;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
