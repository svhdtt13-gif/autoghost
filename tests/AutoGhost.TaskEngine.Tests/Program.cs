using AutoGhost.TaskEngine;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("scheduler_due_priority_dependency", TestSchedulerDuePriorityDependencyAsync),
            ("dry_run_history_persistence", TestDryRunHistoryPersistenceAsync),
            ("live_and_kill_switch_guards", TestLiveAndKillSwitchGuardsAsync),
            ("retry_from_start", TestRetryFromStartAsync),
            ("task_timeout", TestTaskTimeoutAsync),
            ("dec043_canonical_hotkeys_and_observed_normalize", TestCanonicalHotkeysAndObservedNormalizeAsync),
            ("transport_note_persists_by_role_and_date", TestTransportNotePersistsByRoleAndDateAsync),
            ("special_items_match_and_no_match_from_configuration", TestSpecialItemsMatchAndNoMatchAsync),
            ("multi_client_acceptance_isolates_notes_and_alerts", TestMultiClientAcceptanceIsolationAsync)
        };

        try
        {
            foreach (var test in tests)
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }

            Console.WriteLine($"P3 task engine + scheduler tests: PASS ({tests.Length}/{tests.Length})");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P3 task engine + scheduler tests: FAIL {exception}");
            return 1;
        }
    }

    private static Task TestSchedulerDuePriorityDependencyAsync()
    {
        var now = new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero);
        var step = new TaskStepDefinition("step", "observe");
        var definitions = new[]
        {
            new TaskDefinition(
                "task-low",
                "Low priority",
                new[] { step },
                Priority: 1,
                Schedule: TaskSchedule.Daily(new TimeOnly(8, 0))),
            new TaskDefinition(
                "task-high",
                "High priority",
                new[] { step },
                Priority: 10,
                Schedule: TaskSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(8, 0))),
            new TaskDefinition(
                "task-blocked",
                "Missing dependency",
                new[] { step },
                Priority: 100,
                Dependencies: new[] { "not-completed" },
                Schedule: TaskSchedule.Daily(new TimeOnly(8, 0)))
        };

        var due = new TaskSchedulerService().GetDueTasks(
            definitions,
            now,
            Array.Empty<TaskRunHistory>());

        Assert(due.Select(task => task.TaskId).SequenceEqual(new[] { "task-high", "task-low" }),
            "Scheduler did not apply due, dependency, and priority rules.");
        return Task.CompletedTask;
    }

    private static async Task TestDryRunHistoryPersistenceAsync()
    {
        var filePath = CreateTempPath();
        try
        {
            var store = new JsonTaskHistoryStore(filePath);
            var task = new TaskDefinition(
                "task-dry-run",
                "Dry-run task",
                new[]
                {
                    new TaskStepDefinition("step-1", "capture"),
                    new TaskStepDefinition("step-2", "verify")
                },
                Schedule: TaskSchedule.Manual());
            var runtime = new DryRunTaskRuntime();
            var result = await new TaskEngine(store).RunAsync(
                task,
                new TaskExecutionOptions(DryRun: true, KillSwitchActive: false, AutomationArmed: false),
                runtime).ConfigureAwait(false);

            Assert(result.State == TaskRunState.Completed, "Dry-run task did not complete.");
            Assert(runtime.ExecutedActionIds.SequenceEqual(new[] { "capture", "verify" }),
                "Dry-run runtime did not execute the expected action trace.");
            Assert(File.Exists(filePath), "Task history file was not persisted.");
            var reloaded = store.Load();
            Assert(reloaded.Count == 1 && reloaded[0].State == TaskRunState.Completed,
                "Persisted task history did not reload.");
        }
        finally
        {
            DeleteTempPath(filePath);
        }
    }

    private static async Task TestLiveAndKillSwitchGuardsAsync()
    {
        var store = new MemoryHistoryStore();
        var runtime = new CountingRuntime();
        var task = new TaskDefinition(
            "task-guard",
            "Guarded task",
            new[] { new TaskStepDefinition("step", "would-dispatch") });
        var engine = new TaskEngine(store);

        var liveResult = await engine.RunAsync(
            task,
            new TaskExecutionOptions(DryRun: false, KillSwitchActive: false, AutomationArmed: true),
            runtime).ConfigureAwait(false);
        var killSwitchResult = await engine.RunAsync(
            task,
            new TaskExecutionOptions(DryRun: true, KillSwitchActive: true, AutomationArmed: true),
            runtime).ConfigureAwait(false);

        Assert(liveResult.State == TaskRunState.Blocked, "Live execution was not blocked.");
        Assert(killSwitchResult.State == TaskRunState.Blocked, "Kill Switch did not block execution.");
        Assert(runtime.Calls == 0, "Guarded execution reached the runtime.");
        return;
    }

    private static async Task TestRetryFromStartAsync()
    {
        var runtime = new RetryRuntime();
        var task = new TaskDefinition(
            "task-retry",
            "Retry task",
            new[] { new TaskStepDefinition("step", "flaky") },
            MaxRetries: 1);
        var result = await new TaskEngine(new MemoryHistoryStore()).RunAsync(
            task,
            new TaskExecutionOptions(DryRun: true, KillSwitchActive: false, AutomationArmed: false),
            runtime).ConfigureAwait(false);

        Assert(result.State == TaskRunState.Completed, "Retry task did not recover.");
        Assert(result.Attempts == 2 && runtime.Calls == 2, "Retry budget was not applied.");
        return;
    }

    private static async Task TestTaskTimeoutAsync()
    {
        var task = new TaskDefinition(
            "task-timeout",
            "Timeout task",
            new[] { new TaskStepDefinition("step", "hang", TimeoutSeconds: 1) },
            TimeoutSeconds: 1);
        var result = await new TaskEngine(new MemoryHistoryStore()).RunAsync(
            task,
            new TaskExecutionOptions(DryRun: true, KillSwitchActive: false, AutomationArmed: false),
            new HangingRuntime()).ConfigureAwait(false);

        Assert(result.State == TaskRunState.TimedOut, "Task timeout was not recorded.");
        return;
    }

    private static Task TestCanonicalHotkeysAndObservedNormalizeAsync()
    {
        var expected = new Dictionary<AutoGhostPanel, char>
        {
            [AutoGhostPanel.Map] = 'M',
            [AutoGhostPanel.Bag] = 'B',
            [AutoGhostPanel.Chat] = 'X',
            [AutoGhostPanel.Activity] = 'H',
            [AutoGhostPanel.Skill] = 'K',
            [AutoGhostPanel.TaskStatus] = 'L',
            [AutoGhostPanel.Benefits] = 'I',
            [AutoGhostPanel.Market] = 'Y',
            [AutoGhostPanel.Party] = 'T'
        };

        Assert(AutoGhostHotkeyMap.Canonical.Count == expected.Count,
            "Canonical hotkey map does not contain exactly the DEC-043 panels.");
        Assert(expected.All(pair => AutoGhostHotkeyMap.GetKey(pair.Key) == pair.Value),
            "Canonical hotkey map contains a wrong panel key.");
        Assert(expected.Values.Distinct().Count() == expected.Count,
            "Canonical hotkey map contains duplicate toggle keys.");
        foreach (var pair in expected)
        {
            Assert(AutoGhostHotkeyMap.TryResolve(char.ToLowerInvariant(pair.Value), out var panel) &&
                   panel == pair.Key,
                $"Lowercase key '{pair.Value}' did not resolve to {pair.Key}.");
        }

        var panelPlan = AutoGhostGlobalNormalize.Build(new AutoGhostObservedUiState(
            TargetBindingVerified: true,
            Surface: AutoGhostObservedUiSurface.Panel,
            OpenPanel: AutoGhostPanel.Activity,
            MainFrameVerified: false,
            HangZhouVerified: false,
            MainWorldVerified: false));
        Assert(panelPlan[0].Kind == AutoGhostNormalizeStepKind.CloseObservedPanel &&
               panelPlan[0].Panel == AutoGhostPanel.Activity &&
               panelPlan[0].Hotkey == 'H',
            "Observed Activity panel did not produce the single H close toggle.");
        Assert(panelPlan.Count(step => step.Kind == AutoGhostNormalizeStepKind.CloseObservedPanel) == 1,
            "Normalization planned more than one toggle close.");
        Assert(panelPlan.Skip(1).Select(step => step.Kind).SequenceEqual(new[]
        {
            AutoGhostNormalizeStepKind.VerifyMainFrame,
            AutoGhostNormalizeStepKind.VerifyHangZhou,
            AutoGhostNormalizeStepKind.VerifyMainWorld
        }),
            "Normalization did not require the global verification sequence.");

        var toggleAlwaysRequiresReverification = AutoGhostGlobalNormalize.Build(new AutoGhostObservedUiState(
            TargetBindingVerified: true,
            Surface: AutoGhostObservedUiSurface.Panel,
            OpenPanel: AutoGhostPanel.Bag,
            MainFrameVerified: true,
            HangZhouVerified: true,
            MainWorldVerified: true));
        Assert(toggleAlwaysRequiresReverification.Select(step => step.Kind).SequenceEqual(new[]
        {
            AutoGhostNormalizeStepKind.CloseObservedPanel,
            AutoGhostNormalizeStepKind.VerifyMainFrame,
            AutoGhostNormalizeStepKind.VerifyHangZhou,
            AutoGhostNormalizeStepKind.VerifyMainWorld
        }) && toggleAlwaysRequiresReverification[0].Hotkey == 'B',
            "A panel toggle did not force post-toggle global verification.");

        var alreadyNormalized = AutoGhostGlobalNormalize.Build(new AutoGhostObservedUiState(
            TargetBindingVerified: true,
            Surface: AutoGhostObservedUiSurface.MainWorld,
            OpenPanel: null,
            MainFrameVerified: true,
            HangZhouVerified: true,
            MainWorldVerified: true));
        Assert(alreadyNormalized.Count == 0,
            "A verified Hàng Châu main-world frame should not receive toggle input.");

        var unknown = AutoGhostGlobalNormalize.Build(new AutoGhostObservedUiState(
            TargetBindingVerified: true,
            Surface: AutoGhostObservedUiSurface.Unknown,
            OpenPanel: null,
            MainFrameVerified: false,
            HangZhouVerified: false,
            MainWorldVerified: false));
        Assert(unknown.Count == 1 && unknown[0].Kind == AutoGhostNormalizeStepKind.SafeStop,
            "Unknown UI state did not fail closed.");

        return Task.CompletedTask;
    }

    private static Task TestTransportNotePersistsByRoleAndDateAsync()
    {
        var filePath = CreateTempPath();
        try
        {
            var store = new JsonTransportNoteHistoryStore(filePath);
            var service = new TransportNoteService(store);
            var dayOne = new DateOnly(2026, 9, 6);
            var dayTwo = dayOne.AddDays(1);

            service.CaptureAndPersist(
                "ROLE_A",
                dayOne,
                new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero),
                CreateItems("A"),
                configuredSpecialItems: null);
            service.CaptureAndPersist(
                "ROLE_B",
                dayOne,
                new DateTimeOffset(2026, 9, 6, 2, 1, 0, TimeSpan.Zero),
                CreateItems("B"),
                new[] { "B-3" });
            service.CaptureAndPersist(
                "ROLE_A",
                dayTwo,
                new DateTimeOffset(2026, 9, 7, 2, 0, 0, TimeSpan.Zero),
                CreateItems("A2"),
                new[] { "A2-4" });

            var replacementItems = CreateItems("A-replacement");
            service.CaptureAndPersist(
                "ROLE_A",
                dayOne,
                new DateTimeOffset(2026, 9, 6, 3, 0, 0, TimeSpan.Zero),
                replacementItems,
                new[] { "A-replacement-1" });

            var reloaded = new JsonTransportNoteHistoryStore(filePath);
            var roleADayOne = reloaded.Load("ROLE_A", dayOne);
            var roleBDayOne = reloaded.Load("ROLE_B", dayOne);
            var roleADayTwo = reloaded.Load("ROLE_A", dayTwo);

            Assert(reloaded.LoadAll().Count == 3, "Saving the same Role ID + date did not replace the existing note.");
            Assert(roleADayOne is not null && roleADayOne.Items[0].Name == "A-replacement-1",
                "Role A day-one note was not reloaded by its exact composite key.");
            Assert(roleBDayOne is not null && roleBDayOne.Items[0].Name == "B-1" &&
                   roleBDayOne.SpecialItemStatus == SpecialItemComparisonStatus.Match,
                "Role B day-one note was not isolated from Role A.");
            Assert(roleADayTwo is not null && roleADayTwo.Items[0].Name == "A2-1",
                "Role A day-two note was not isolated from Role A day one.");
        }
        finally
        {
            DeleteTempPath(filePath);
        }

        return Task.CompletedTask;
    }

    private static Task TestSpecialItemsMatchAndNoMatchAsync()
    {
        var items = CreateItems("Configured");

        var match = SpecialItemMatcher.Compare(items, new[] { "  Configured-3  " });
        Assert(match.Status == SpecialItemComparisonStatus.Match,
            "Configured special-item match was not detected.");
        Assert(match.MatchedItems.SequenceEqual(new[] { "Configured-3" }),
            "The matched item evidence did not preserve the captured item name.");

        var noMatch = SpecialItemMatcher.Compare(items, new[] { "Operator supplied item that is absent" });
        Assert(noMatch.Status == SpecialItemComparisonStatus.NoMatch && noMatch.MatchedItems.Count == 0,
            "Configured special-item no-match case was not detected.");

        var pending = SpecialItemMatcher.Compare(items, configuredSpecialItems: null);
        Assert(pending.Status == SpecialItemComparisonStatus.PendingUserList && !pending.AlertRequired,
            "An unconfigured special-item list did not remain pending without an alert.");
        return Task.CompletedTask;
    }

    private static Task TestMultiClientAcceptanceIsolationAsync()
    {
        const string roleA = "3488404011";
        const string roleB = "16711204011";
        var filePath = CreateTempPath();
        try
        {
            var store = new JsonTransportNoteHistoryStore(filePath);
            var service = new TransportNoteService(store);
            var localDate = new DateOnly(2026, 9, 6);
            var roleAItems = new[]
            {
                new TransportItemRequirement("Trân Châu", 2),
                new TransportItemRequirement("Hồng Hoa Hoàn", 20),
                new TransportItemRequirement("Xích Thước Hoàn", 20),
                new TransportItemRequirement("Bánh Củ Hoàng", 20),
                new TransportItemRequirement("Đào Hoa Thạch", 1),
                new TransportItemRequirement("Lăng Tiêu Hoàn", 20),
                new TransportItemRequirement("Canh Ngũ Sắc", 20),
                new TransportItemRequirement("Gà Hoa Tuyết", 5)
            };

            service.CaptureAndPersist(
                roleA,
                localDate,
                new DateTimeOffset(2026, 9, 6, 3, 30, 0, TimeSpan.Zero),
                roleAItems,
                new[] { "Đào Hoa Thạch" });
            service.CaptureAndPersist(
                roleB,
                localDate,
                new DateTimeOffset(2026, 9, 6, 3, 31, 0, TimeSpan.Zero),
                CreateItems("RoleB"),
                new[] { "Đào Hoa Thạch" });

            var roleANote = store.Load(roleA, localDate);
            var roleBNote = store.Load(roleB, localDate);
            Assert(roleANote is not null && roleANote.SpecialItemStatus == SpecialItemComparisonStatus.Match &&
                   roleANote.MatchedSpecialItems.SequenceEqual(new[] { "Đào Hoa Thạch" }),
                "Role 3488404011 did not retain its own special-item match and alert evidence.");
            Assert(roleBNote is not null && roleBNote.SpecialItemStatus == SpecialItemComparisonStatus.NoMatch &&
                   roleBNote.MatchedSpecialItems.Count == 0 && roleBNote.Items[0].Name == "RoleB-1",
                "Role 16711204011 leaked Role A item or alert state.");
            Assert(store.LoadAll().Count == 2,
                "Multi-client acceptance did not persist exactly one note per Role ID + date.");
        }
        finally
        {
            DeleteTempPath(filePath);
        }

        return Task.CompletedTask;
    }

    private static IReadOnlyList<TransportItemRequirement> CreateItems(string prefix) =>
        Enumerable.Range(1, 8)
            .Select(index => new TransportItemRequirement($"{prefix}-{index}", index))
            .ToArray();

    private static string CreateTempPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "AutoGhost-P3-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history.json");
    }

    private static void DeleteTempPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MemoryHistoryStore : ITaskHistoryStore
    {
        private readonly List<TaskRunHistory> history = new();

        public IReadOnlyList<TaskRunHistory> Load() => history.ToArray();

        public void Append(TaskRunHistory item) => history.Add(item);
    }

    private sealed class CountingRuntime : ITaskRuntime
    {
        public int Calls { get; private set; }

        public Task<TaskStepExecutionResult> ExecuteStepAsync(
            TaskDefinition task,
            TaskStepDefinition step,
            TaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TaskStepExecutionResult(true, true, true));
        }
    }

    private sealed class RetryRuntime : ITaskRuntime
    {
        public int Calls { get; private set; }

        public Task<TaskStepExecutionResult> ExecuteStepAsync(
            TaskDefinition task,
            TaskStepDefinition step,
            TaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            var result = Calls == 1
                ? new TaskStepExecutionResult(false, false, true, "simulated verification failure")
                : new TaskStepExecutionResult(true, true, true);
            return Task.FromResult(result);
        }
    }

    private sealed class HangingRuntime : ITaskRuntime
    {
        public async Task<TaskStepExecutionResult> ExecuteStepAsync(
            TaskDefinition task,
            TaskStepDefinition step,
            TaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return new TaskStepExecutionResult(true, true, true);
        }
    }
}
