namespace AutoGhost.TaskEngine;

public sealed class TaskEngine
{
    private readonly ITaskHistoryStore historyStore;

    public TaskEngine(ITaskHistoryStore historyStore)
    {
        this.historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
    }

    public async Task<TaskRunResult> RunAsync(
        TaskDefinition task,
        TaskExecutionOptions options,
        ITaskRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(runtime);
        task.Validate();

        var runId = Guid.NewGuid();
        var startedUtc = DateTimeOffset.UtcNow;
        var completedStepIds = new List<string>();
        var attempts = 0;

        if (!task.Enabled)
        {
            return Finish(
                runId,
                task,
                startedUtc,
                TaskRunState.Skipped,
                attempts,
                completedStepIds,
                "Task is disabled.");
        }

        var completedDependencies = options.CompletedDependencies ?? new HashSet<string>(StringComparer.Ordinal);
        var missingDependencies = task.DependencyIds
            .Where(dependency => !completedDependencies.Contains(dependency))
            .ToArray();

        if (missingDependencies.Length > 0)
        {
            return Finish(
                runId,
                task,
                startedUtc,
                TaskRunState.Blocked,
                attempts,
                completedStepIds,
                "Missing dependencies: " + string.Join(", ", missingDependencies));
        }

        if (options.KillSwitchActive)
        {
            return Finish(
                runId,
                task,
                startedUtc,
                TaskRunState.Blocked,
                attempts,
                completedStepIds,
                "Kill Switch is active.");
        }

        if (!options.DryRun)
        {
            return Finish(
                runId,
                task,
                startedUtc,
                TaskRunState.Blocked,
                attempts,
                completedStepIds,
                "P3 live execution is not authorized; use dry-run.");
        }

        using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        taskTimeout.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

        while (attempts <= task.MaxRetries)
        {
            attempts++;
            completedStepIds.Clear();

            try
            {
                foreach (var step in task.Steps)
                {
                    using var stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(taskTimeout.Token);
                    stepTimeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

                    var context = new TaskExecutionContext(
                        ClientId: null,
                        DryRun: options.DryRun,
                        KillSwitchActive: options.KillSwitchActive,
                        AutomationArmed: options.AutomationArmed);

                    var result = await runtime.ExecuteStepAsync(
                        task,
                        step,
                        context,
                        stepTimeout.Token).ConfigureAwait(false);

                    if (!result.Succeeded || !result.Verified)
                    {
                        throw new TaskStepFailureException(
                            result.FailureReason ?? "Step execution or verification failed.");
                    }

                    completedStepIds.Add(step.StepId);
                }

                return Finish(
                    runId,
                    task,
                    startedUtc,
                    TaskRunState.Completed,
                    attempts,
                    completedStepIds,
                    failureReason: null);
            }
            catch (OperationCanceledException) when (taskTimeout.IsCancellationRequested)
            {
                return Finish(
                    runId,
                    task,
                    startedUtc,
                    TaskRunState.TimedOut,
                    attempts,
                    completedStepIds,
                    "Task timeout exceeded.");
            }
            catch (OperationCanceledException)
            {
                return Finish(
                    runId,
                    task,
                    startedUtc,
                    TaskRunState.Cancelled,
                    attempts,
                    completedStepIds,
                    "Cancellation requested.");
            }
            catch (TaskStepFailureException exception) when (attempts <= task.MaxRetries)
            {
                if (task.Recovery == TaskRecoveryMode.FailTask)
                {
                    return Finish(
                        runId,
                        task,
                        startedUtc,
                        TaskRunState.Failed,
                        attempts,
                        completedStepIds,
                        exception.Message);
                }
            }
            catch (TaskStepFailureException exception)
            {
                return Finish(
                    runId,
                    task,
                    startedUtc,
                    TaskRunState.Failed,
                    attempts,
                    completedStepIds,
                    exception.Message);
            }
        }

        return Finish(
            runId,
            task,
            startedUtc,
            TaskRunState.Failed,
            attempts,
            completedStepIds,
            "Retry budget exhausted.");
    }

    private TaskRunResult Finish(
        Guid runId,
        TaskDefinition task,
        DateTimeOffset startedUtc,
        TaskRunState state,
        int attempts,
        IReadOnlyList<string> completedStepIds,
        string? failureReason)
    {
        var endedUtc = DateTimeOffset.UtcNow;
        var history = new TaskRunHistory(
            runId,
            task.TaskId,
            startedUtc,
            endedUtc,
            state,
            attempts,
            completedStepIds.ToArray(),
            failureReason);
        historyStore.Append(history);

        return new TaskRunResult(
            runId,
            task.TaskId,
            state,
            attempts,
            completedStepIds.ToArray(),
            failureReason);
    }

    private sealed class TaskStepFailureException : Exception
    {
        public TaskStepFailureException(string message)
            : base(message)
        {
        }
    }
}
