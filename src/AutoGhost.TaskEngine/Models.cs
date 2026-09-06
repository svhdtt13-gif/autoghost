namespace AutoGhost.TaskEngine;

public enum TaskScheduleKind
{
    Manual,
    Daily,
    Weekly
}

public enum TaskRunState
{
    Pending,
    Blocked,
    Ready,
    Running,
    Verifying,
    Retrying,
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Skipped
}

public enum TaskRecoveryMode
{
    RetryFromStart,
    FailTask
}

public sealed record TaskSchedule(
    TaskScheduleKind Kind,
    TimeOnly? Time = null,
    DayOfWeek? Day = null)
{
    public static TaskSchedule Manual() => new(TaskScheduleKind.Manual);

    public static TaskSchedule Daily(TimeOnly time) => new(TaskScheduleKind.Daily, time);

    public static TaskSchedule Weekly(DayOfWeek day, TimeOnly time) =>
        new(TaskScheduleKind.Weekly, time, day);

    public bool IsDue(DateTimeOffset now, DateTimeOffset? lastRunUtc)
    {
        if (Kind == TaskScheduleKind.Manual || Time is null)
        {
            return false;
        }

        var localNow = now.LocalDateTime;
        if (lastRunUtc.HasValue && lastRunUtc.Value.ToLocalTime().Date == localNow.Date)
        {
            return false;
        }

        if (TimeOnly.FromDateTime(localNow) < Time.Value)
        {
            return false;
        }

        return Kind != TaskScheduleKind.Weekly || Day == localNow.DayOfWeek;
    }
}

public sealed record TaskStepDefinition(
    string StepId,
    string ActionId,
    string? VerifyId = null,
    int TimeoutSeconds = 30);

public sealed record TaskDefinition(
    string TaskId,
    string Name,
    IReadOnlyList<TaskStepDefinition> Steps,
    int Priority = 0,
    bool Enabled = true,
    int MaxRetries = 0,
    int TimeoutSeconds = 300,
    IReadOnlyList<string>? Dependencies = null,
    TaskSchedule? Schedule = null,
    TaskRecoveryMode Recovery = TaskRecoveryMode.RetryFromStart)
{
    public IReadOnlyList<string> DependencyIds => Dependencies ?? Array.Empty<string>();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TaskId))
        {
            throw new ArgumentException("TaskId is required.", nameof(TaskId));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Name is required.", nameof(Name));
        }

        if (Steps is null || Steps.Count == 0)
        {
            throw new ArgumentException("At least one step is required.", nameof(Steps));
        }

        if (Steps.Any(step => string.IsNullOrWhiteSpace(step.StepId) ||
                             string.IsNullOrWhiteSpace(step.ActionId)))
        {
            throw new ArgumentException("Every step requires StepId and ActionId.", nameof(Steps));
        }

        if (Steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
        {
            throw new ArgumentException("StepId values must be unique.", nameof(Steps));
        }

        if (Steps.Any(step => step.TimeoutSeconds <= 0))
        {
            throw new ArgumentException("Step timeouts must be positive.", nameof(Steps));
        }

        if (TimeoutSeconds <= 0)
        {
            throw new ArgumentException("Task timeout must be positive.", nameof(TimeoutSeconds));
        }

        if (MaxRetries < 0)
        {
            throw new ArgumentException("MaxRetries cannot be negative.", nameof(MaxRetries));
        }
    }
}

public sealed record TaskExecutionOptions(
    bool DryRun,
    bool KillSwitchActive,
    bool AutomationArmed,
    IReadOnlySet<string>? CompletedDependencies = null);

public sealed record TaskExecutionContext(
    string? ClientId,
    bool DryRun,
    bool KillSwitchActive,
    bool AutomationArmed);

public sealed record TaskStepExecutionResult(
    bool Succeeded,
    bool Verified,
    bool DispatchSuppressed,
    string? FailureReason = null);

public interface ITaskRuntime
{
    Task<TaskStepExecutionResult> ExecuteStepAsync(
        TaskDefinition task,
        TaskStepDefinition step,
        TaskExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class DryRunTaskRuntime : ITaskRuntime
{
    private readonly List<string> executedActionIds = new();

    public IReadOnlyList<string> ExecutedActionIds => executedActionIds;

    public Task<TaskStepExecutionResult> ExecuteStepAsync(
        TaskDefinition task,
        TaskStepDefinition step,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executedActionIds.Add(step.ActionId);
        return Task.FromResult(new TaskStepExecutionResult(
            Succeeded: true,
            Verified: true,
            DispatchSuppressed: true));
    }
}

public sealed record TaskRunHistory(
    Guid RunId,
    string TaskId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    TaskRunState State,
    int Attempts,
    IReadOnlyList<string> CompletedStepIds,
    string? FailureReason);

public interface ITaskHistoryStore
{
    IReadOnlyList<TaskRunHistory> Load();

    void Append(TaskRunHistory history);
}

public sealed record TaskRunResult(
    Guid RunId,
    string TaskId,
    TaskRunState State,
    int Attempts,
    IReadOnlyList<string> CompletedStepIds,
    string? FailureReason);
