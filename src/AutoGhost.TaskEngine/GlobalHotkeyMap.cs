using System.Collections.ObjectModel;

namespace AutoGhost.TaskEngine;

/// <summary>
/// Panels that can be toggled by the canonical qnyh keyboard map.
/// </summary>
public enum AutoGhostPanel
{
    Map,
    Bag,
    Chat,
    Activity,
    Skill,
    TaskStatus,
    Benefits,
    Market,
    Party
}

/// <summary>
/// The only supported qnyh panel hotkeys. These are toggles: callers must
/// observe the current panel state before dispatching one of these keys.
/// </summary>
public static class AutoGhostHotkeyMap
{
    private static readonly IReadOnlyDictionary<AutoGhostPanel, char> CanonicalMap =
        new ReadOnlyDictionary<AutoGhostPanel, char>(new Dictionary<AutoGhostPanel, char>
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
        });

    public static IReadOnlyDictionary<AutoGhostPanel, char> Canonical => CanonicalMap;

    public static char GetKey(AutoGhostPanel panel) => CanonicalMap[panel];

    public static bool TryResolve(char key, out AutoGhostPanel panel)
    {
        var normalized = char.ToUpperInvariant(key);
        foreach (var pair in CanonicalMap)
        {
            if (pair.Value == normalized)
            {
                panel = pair.Key;
                return true;
            }
        }

        panel = default;
        return false;
    }
}

public enum AutoGhostObservedUiSurface
{
    Unknown,
    MainWorld,
    Panel
}

public sealed record AutoGhostObservedUiState(
    bool TargetBindingVerified,
    AutoGhostObservedUiSurface Surface,
    AutoGhostPanel? OpenPanel,
    bool MainFrameVerified,
    bool HangZhouVerified,
    bool MainWorldVerified);

public enum AutoGhostNormalizeStepKind
{
    CloseObservedPanel,
    VerifyMainFrame,
    VerifyHangZhou,
    VerifyMainWorld,
    SafeStop
}

public sealed record AutoGhostNormalizeStep(
    AutoGhostNormalizeStepKind Kind,
    AutoGhostPanel? Panel,
    char? Hotkey,
    string Reason);

/// <summary>
/// Builds the global normalization sequence from observed state. It never
/// emits a batch of toggle keys and never invents a key for travel or a
/// location change. Unknown or inconsistent observations fail closed.
/// </summary>
public static class AutoGhostGlobalNormalize
{
    public static IReadOnlyList<AutoGhostNormalizeStep> Build(
        AutoGhostObservedUiState observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        var steps = new List<AutoGhostNormalizeStep>();
        if (!observed.TargetBindingVerified)
        {
            return SafeStop(steps, "Target binding is not verified.");
        }

        var panelWasClosed = false;
        switch (observed.Surface)
        {
            case AutoGhostObservedUiSurface.Unknown:
                return SafeStop(steps, "Current qnyh UI surface is unknown.");
            case AutoGhostObservedUiSurface.MainWorld when observed.OpenPanel is not null:
                return SafeStop(steps, "Main-world observation cannot also report an open panel.");
            case AutoGhostObservedUiSurface.Panel when observed.OpenPanel is null:
                return SafeStop(steps, "A panel surface was observed without identifying the panel.");
            case AutoGhostObservedUiSurface.Panel:
                var panel = observed.OpenPanel!.Value;
                steps.Add(new AutoGhostNormalizeStep(
                    AutoGhostNormalizeStepKind.CloseObservedPanel,
                    panel,
                    AutoGhostHotkeyMap.GetKey(panel),
                    $"Close the observed {panel} panel once, then capture and verify the frame."));
                panelWasClosed = true;
                break;
        }

        if (panelWasClosed || !observed.MainFrameVerified)
        {
            steps.Add(new AutoGhostNormalizeStep(
                AutoGhostNormalizeStepKind.VerifyMainFrame,
                Panel: null,
                Hotkey: null,
                "Capture and verify the qnyh main frame after panel normalization."));
        }

        if (panelWasClosed || !observed.HangZhouVerified)
        {
            steps.Add(new AutoGhostNormalizeStep(
                AutoGhostNormalizeStepKind.VerifyHangZhou,
                Panel: null,
                Hotkey: null,
                "Capture and verify the Hàng Châu location; no guessed travel key is allowed."));
        }

        if (panelWasClosed || !observed.MainWorldVerified)
        {
            steps.Add(new AutoGhostNormalizeStep(
                AutoGhostNormalizeStepKind.VerifyMainWorld,
                Panel: null,
                Hotkey: null,
                "Capture and verify the main-world view before the feature flow."));
        }

        return steps;
    }

    private static IReadOnlyList<AutoGhostNormalizeStep> SafeStop(
        ICollection<AutoGhostNormalizeStep> steps,
        string reason)
    {
        steps.Add(new AutoGhostNormalizeStep(
            AutoGhostNormalizeStepKind.SafeStop,
            Panel: null,
            Hotkey: null,
            reason));
        return steps.ToArray();
    }
}
