using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoGhost.P0Probe;

namespace AutoGhost.P2ContextDiagnostic;

public static class Program
{
    private const string DefaultRoleIdRegex = @"Role\s*\[(?<roleId>\d+)\]";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;
    private const int TokenIntegrityLevel = 25;
    private const int TokenSessionId = 12;
    private const int TokenUserObjectName = 2;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopSwitchDesktop = 0x0100;
    private const uint UoiName = 2;
    private const uint InvalidSessionId = 0xFFFFFFFF;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main()
    {
        var evidenceDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable("AUTOGHOST_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts"));
        Directory.CreateDirectory(evidenceDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmmss");
        var evidencePath = Path.Combine(evidenceDirectory, $"p2-context-diagnostic-{timestamp}.json");
        var logPath = Path.Combine(evidenceDirectory, $"p2-context-diagnostic-{timestamp}.log");

        try
        {
            var activeConsoleSessionId = WTSGetActiveConsoleSessionId();
            var currentProcess = Process.GetCurrentProcess();
            var currentContext = ReadCurrentContext(currentProcess, activeConsoleSessionId);
            var qnyhProcesses = Process.GetProcessesByName("qnyh")
                .Select(process => ReadProcessSnapshot(process, activeConsoleSessionId))
                .ToArray();
            var explorerProcesses = Process.GetProcessesByName("explorer")
                .Select(process => ReadProcessSnapshot(process, activeConsoleSessionId))
                .ToArray();

            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoGhost",
                "client-registry.json");
            var registryPresent = File.Exists(configPath);
            var config = LoadConfig(configPath);
            var resolver = new RoleIdResolver(config.RoleIdRegex);
            var inventory = new Win32WindowInventory().Scan(config.ProcessName, resolver);
            var boundWindowCandidates = inventory.TargetWindows
                .Where(static window => !string.IsNullOrWhiteSpace(window.RoleId))
                .Select(window => new
                {
                    clientId = window.RoleId,
                    roleId = window.RoleId,
                    processId = window.ProcessId,
                    windowHandle = window.WindowHandle,
                    hwnd = $"0x{window.WindowHandle.ToInt64():X}",
                    title = window.Title,
                    className = window.ClassName,
                    visible = window.IsVisible,
                    minimized = window.IsMinimized,
                    targetDesktop = window.DesktopName,
                    targetThreadId = window.ThreadId,
                    targetThreadDesktop = GetObjectName(GetThreadDesktop(window.ThreadId)),
                    sessionId = TryGetSessionId(window.ProcessId),
                    clientRect = window.ClientRect
                })
                .ToArray();

            var foregroundSamples = new List<ForegroundSnapshot>();
            var foregroundDeadline = DateTime.UtcNow.AddSeconds(10);
            ForegroundSnapshot? foreground = null;
            while (DateTime.UtcNow < foregroundDeadline)
            {
                foreground = ReadForegroundSnapshot(
                    boundWindowCandidates.Select(static window => window.windowHandle).ToArray());
                foregroundSamples.Add(foreground);
                if (foreground.exactBoundHwnds.Count > 0)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            foreground ??= ReadForegroundSnapshot(
                boundWindowCandidates.Select(static window => window.windowHandle).ToArray());
            var boundWindows = boundWindowCandidates
                .Select(window => new
                {
                    window.clientId,
                    window.roleId,
                    window.processId,
                    window.hwnd,
                    window.title,
                    window.className,
                    window.visible,
                    window.minimized,
                    exactForeground = foreground.exactBoundHwnds.Contains(window.hwnd, StringComparer.Ordinal),
                    window.targetDesktop,
                    window.targetThreadId,
                    window.targetThreadDesktop,
                    window.sessionId,
                    window.clientRect
                })
                .ToArray();

            var contextMatchesActiveSession =
                currentContext.sessionId == activeConsoleSessionId &&
                activeConsoleSessionId != InvalidSessionId;
            var qnyhInActiveSession = qnyhProcesses.Any(process => process.sessionId == activeConsoleSessionId);
            var explorerInActiveSession = explorerProcesses.Any(process => process.sessionId == activeConsoleSessionId);
            var inputDesktopAvailable = !string.Equals(
                currentContext.inputDesktopName,
                "<unavailable>",
                StringComparison.Ordinal);
            var currentThreadOnInputDesktop = inputDesktopAvailable && string.Equals(
                currentContext.threadDesktopName,
                currentContext.inputDesktopName,
                StringComparison.Ordinal);
            var boundDesktopMatchesInput = boundWindows.Length > 0 && boundWindows.All(window =>
                string.Equals(window.targetDesktop, currentContext.inputDesktopName, StringComparison.Ordinal));
            var qnyhIntegrityMatches = qnyhProcesses.Length > 0 && qnyhProcesses.All(process =>
                string.Equals(process.integrityLevel, currentContext.integrityLevel, StringComparison.Ordinal));
            var exactBoundForegroundObserved = foreground.exactBoundHwnds.Count > 0;
            var contextStatus = contextMatchesActiveSession && qnyhInActiveSession && explorerInActiveSession &&
                                inputDesktopAvailable && currentThreadOnInputDesktop && boundDesktopMatchesInput &&
                                qnyhIntegrityMatches && exactBoundForegroundObserved
                ? "CONTEXT_OBSERVED"
                : "BLOCKED_BY_INTERACTIVE_CONTEXT";

            var contextComparisons = new
            {
                currentSessionMatchesActiveConsole = contextMatchesActiveSession,
                qnyhInActiveSession,
                explorerInActiveSession,
                inputDesktopAvailable,
                currentThreadOnInputDesktop,
                boundDesktopMatchesInput,
                qnyhIntegrityMatches,
                exactBoundForegroundObserved,
                currentIntegrity = currentContext.integrityLevel,
                qnyhIntegrityLevels = qnyhProcesses
                    .Select(process => new { process.processId, process.integrityLevel, process.elevated })
                    .ToArray()
            };

            var evidence = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                status = contextStatus,
                activeConsoleSessionId,
                currentProcess = currentContext,
                registryPath = configPath,
                registryPresent,
                bindingAuthorization = registryPresent ? "REGISTRY_PRESENT" : "NOT_VERIFIED_REGISTRY_MISSING",
                qnyhProcesses,
                explorerProcesses,
                boundWindows,
                foreground,
                foregroundSamples,
                contextComparisons,
                notes = new[]
                {
                    "Read-only diagnostic; no input was dispatched.",
                    "Exact foreground guard remains required: GetForegroundWindow() == bound HWND.",
                    contextStatus == "BLOCKED_BY_INTERACTIVE_CONTEXT"
                        ? "Run this executable manually from the visible interactive Windows desktop and compare the resulting context."
                        : registryPresent
                            ? "Context is observable; rerun P2 live acceptance in this same context."
                            : "Context is observable, but exact Client Registry binding is not verified because the registry file is missing."
                }
            };
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
            File.WriteAllText(
                logPath,
                $"{DateTimeOffset.Now:O} status={contextStatus} currentPid={currentContext.processId} " +
                $"currentSession={currentContext.sessionId} activeConsoleSession={activeConsoleSessionId} " +
                $"windowStation={currentContext.windowStationName} threadDesktop={currentContext.threadDesktopName} " +
                $"inputDesktop={currentContext.inputDesktopName} foreground={foreground.hwnd} " +
                $"threadOnInputDesktop={currentThreadOnInputDesktop} boundDesktopMatchesInput={boundDesktopMatchesInput} " +
                $"qnyhIntegrityMatches={qnyhIntegrityMatches} exactBoundForegroundObserved={exactBoundForegroundObserved} " +
                $"foregroundSamples={foregroundSamples.Count}{Environment.NewLine}",
                Encoding.UTF8);
            Console.WriteLine($"P2 context diagnostic: {contextStatus}");
            Console.WriteLine($"Evidence: {evidencePath}");
            Console.WriteLine($"Log: {logPath}");
            return contextStatus == "CONTEXT_OBSERVED" ? 0 : 2;
        }
        catch (Exception exception)
        {
            var error = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                status = "ERROR",
                error = $"{exception.GetType().Name}: {exception.Message}"
            };
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(error, JsonOptions), Encoding.UTF8);
            File.WriteAllText(logPath, $"{DateTimeOffset.Now:O} {error.error}{Environment.NewLine}", Encoding.UTF8);
            Console.Error.WriteLine($"P2 context diagnostic: ERROR — {exception.Message}");
            return 3;
        }
    }

    private static ForegroundSnapshot ReadForegroundSnapshot(IReadOnlyCollection<nint> candidateHandles)
    {
        var foregroundWindow = GetForegroundWindow();
        var foregroundPid = foregroundWindow == nint.Zero
            ? 0u
            : GetWindowThreadProcessId(foregroundWindow, out var foregroundPidValue) == 0
                ? 0u
                : foregroundPidValue;
        var foregroundThreadId = foregroundWindow == nint.Zero
            ? 0u
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var exactBoundHwnds = candidateHandles
            .Where(candidate => candidate == foregroundWindow)
            .Select(candidate => $"0x{candidate.ToInt64():X}")
            .ToArray();
        return new ForegroundSnapshot(
            hwnd: $"0x{foregroundWindow.ToInt64():X}",
            pid: foregroundPid,
            threadId: foregroundThreadId,
            sessionId: TryGetSessionId(foregroundPid),
            title: ReadWindowText(foregroundWindow),
            exactBoundHwnds: exactBoundHwnds);
    }

    private static CurrentContext ReadCurrentContext(Process process, uint activeConsoleSessionId)
    {
        var threadId = GetCurrentThreadId();
        return new CurrentContext(
            processId: process.Id,
            sessionId: process.SessionId,
            activeConsoleSessionId: activeConsoleSessionId,
            windowStationName: GetObjectName(GetProcessWindowStation()),
            threadDesktopName: GetObjectName(GetThreadDesktop(threadId)),
            inputDesktopName: GetInputDesktopName(),
            integrityLevel: ReadIntegrityLevel(process.Handle, out var elevated),
            elevated: elevated,
            processPath: TryGetProcessPath(process));
    }

    private static ProcessSnapshot ReadProcessSnapshot(Process process, uint activeConsoleSessionId)
    {
        try
        {
            return new ProcessSnapshot(
                processId: process.Id,
                processName: process.ProcessName,
                processPath: TryGetProcessPath(process),
                sessionId: process.SessionId,
                activeConsoleSession: process.SessionId == activeConsoleSessionId,
                integrityLevel: ReadProcessIntegrity(process.Id, out var elevated),
                elevated: elevated);
        }
        catch (Exception exception)
        {
            return new ProcessSnapshot(
                processId: process.Id,
                processName: process.ProcessName,
                processPath: null,
                sessionId: -1,
                activeConsoleSession: false,
                integrityLevel: "Unavailable",
                elevated: false,
                error: exception.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadProcessIntegrity(int processId, out bool elevated)
    {
        elevated = false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            return "Unavailable";
        }

        try
        {
            return ReadIntegrityLevel(handle, out elevated);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int TryGetSessionId(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return -1;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadIntegrityLevel(nint processHandle, out bool elevated)
    {
        elevated = false;
        if (processHandle == nint.Zero || !OpenProcessToken(processHandle, TokenQuery, out var token))
        {
            return "Unavailable";
        }

        try
        {
            var elevationSize = Marshal.SizeOf<TokenElevationData>();
            var elevationBuffer = Marshal.AllocHGlobal(elevationSize);
            try
            {
                if (GetTokenInformation(token, TokenElevationInformationClass, elevationBuffer, elevationSize, out _))
                {
                    elevated = Marshal.PtrToStructure<TokenElevationData>(elevationBuffer).TokenIsElevated != 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(elevationBuffer);
            }

            if (!GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out var required) &&
                required <= 0)
            {
                return "Unavailable";
            }

            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out _))
                {
                    return "Unavailable";
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Label.Sid));
                var rid = Marshal.ReadInt32(GetSidSubAuthority(label.Label.Sid, (uint)Math.Max(0, count - 1)));
                return rid switch
                {
                    >= 0x00005000 => "System",
                    >= 0x00003000 => "High",
                    >= 0x00002000 => "Medium",
                    >= 0x00001000 => "Low",
                    _ => $"RID-{rid:X}"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string GetInputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == nint.Zero)
        {
            return "<unavailable>";
        }

        try
        {
            return GetObjectName(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static string GetObjectName(nint handle)
    {
        if (handle == nint.Zero)
        {
            return "<unavailable>";
        }

        _ = GetUserObjectInformation(handle, UoiName, null, 0, out var required);
        if (required <= 0)
        {
            return "<unavailable>";
        }

        var buffer = new StringBuilder(required);
        return GetUserObjectInformation(handle, UoiName, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : "<unavailable>";
    }

    private static string ReadWindowText(nint handle)
    {
        if (handle == nint.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(512);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static P0Config LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            return new P0Config(
                ProcessName: "qnyh",
                RoleIdRegex: DefaultRoleIdRegex,
                Clients: Array.Empty<ClientDefinition>());
        }

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

    private sealed record CurrentContext(
        int processId,
        int sessionId,
        uint activeConsoleSessionId,
        string windowStationName,
        string threadDesktopName,
        string inputDesktopName,
        string integrityLevel,
        bool elevated,
        string? processPath);

    private sealed record ForegroundSnapshot(
        string hwnd,
        uint pid,
        uint threadId,
        int sessionId,
        string title,
        IReadOnlyList<string> exactBoundHwnds);

    private sealed record ProcessSnapshot(
        int processId,
        string processName,
        string? processPath,
        int sessionId,
        bool activeConsoleSession,
        string integrityLevel,
        bool elevated,
        string? error = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationData
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        nint handle,
        uint index,
        StringBuilder? information,
        int length,
        out int needed);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
}
