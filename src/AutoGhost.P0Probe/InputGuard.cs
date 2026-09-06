namespace AutoGhost.P0Probe;

public sealed class InputGuard
{
    public InputDecision Validate(
        RuntimeBinding? binding,
        WindowObservation currentObservation,
        bool killSwitchActive,
        bool inputEnabled)
    {
        if (killSwitchActive)
        {
            return new InputDecision(false, "Kill switch is active.");
        }

        if (!inputEnabled)
        {
            return new InputDecision(false, "P0 probe is running in dry-run mode; no OS input will be emitted.");
        }

        if (binding is null || binding.State != BindingState.Ready)
        {
            return new InputDecision(false, "No READY runtime binding exists.");
        }

        if (!string.Equals(binding.ClientId, binding.RoleId, StringComparison.Ordinal) ||
            !string.Equals(binding.ClientId, currentObservation.RoleId, StringComparison.Ordinal))
        {
            return new InputDecision(false, "Identity invariant failed: Role ID != registered Client ID.");
        }

        if (binding.ProcessId != currentObservation.ProcessId ||
            binding.WindowHandle != currentObservation.WindowHandle)
        {
            return new InputDecision(false, "Runtime session changed; binding must be revalidated before input.");
        }

        if (currentObservation.IdentityState != IdentityState.Matched)
        {
            return new InputDecision(false, "Current observation is not in MATCHED identity state.");
        }

        return new InputDecision(true,
            "Identity verified, session handle/PID unchanged, kill switch off; guarded input may be dispatched.");
    }
}
