using AutoGhost.P0Probe;

namespace AutoGhost.App.Services;

public sealed record RuntimeProbeResult(
    IReadOnlyList<ProcessObservation> Processes,
    IReadOnlyList<WindowObservation> Windows,
    IReadOnlyList<RuntimeBinding> Bindings);

/// <summary>
/// P1 uses the proven P0 discovery/binding path as a read-only runtime view.
/// No capture, input dispatch, or task automation is wired here.
/// </summary>
public sealed class RuntimeProbeService
{
    public RuntimeProbeResult Scan(P0Config config)
    {
        var resolver = new RoleIdResolver(config.RoleIdRegex);
        var inventory = new Win32WindowInventory().Scan(config.ProcessName, resolver);
        var duplicateRoleIds = inventory.TargetWindows
            .Where(static observation => !string.IsNullOrWhiteSpace(observation.RoleId))
            .GroupBy(static observation => observation.RoleId!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var classified = inventory.TargetWindows
            .Select(observation => BindingEngine.Classify(
                observation,
                config.RegisteredClients,
                duplicateRoleIds))
            .ToArray();
        var bindings = new BindingEngine(config.RegisteredClients).Evaluate(classified);

        return new RuntimeProbeResult(inventory.Processes, classified, bindings);
    }
}
