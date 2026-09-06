using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoGhost.ActionModel;
using AutoGhost.P0Probe;

namespace AutoGhost.P2LiveAcceptance;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> Main()
    {
        var evidenceDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable("AUTOGHOST_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts"));
        Directory.CreateDirectory(evidenceDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmmss");
        var evidencePath = Path.Combine(evidenceDirectory, $"p2-live-acceptance-{timestamp}.json");
        var logPath = Path.Combine(evidenceDirectory, $"p2-live-acceptance-{timestamp}.log");
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);

        void Log(string message)
        {
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
        }

        try
        {
            var registryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoGhost",
                "client-registry.json");
            if (!File.Exists(registryPath))
            {
                evidence["status"] = "BLOCKED_REGISTRY_MISSING";
                evidence["registryPath"] = registryPath;
                evidence["reason"] = "Client Registry is missing in this Windows user context; exact authorized Client ID binding cannot be verified.";
                WriteEvidence(evidencePath, evidence);
                Log($"blocked: registry missing path={registryPath}");
                Console.Error.WriteLine($"P2 live acceptance: BLOCKED_REGISTRY_MISSING — {registryPath}");
                Console.Error.WriteLine($"Evidence: {evidencePath}");
                Console.Error.WriteLine($"Log: {logPath}");
                return 2;
            }

            var config = LoadConfig(registryPath);
            var resolver = new RoleIdResolver(config.RoleIdRegex);
            var initial = Scan(config, resolver);
            var selected = SelectReadyTarget(initial);
            if (selected is null)
            {
                throw new InvalidOperationException("No READY qnyh binding with a client rectangle was found.");
            }

            var ready = selected.Binding;
            var observation = selected.Observation;
            var target = selected.Target;
            evidence["timestampUtc"] = DateTimeOffset.UtcNow;
            evidence["registryPath"] = registryPath;
            evidence["target"] = new
            {
                ready.ClientId,
                ready.RoleId,
                ready.ProcessId,
                WindowHandle = $"0x{ready.WindowHandle.ToInt64():X}",
                observation.Title,
                observation.ClassName,
                observation.IsVisible,
                observation.IsMinimized,
                observation.IsForeground,
                ForegroundWindow = $"0x{GetForegroundWindow().ToInt64():X}",
                foregroundWindowExact = GetForegroundWindow() == target.WindowHandle,
                target.ClientWidth,
                target.ClientHeight
            };
            Log($"binding client={ready.ClientId} role={ready.RoleId} pid={ready.ProcessId} hwnd=0x{ready.WindowHandle.ToInt64():X} foreground={observation.IsForeground}");

            var foregroundSamples = new List<ForegroundSample>();
            var foregroundDeadline = DateTime.UtcNow.AddSeconds(30);
            var foregroundStable = false;
            Console.WriteLine(
                $"FOREGROUND_SAMPLING_STARTED target=0x{target.WindowHandle.ToInt64():X}; " +
                "focus the exact qnyh window now and keep it foreground.");
            while (DateTime.UtcNow < foregroundDeadline)
            {
                var sample = ReadForegroundSample(target, observation, ready);
                foregroundSamples.Add(sample);
                Log($"foreground.sample index={sample.Index} exact={sample.ExactBoundHwndForeground} " +
                    $"fgHwnd={sample.ForegroundWindowHandle} fgPid={sample.ForegroundProcessId} " +
                    $"fgThread={sample.ForegroundThreadId} active={sample.HwndActive} focus={sample.HwndFocus} " +
                    $"guiInfo={sample.GuiThreadInfoSucceeded} session={sample.ForegroundSessionId} " +
                    $"desktop={sample.TargetDesktopName}");
                if (foregroundSamples.Count >= 8 &&
                    foregroundSamples.TakeLast(8).All(static item => item.ExactBoundHwndForeground))
                {
                    foregroundStable = true;
                    Console.WriteLine(
                        $"FOREGROUND_STABLE exact=0x{target.WindowHandle.ToInt64():X}; " +
                        "observation recorder will start next.");
                    break;
                }

                PumpMessages();
                Thread.Sleep(50);
            }

            evidence["foregroundSamples"] = foregroundSamples;
            evidence["foregroundStableExact"] = foregroundStable;
            if (!foregroundStable)
            {
                evidence["status"] = "BLOCKED_NOT_FOREGROUND";
                evidence["reason"] = "Exact bound HWND was not foreground for 8 consecutive samples; no recorder hook was started.";
                WriteEvidence(evidencePath, evidence);
                Log("blocked: exact bound HWND foreground was not stable");
                Console.Error.WriteLine("P2 live acceptance: BLOCKED_NOT_FOREGROUND — exact qnyh HWND was not stable foreground.");
                Console.Error.WriteLine($"Evidence: {evidencePath}");
                Console.Error.WriteLine($"Log: {logPath}");
                return 2;
            }

            evidence["target"] = new
            {
                ready.ClientId,
                ready.RoleId,
                ready.ProcessId,
                WindowHandle = $"0x{ready.WindowHandle.ToInt64():X}",
                observation.Title,
                observation.ClassName,
                observation.IsVisible,
                observation.IsMinimized,
                observation.IsForeground,
                ForegroundWindow = $"0x{GetForegroundWindow().ToInt64():X}",
                foregroundWindowExact = GetForegroundWindow() == target.WindowHandle,
                target.ClientWidth,
                target.ClientHeight
            };
            Log($"target-selected client={ready.ClientId} role={ready.RoleId} pid={ready.ProcessId} hwnd=0x{ready.WindowHandle.ToInt64():X} foreground={observation.IsForeground}");

            var identityResolver = new LiveIdentityValidator(target, resolver);
            using var recorder = new WindowsInputRecorder(
                identityResolver.IsVerified,
                GetForegroundWindow,
                targetCandidate => GetForegroundWindow() == targetCandidate.WindowHandle);
            recorder.Start(target);
            Log("recorder.started foreground-only no-dispatch");
            Console.WriteLine(
                $"RECORDER_STARTED observation-only hwnd=0x{target.WindowHandle.ToInt64():X}; " +
                "perform one harmless click or key action in qnyh now (15-second window).");
            var recordingDeadline = DateTime.UtcNow.AddSeconds(15);
            while (recorder.StepCount == 0 && DateTime.UtcNow < recordingDeadline)
            {
                PumpMessages();
                Thread.Sleep(10);
            }

            var action = recorder.Stop("p2-live-qnyh-sample");
            var hookCallbackCount = recorder.HookCallbackCount;
            var rejectedForegroundOrIdentityCount = recorder.RejectedForegroundOrIdentityCount;
            var rejectedInjectedCount = recorder.RejectedInjectedCount;
            Console.WriteLine(
                $"RECORDER_WINDOW_END callbacks={hookCallbackCount} " +
                $"rejectedForegroundOrIdentity={rejectedForegroundOrIdentityCount} " +
                $"rejectedInjected={rejectedInjectedCount} steps={action.Steps.Count}");
            var actionPath = Path.Combine(evidenceDirectory, $"p2-live-action-{timestamp}.action.json");
            new ActionDefinitionStore().Save(actionPath, action);
            var loadedAction = new ActionDefinitionStore().Load(actionPath);
            var validation = ActionDefinitionValidator.Validate(loadedAction);
            Log($"recorder.stopped steps={loadedAction.Steps.Count} action={actionPath}");

            var rebound = Scan(config, resolver);
            var reboundBinding = rebound.Bindings.FirstOrDefault(binding =>
                string.Equals(binding.ClientId, ready.ClientId, StringComparison.Ordinal) &&
                string.Equals(binding.RoleId, ready.RoleId, StringComparison.Ordinal) &&
                binding.State == BindingState.Ready);
            var validDryRun = await RunDryRunAsync(loadedAction, ready.ClientId, ready.RoleId, killSwitchActive: true);
            var mismatch = await RunDryRunAsync(loadedAction, ready.ClientId + "-mismatch", ready.RoleId, killSwitchActive: true);
            var disabled = await RunDryRunAsync(loadedAction, ready.ClientId, ready.RoleId, bindingReady: false, killSwitchActive: true);
            var staleStartRejected = false;
            try
            {
                using var staleRecorder = new WindowsInputRecorder(_ => false, GetForegroundWindow);
                staleRecorder.Start(target);
            }
            catch (InvalidOperationException)
            {
                staleStartRejected = true;
            }

            evidence["recording"] = new
            {
                steps = loadedAction.Steps.Count,
                validation.IsValid,
                actionPath,
                hasNormalizedPoint = loadedAction.Steps.Any(step => step.Point is not null),
                hasTimingField = loadedAction.Steps.All(step => step.DelayBeforeMs >= 0),
                persistenceReloaded = loadedAction.TargetClientId == ready.ClientId,
                hookCallbackCount,
                rejectedForegroundOrIdentityCount,
                rejectedInjectedCount
            };
            evidence["rebind"] = new
            {
                scannedAgain = true,
                resolvedByRoleId = reboundBinding is not null,
                reboundPid = reboundBinding?.ProcessId,
                reboundHwnd = reboundBinding?.WindowHandle.ToInt64().ToString("X")
            };
            evidence["guards"] = new
            {
                mismatchBlocked = mismatch.State == DryRunReplayState.Blocked,
                disabledBlocked = disabled.State == DryRunReplayState.Blocked,
                staleStartRejected,
                killSwitchActive = true,
                automationArmed = false
            };
            evidence["dryRun"] = new
            {
                state = validDryRun.State.ToString(),
                eventCount = validDryRun.Events.Count,
                allDispatchSuppressed = validDryRun.Events.All(item => item.DispatchSuppressed),
                trace = validDryRun.Events
            };
            evidence["status"] = loadedAction.Steps.Count > 0 &&
                validation.IsValid &&
                reboundBinding is not null &&
                validDryRun.State == DryRunReplayState.Completed &&
                validDryRun.Events.All(item => item.DispatchSuppressed) &&
                mismatch.State == DryRunReplayState.Blocked &&
                disabled.State == DryRunReplayState.Blocked &&
                staleStartRejected
                ? "PASS"
                : "VERIFYING";
            WriteEvidence(evidencePath, evidence);
            Log($"final status={evidence["status"]} rebound={reboundBinding is not null} steps={loadedAction.Steps.Count} dryRun={validDryRun.State}");
            Console.WriteLine($"P2 live acceptance: {evidence["status"]}");
            Console.WriteLine($"Evidence: {evidencePath}");
            Console.WriteLine($"Log: {logPath}");
            return evidence["status"] as string == "PASS" ? 0 : 3;
        }
        catch (Exception exception)
        {
            evidence["status"] = "BLOCKED";
            evidence["error"] = $"{exception.GetType().Name}: {exception.Message}";
            WriteEvidence(evidencePath, evidence);
            Log($"fatal {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine($"P2 live acceptance: BLOCKED — {exception.Message}");
            Console.Error.WriteLine($"Evidence: {evidencePath}");
            return 4;
        }
    }

    private static async Task<DryRunReplayReport> RunDryRunAsync(
        ActionDefinition action,
        string clientId,
        string roleId,
        bool bindingReady = true,
        bool killSwitchActive = true)
    {
        return await new DryRunReplayEngine().RunAsync(
            action,
            new ReplayContext(clientId, roleId, bindingReady, killSwitchActive));
    }

    private static P0ScanSnapshot Scan(P0Config config, RoleIdResolver resolver)
    {
        var inventory = new Win32WindowInventory().Scan(config.ProcessName, resolver);
        var duplicateRoleIds = inventory.TargetWindows
            .Where(static observation => !string.IsNullOrWhiteSpace(observation.RoleId))
            .GroupBy(static observation => observation.RoleId!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var classified = inventory.TargetWindows
            .Select(observation => BindingEngine.Classify(observation, config.RegisteredClients, duplicateRoleIds))
            .ToArray();
        var bindings = new BindingEngine(config.RegisteredClients).Evaluate(classified);
        return new P0ScanSnapshot(inventory.Processes, classified, bindings);
    }

    private static ReadyTarget? SelectReadyTarget(P0ScanSnapshot snapshot)
    {
        var candidates = snapshot.Bindings
            .Where(static binding => binding.State == BindingState.Ready)
            .Select(binding => new
            {
                Binding = binding,
                Observation = snapshot.Windows.FirstOrDefault(candidate =>
                    candidate.WindowHandle == binding.WindowHandle &&
                    candidate.ProcessId == binding.ProcessId)
            })
            .Where(static candidate => candidate.Observation?.ClientRect is not null)
            .Select(candidate => new ReadyTarget(
                candidate.Binding,
                candidate.Observation!,
                new RecordingTarget(
                    candidate.Binding.ClientId,
                    candidate.Binding.WindowHandle,
                    ScreenLeft: 0,
                    ScreenTop: 0,
                    ClientWidth: Math.Max(1, candidate.Observation!.ClientRect!.Right - candidate.Observation.ClientRect.Left),
                    ClientHeight: Math.Max(1, candidate.Observation.ClientRect.Bottom - candidate.Observation.ClientRect.Top))))
            .ToArray();
        return candidates
            .OrderByDescending(static candidate => candidate.Observation.IsForeground)
            .ThenByDescending(static candidate => candidate.Observation.IsVisible)
            .ThenBy(static candidate => candidate.Observation.IsMinimized)
            .FirstOrDefault();
    }

    private static P0Config LoadConfig(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var processName = root.TryGetProperty("processName", out var process)
            ? process.GetString() ?? "qnyh"
            : "qnyh";
        var roleRegex = root.TryGetProperty("roleIdRegex", out var regex) ? regex.GetString() : null;
        var clients = (root.TryGetProperty("clients", out var clientArray)
                ? clientArray.EnumerateArray()
                : Enumerable.Empty<JsonElement>())
            .Select(client => new ClientDefinition(
                client.GetProperty("clientId").GetString() ?? string.Empty,
                !client.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()))
            .Where(static client => !string.IsNullOrWhiteSpace(client.ClientId))
            .ToArray();
        return new P0Config(processName, roleRegex, clients);
    }

    private static void WriteEvidence(string path, IReadOnlyDictionary<string, object?> evidence)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var message, nint.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private sealed record P0ScanSnapshot(
        IReadOnlyList<ProcessObservation> Processes,
        IReadOnlyList<WindowObservation> Windows,
        IReadOnlyList<RuntimeBinding> Bindings);

    private sealed record ReadyTarget(
        RuntimeBinding Binding,
        WindowObservation Observation,
        RecordingTarget Target);

    private sealed class LiveIdentityValidator
    {
        private readonly RecordingTarget _target;
        private readonly RoleIdResolver _resolver;

        public LiveIdentityValidator(RecordingTarget target, RoleIdResolver resolver)
        {
            _target = target;
            _resolver = resolver;
        }

        public bool IsVerified(RecordingTarget candidate)
        {
            if (candidate.WindowHandle != _target.WindowHandle ||
                !IsWindow(candidate.WindowHandle) ||
                GetForegroundWindow() != candidate.WindowHandle)
            {
                return false;
            }

            var buffer = new StringBuilder(512);
            _ = GetWindowText(candidate.WindowHandle, buffer, buffer.Capacity);
            return string.Equals(_resolver.Resolve(buffer.ToString()), candidate.ClientId, StringComparison.Ordinal);
        }
    }

    private static ForegroundSample ReadForegroundSample(
        RecordingTarget target,
        WindowObservation observation,
        RuntimeBinding binding)
    {
        var foreground = GetForegroundWindow();
        uint foregroundProcessId = 0;
        var foregroundThreadId = foreground == nint.Zero
            ? 0u
            : GetWindowThreadProcessId(foreground, out foregroundProcessId);
        var guiInfo = new GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<GuiThreadInfo>()
        };
        var guiSucceeded = GetGUIThreadInfo(observation.ThreadId, ref guiInfo);
        var title = new StringBuilder(512);
        if (foreground != nint.Zero)
        {
            _ = GetWindowText(foreground, title, title.Capacity);
        }

        return new ForegroundSample(
            Index: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimestampUtc: DateTimeOffset.UtcNow,
            TargetClientId: binding.ClientId,
            TargetProcessId: binding.ProcessId,
            TargetWindowHandle: $"0x{target.WindowHandle.ToInt64():X}",
            TargetThreadId: observation.ThreadId,
            TargetSessionId: TryGetSessionId(binding.ProcessId),
            TargetDesktopName: observation.DesktopName,
            TargetThreadDesktopHandle: $"0x{GetThreadDesktop(observation.ThreadId).ToInt64():X}",
            ForegroundWindowHandle: $"0x{foreground.ToInt64():X}",
            ForegroundProcessId: foregroundProcessId,
            ForegroundThreadId: foregroundThreadId,
            ForegroundSessionId: TryGetSessionId(foregroundProcessId),
            ForegroundTitle: title.ToString(),
            HwndActive: $"0x{guiInfo.HwndActive.ToInt64():X}",
            HwndFocus: $"0x{guiInfo.HwndFocus.ToInt64():X}",
            GuiThreadInfoSucceeded: guiSucceeded,
            ExactBoundHwndForeground: foreground == target.WindowHandle);
    }

    private static int TryGetSessionId(uint processId)
    {
        if (processId == 0)
        {
            return -1;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    private sealed record ForegroundSample(
        long Index,
        DateTimeOffset TimestampUtc,
        string TargetClientId,
        uint TargetProcessId,
        string TargetWindowHandle,
        uint TargetThreadId,
        int TargetSessionId,
        string TargetDesktopName,
        string TargetThreadDesktopHandle,
        string ForegroundWindowHandle,
        uint ForegroundProcessId,
        uint ForegroundThreadId,
        int ForegroundSessionId,
        string ForegroundTitle,
        string HwndActive,
        string HwndFocus,
        bool GuiThreadInfoSucceeded,
        bool ExactBoundHwndForeground);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint HWnd;
        public uint MessageId;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo threadInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint HwndActive;
        public nint HwndFocus;
        public nint HwndCapture;
        public nint HwndMenuOwner;
        public nint HwndMoveSize;
        public nint HwndCaret;
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint hWnd, uint minFilter, uint maxFilter, uint removeMessage);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

}
