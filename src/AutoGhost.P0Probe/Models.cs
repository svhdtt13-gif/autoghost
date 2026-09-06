namespace AutoGhost.P0Probe;

public enum IdentityState
{
    Identifying,
    RoleIdUnavailable,
    Matched,
    Mismatch,
    DuplicateIdentity,
    Error
}

public enum BindingState
{
    Identifying,
    Ready,
    Error,
    Unbound
}

public sealed record ClientDefinition(
    string ClientId,
    bool Enabled = true,
    IReadOnlyList<string>? SpecialItems = null);

public sealed record WindowRectangle(
    int Left,
    int Top,
    int Right,
    int Bottom);

public sealed record ProcessObservation(
    uint ProcessId,
    string ProcessName,
    string? ProcessPath,
    uint? ParentProcessId,
    IReadOnlyList<uint> ProcessAncestry,
    string ProcessTreeRelation);

public sealed record WindowObservation(
    nint WindowHandle,
    string DesktopName,
    string Title,
    string ClassName,
    bool IsVisible,
    bool IsEnabled,
    nint OwnerWindowHandle,
    nint ParentWindowHandle,
    uint ThreadId,
    uint ProcessId,
    string ProcessName,
    string? ProcessPath,
    uint? ParentProcessId,
    IReadOnlyList<uint> ProcessAncestry,
    string ProcessTreeRelation,
    bool IsTargetProcessTree,
    WindowRectangle? WindowRect,
    WindowRectangle? ClientRect,
    bool IsMinimized,
    bool IsCloaked,
    bool IsForeground,
    string? RoleId,
    IdentityState IdentityState,
    string IdentityReason);

public sealed record RuntimeBinding(
    string ClientId,
    string RoleId,
    uint ProcessId,
    nint WindowHandle,
    Guid SessionId,
    BindingState State,
    string Reason);

public sealed record InputDecision(bool Allowed, string Reason);

public sealed record InputGuardEvidence(
    string Scenario,
    uint ProcessId,
    nint WindowHandle,
    string? RoleId,
    IdentityState IdentityState,
    bool BindingReady,
    bool KillSwitchActive,
    bool InputEnabledForDecision,
    bool Allowed,
    string Reason);

public sealed record CaptureEvidence(
    string Scenario,
    uint ProcessId,
    nint WindowHandle,
    string DesktopName,
    string Method,
    bool OperationSucceeded,
    bool ForegroundObserved,
    bool MinimizedObserved,
    int Width,
    int Height,
    double NonBlankRatio,
    string EvidencePath,
    string Limitation);

public sealed record InputProbeEvidence(
    string Scenario,
    uint ProcessId,
    nint WindowHandle,
    string DesktopName,
    string Key,
    bool ForegroundRequestSucceeded,
    bool ForegroundObserved,
    int EventsInjected,
    bool DispatchSucceeded,
    string Limitation)
{
    public nint ForegroundWindowBefore { get; init; }
    public uint ForegroundProcessIdBefore { get; init; }
    public nint ForegroundWindowAfter { get; init; }
    public uint ForegroundProcessIdAfter { get; init; }
    public string ForegroundWindowTitleBefore { get; init; } = string.Empty;
    public string CurrentIntegrityLevel { get; init; } = "Unknown";
    public string TargetIntegrityLevel { get; init; } = "Unknown";
    public bool CurrentProcessElevated { get; init; }
    public bool TargetProcessElevated { get; init; }
    public int SendInputLastWin32Error { get; init; }
    public int InputStructSize { get; init; }
    public int InputStructSizeExpected { get; init; }
    public string PInvokeLayout { get; init; } = string.Empty;
}

public sealed record P0Config(
    string ProcessName = "qnyh",
    string? RoleIdRegex = null,
    IReadOnlyList<ClientDefinition>? Clients = null)
{
    public IReadOnlyList<ClientDefinition> RegisteredClients => Clients ?? Array.Empty<ClientDefinition>();
}

public sealed record P0ScanReport(
    DateTimeOffset TimestampUtc,
    string ProcessName,
    string? RoleIdRegex,
    IReadOnlyList<ProcessObservation> Processes,
    IReadOnlyList<WindowObservation> WindowCandidates,
    IReadOnlyList<WindowObservation> Windows,
    IReadOnlyList<RuntimeBinding> Bindings,
    IReadOnlyList<InputGuardEvidence> InputGuardDecisions,
    IReadOnlyList<CaptureEvidence> Captures,
    IReadOnlyList<InputProbeEvidence> InputTests,
    IReadOnlyList<string> Warnings);
