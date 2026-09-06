using System.Text.Json;
using AutoGhost.ActionModel;

namespace AutoGhost.ActionModel.Tests;

public static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            TestNormalizedCoordinates();
            TestActionValidationAndPersistence();
            await TestDryRunIdentityGuardAsync();
            await TestDryRunKillSwitchPauseAndStopAsync();
            Console.WriteLine("P2 action model + dry-run tests: PASS (4/4)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P2 action model tests: FAIL — {exception.Message}");
            return 1;
        }
    }

    private static void TestNormalizedCoordinates()
    {
        var point = NormalizedPoint.FromClientPixel(50, 25, 101, 51);
        Assert(Math.Abs(point.X - 0.5) < 0.0001, "X coordinate was not normalized");
        Assert(Math.Abs(point.Y - 0.5) < 0.0001, "Y coordinate was not normalized");
        var roundTrip = point.ToClientPixel(101, 51);
        Assert(roundTrip == (50, 25), "normalized coordinate did not round-trip");
    }

    private static void TestActionValidationAndPersistence()
    {
        var definition = ActionDefinition.Create(
            "sample",
            "client-alpha",
            100,
            50,
            new ActionStep[]
            {
                new(0, RecordedActionKind.MouseMove, 12, Point: new NormalizedPoint(0.25, 0.75)),
                new(1, RecordedActionKind.MouseButton, 4, Point: new NormalizedPoint(0.25, 0.75),
                    MouseButton: RecordedMouseButton.Left, IsDown: true),
                new(2, RecordedActionKind.Key, 20, VirtualKey: 0x41, KeyEvent: RecordedKeyEvent.Down),
                new(3, RecordedActionKind.SemanticHook, 0, Semantic: new SemanticHookDefinition(
                    "confirm", SemanticHookKind.Text, "OK"))
            });

        Assert(ActionDefinitionValidator.Validate(definition).IsValid, "valid action was rejected");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AutoGhost-P2-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDirectory, "sample.action.json");
        try
        {
            var store = new ActionDefinitionStore();
            store.Save(path, definition);
            var loaded = store.Load(path);
            Assert(loaded.SchemaVersion == definition.SchemaVersion &&
                   loaded.Name == definition.Name &&
                   loaded.TargetClientId == definition.TargetClientId &&
                   loaded.RecordedClientWidth == definition.RecordedClientWidth &&
                   loaded.RecordedClientHeight == definition.RecordedClientHeight &&
                   loaded.Steps.SequenceEqual(definition.Steps),
                "action definition did not round-trip");
            var json = File.ReadAllText(path);
            Assert(!json.Contains("processId", StringComparison.OrdinalIgnoreCase), "PID leaked into action definition");
            Assert(!json.Contains("windowHandle", StringComparison.OrdinalIgnoreCase), "HWND leaked into action definition");
            _ = JsonDocument.Parse(json);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task TestDryRunIdentityGuardAsync()
    {
        var definition = ActionDefinition.Create(
            "guarded",
            "client-alpha",
            100,
            50,
            new[] { new ActionStep(0, RecordedActionKind.MouseMove, 0, Point: new NormalizedPoint(0.5, 0.5)) });
        var engine = new DryRunReplayEngine();
        var report = await engine.RunAsync(
            definition,
            new ReplayContext("client-beta", "client-beta", BindingReady: true, KillSwitchActive: true));
        Assert(report.State == DryRunReplayState.Blocked, "mismatched target was not blocked");
        Assert(report.Events.Count == 0, "blocked replay emitted events");
    }

    private static async Task TestDryRunKillSwitchPauseAndStopAsync()
    {
        var definition = ActionDefinition.Create(
            "stop-test",
            "client-alpha",
            100,
            50,
            Enumerable.Range(0, 8).Select(index => new ActionStep(
                index,
                RecordedActionKind.MouseMove,
                0,
                Point: new NormalizedPoint(0.1 + index / 10d, 0.5))).ToArray());
        var engine = new DryRunReplayEngine();
        var progress = new Progress<DryRunReplayEvent>(_ => engine.Pause());
        var run = engine.RunAsync(
            definition,
            new ReplayContext("client-alpha", "client-alpha", BindingReady: true, KillSwitchActive: true),
            progress);
        await Task.Delay(30);
        Assert(engine.IsPaused, "dry-run did not pause");
        engine.Resume();
        engine.Stop();
        var report = await run;
        Assert(report.State == DryRunReplayState.Stopped, "operator stop did not stop dry-run");
        Assert(report.Events.All(item => item.DispatchSuppressed), "dry-run emitted an unsuppressed event");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
