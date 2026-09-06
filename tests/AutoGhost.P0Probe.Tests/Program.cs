using AutoGhost.P0Probe;

namespace AutoGhost.P0Probe.Tests;

internal static class Program
{
    private static int Main()
    {
        try
        {
            RoleIdResolverTests();
            IdentityClassificationTests();
            DuplicateIdentityFailsClosedTests();
            InputGuardTests();
            RebindGuardTests();
            ReconciliationTests();
            Console.WriteLine("P0 smoke tests: PASS (6/6)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P0 smoke tests: FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void RoleIdResolverTests()
    {
        var resolver = new RoleIdResolver(@"^qnyh\s*\|\s*(?<roleId>[A-Z0-9_-]+)$");
        Equal("CLIENT_A", resolver.Resolve("qnyh | CLIENT_A"));
        Null(resolver.Resolve("qnyh | unknown title"));
        Null(new RoleIdResolver(null).Resolve("CLIENT_A"));
    }

    private static void IdentityClassificationTests()
    {
        var clients = new[] { new ClientDefinition("CLIENT_A") };
        var observation = Observation(roleId: "CLIENT_A");
        var classified = BindingEngine.Classify(observation, clients, EmptySet());
        Equal(IdentityState.Matched, classified.IdentityState);

        var mismatch = BindingEngine.Classify(Observation(roleId: "CLIENT_B"), clients, EmptySet());
        Equal(IdentityState.Mismatch, mismatch.IdentityState);
    }

    private static void DuplicateIdentityFailsClosedTests()
    {
        var clients = new[] { new ClientDefinition("CLIENT_A") };
        var duplicate = new HashSet<string>(StringComparer.Ordinal) { "CLIENT_A" };
        var classified = BindingEngine.Classify(Observation(roleId: "CLIENT_A"), clients, duplicate);
        Equal(IdentityState.DuplicateIdentity, classified.IdentityState);
        Equal(0, new BindingEngine(clients).Evaluate(new[] { classified }).Count);
    }

    private static void InputGuardTests()
    {
        var observation = Observation(roleId: "CLIENT_A") with { IdentityState = IdentityState.Matched };
        var binding = new RuntimeBinding("CLIENT_A", "CLIENT_A", 100, observation.WindowHandle,
            Guid.NewGuid(), BindingState.Ready, "test");
        var guard = new InputGuard();

        False(guard.Validate(binding, observation, killSwitchActive: true, inputEnabled: true));
        False(guard.Validate(binding, observation with { RoleId = "CLIENT_B" }, false, true));
        True(guard.Validate(binding, observation, false, true));
    }

    private static void RebindGuardTests()
    {
        var observation = Observation(roleId: "CLIENT_A") with { IdentityState = IdentityState.Matched };
        var binding = new RuntimeBinding("CLIENT_A", "CLIENT_A", 100, observation.WindowHandle,
            Guid.NewGuid(), BindingState.Ready, "test");
        var changedPid = observation with { ProcessId = 101 };
        var decision = new InputGuard().Validate(binding, changedPid, false, true);
        False(decision);
        Contains("session changed", decision.Reason);
    }

    private static void ReconciliationTests()
    {
        var current = Observation(roleId: "CLIENT_A") with { IdentityState = IdentityState.Matched };
        var first = new BindingReconciler().Reconcile(null, current);
        True(first is not null && first.State == BindingState.Ready);

        var recreated = current with { ProcessId = 101, WindowHandle = new nint(201) };
        var rebound = new BindingReconciler().Reconcile(first, recreated);
        True(rebound is not null && rebound.State == BindingState.Ready);
        if (rebound!.SessionId == first!.SessionId)
        {
            throw new InvalidOperationException("A recreated runtime window must receive a new session ID.");
        }

        var delayedRole = current with { RoleId = null, IdentityState = IdentityState.RoleIdUnavailable };
        Null(new BindingReconciler().Reconcile(first, delayedRole));

        var changedRole = current with { RoleId = "CLIENT_B", IdentityState = IdentityState.Mismatch };
        Null(new BindingReconciler().Reconcile(first, changedRole));
    }

    private static WindowObservation Observation(string? roleId) => new(
        WindowHandle: new nint(200),
        DesktopName: "TestDesktop",
        Title: roleId is null ? "qnyh" : $"qnyh | {roleId}",
        ClassName: "TestWindow",
        IsVisible: true,
        IsEnabled: true,
        OwnerWindowHandle: nint.Zero,
        ParentWindowHandle: nint.Zero,
        ThreadId: 1,
        ProcessId: 100,
        ProcessName: "qnyh",
        ProcessPath: null,
        ParentProcessId: null,
        ProcessAncestry: Array.Empty<uint>(),
        ProcessTreeRelation: "Target",
        IsTargetProcessTree: true,
        WindowRect: new WindowRectangle(0, 0, 100, 100),
        ClientRect: new WindowRectangle(0, 0, 100, 100),
        IsMinimized: false,
        IsCloaked: false,
        IsForeground: true,
        RoleId: roleId,
        IdentityState: IdentityState.Identifying,
        IdentityReason: "test");

    private static IReadOnlySet<string> EmptySet() => new HashSet<string>(StringComparer.Ordinal);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got '{value}'.");
        }
    }

    private static void True(InputDecision decision)
    {
        if (!decision.Allowed)
        {
            throw new InvalidOperationException($"Expected allowed input: {decision.Reason}");
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }

    private static void False(InputDecision decision)
    {
        if (decision.Allowed)
        {
            throw new InvalidOperationException($"Expected blocked input: {decision.Reason}");
        }
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }
}
