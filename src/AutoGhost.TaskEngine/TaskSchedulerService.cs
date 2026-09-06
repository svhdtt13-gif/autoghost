namespace AutoGhost.TaskEngine;

public sealed class TaskSchedulerService
{
    public IReadOnlyList<TaskDefinition> GetDueTasks(
        IEnumerable<TaskDefinition> definitions,
        DateTimeOffset now,
        IReadOnlyList<TaskRunHistory> history,
        IReadOnlySet<string>? completedTaskIds = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(history);

        var latestByTask = history
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.StartedUtc).First(),
                StringComparer.Ordinal);

        var completed = new HashSet<string>(
            completedTaskIds is null ? Array.Empty<string>() : completedTaskIds,
            StringComparer.Ordinal);

        foreach (var item in latestByTask.Values.Where(item => item.State == TaskRunState.Completed))
        {
            completed.Add(item.TaskId);
        }

        return definitions
            .Where(task => task.Enabled && task.Schedule is not null)
            .Where(task => task.Schedule!.IsDue(
                now,
                latestByTask.TryGetValue(task.TaskId, out var lastRun)
                    ? lastRun.EndedUtc ?? lastRun.StartedUtc
                    : null))
            .Where(task => task.DependencyIds.All(completed.Contains))
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.TaskId, StringComparer.Ordinal)
            .ToArray();
    }
}
