namespace AutoGhost.TaskEngine;

public enum AutoGhostMainFrameState
{
    Unknown,
    Normalized
}

public enum AutoGhostHangZhouState
{
    Unknown,
    Verified
}

public enum AutoGhostMainWorldViewState
{
    Unknown,
    Verified
}

/// <summary>
/// Shared target state required by every feature that can send active input
/// to qnyh. Read-only process/window inspection does not need this state.
/// </summary>
public record AutoGhostInteractionTarget(
    string ClientId,
    string RoleId,
    nint WindowHandle,
    bool IsForeground,
    bool KillSwitchActive,
    bool AutomationArmed,
    AutoGhostMainFrameState MainFrameState = AutoGhostMainFrameState.Unknown,
    AutoGhostHangZhouState HangZhouState = AutoGhostHangZhouState.Unknown,
    AutoGhostMainWorldViewState MainWorldViewState = AutoGhostMainWorldViewState.Unknown);

/// <summary>
/// Global fail-closed invariant for active qnyh interaction:
/// bind/verify target, normalize to the main frame, verify Hang Zhou, verify
/// the main-world view, then allow a feature-specific flow.
/// </summary>
public static class AutoGhostGlobalInteractionGuard
{
    public static void RequireActiveInteractionReady(
        AutoGhostInteractionTarget target,
        string featureName)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.ClientId) ||
            string.IsNullOrWhiteSpace(target.RoleId) ||
            !string.Equals(target.ClientId, target.RoleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{featureName} requires an exact registered Client ID and Role ID match.");
        }

        if (target.WindowHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"{featureName} requires a non-zero bound HWND.");
        }

        if (!target.IsForeground)
        {
            throw new InvalidOperationException(
                $"{featureName} requires the exact bound HWND to be foreground.");
        }

        if (target.KillSwitchActive)
        {
            throw new InvalidOperationException(
                $"Kill Switch is active; {featureName} input is blocked.");
        }

        if (!target.AutomationArmed)
        {
            throw new InvalidOperationException(
                $"Automation is not armed; {featureName} input is blocked.");
        }

        if (target.MainFrameState != AutoGhostMainFrameState.Normalized)
        {
            throw new InvalidOperationException(
                $"{featureName} requires GLOBAL_NORMALIZE to the qnyh main frame.");
        }

        if (target.HangZhouState != AutoGhostHangZhouState.Verified)
        {
            throw new InvalidOperationException(
                $"{featureName} requires verified HANG_ZHOU state before input.");
        }

        if (target.MainWorldViewState != AutoGhostMainWorldViewState.Verified)
        {
            throw new InvalidOperationException(
                $"{featureName} requires verified MAIN_WORLD_VIEW before input.");
        }
    }
}
