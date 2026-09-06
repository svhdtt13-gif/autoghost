namespace AutoGhost.P0Probe;

public sealed class BindingEngine
{
    private readonly HashSet<string> _registeredClientIds;

    public BindingEngine(IEnumerable<ClientDefinition> clients)
    {
        _registeredClientIds = clients
            .Where(static client => client.Enabled)
            .Select(static client => client.ClientId.Trim())
            .Where(static clientId => clientId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<RuntimeBinding> Evaluate(IReadOnlyList<WindowObservation> observations)
    {
        var duplicateRoleIds = observations
            .Where(static observation => !string.IsNullOrWhiteSpace(observation.RoleId))
            .GroupBy(static observation => observation.RoleId!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<RuntimeBinding>();
        foreach (var observation in observations)
        {
            if (observation.IdentityState != IdentityState.Matched)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(observation.RoleId))
            {
                continue;
            }

            var roleId = observation.RoleId;
            if (duplicateRoleIds.Contains(roleId))
            {
                continue;
            }

            if (!_registeredClientIds.Contains(roleId))
            {
                continue;
            }

            result.Add(new RuntimeBinding(
                ClientId: roleId,
                RoleId: roleId,
                ProcessId: observation.ProcessId,
                WindowHandle: observation.WindowHandle,
                SessionId: Guid.NewGuid(),
                State: BindingState.Ready,
                Reason: "Role ID exactly matched a registered Client ID and was unique in this scan."));
        }

        return result;
    }

    public static WindowObservation Classify(
        WindowObservation observation,
        IReadOnlyList<ClientDefinition> clients,
        IReadOnlySet<string> duplicateRoleIds)
    {
        if (string.IsNullOrWhiteSpace(observation.RoleId))
        {
            return observation with
            {
                IdentityState = IdentityState.RoleIdUnavailable,
                IdentityReason = "No Role ID was resolved. Raw title is evidence only; no bind is allowed."
            };
        }

        if (duplicateRoleIds.Contains(observation.RoleId))
        {
            return observation with
            {
                IdentityState = IdentityState.DuplicateIdentity,
                IdentityReason = "Role ID appeared on more than one window in the same scan; fail closed."
            };
        }

        if (!clients.Any(client => client.Enabled &&
                                   string.Equals(client.ClientId.Trim(), observation.RoleId,
                                       StringComparison.Ordinal)))
        {
            return observation with
            {
                IdentityState = IdentityState.Mismatch,
                IdentityReason = "Role ID did not match an enabled registered Client ID; input is forbidden."
            };
        }

        return observation with
        {
            IdentityState = IdentityState.Matched,
            IdentityReason = "Role ID exactly matched one enabled registered Client ID."
        };
    }
}

public sealed class BindingReconciler
{
    public RuntimeBinding? Reconcile(RuntimeBinding? previous, WindowObservation current)
    {
        if (current.IdentityState != IdentityState.Matched ||
            string.IsNullOrWhiteSpace(current.RoleId))
        {
            return null;
        }

        if (previous is null)
        {
            return new RuntimeBinding(
                ClientId: current.RoleId,
                RoleId: current.RoleId,
                ProcessId: current.ProcessId,
                WindowHandle: current.WindowHandle,
                SessionId: Guid.NewGuid(),
                State: BindingState.Ready,
                Reason: "Initial identity verification completed.");
        }

        if (!string.Equals(previous.ClientId, current.RoleId, StringComparison.Ordinal) ||
            !string.Equals(previous.RoleId, current.RoleId, StringComparison.Ordinal))
        {
            return null;
        }

        if (previous.ProcessId == current.ProcessId &&
            previous.WindowHandle == current.WindowHandle)
        {
            return previous with
            {
                State = BindingState.Ready,
                Reason = "Same verified runtime session remains active."
            };
        }

        return new RuntimeBinding(
            ClientId: current.RoleId,
            RoleId: current.RoleId,
            ProcessId: current.ProcessId,
            WindowHandle: current.WindowHandle,
            SessionId: Guid.NewGuid(),
            State: BindingState.Ready,
            Reason: "PID/HWND changed; old session invalidated and a new verified session was created.");
    }
}
