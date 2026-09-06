using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoGhost.P0Probe;

namespace AutoGhost.P2InputOriginDiagnostic;

public static class Program
{
    private const uint ErrorClassAlreadyExists = 1410;

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
        var evidencePath = Path.Combine(evidenceDirectory, $"p2.1-input-origin-{timestamp}.json");
        var logPath = Path.Combine(evidenceDirectory, $"p2.1-input-origin-{timestamp}.log");

        try
        {
            var registryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoGhost",
                "client-registry.json");
            if (!File.Exists(registryPath))
            {
                throw new InvalidOperationException($"Client Registry is missing: {registryPath}");
            }

            var config = LoadConfig(registryPath);
            var resolver = new RoleIdResolver(config.RoleIdRegex);
            var target = ResolveTarget(config, resolver);

            Console.WriteLine(
                $"P2.1 target Role={target.RoleId} PID={target.ProcessId} " +
                $"HWND=0x{target.WindowHandle.ToInt64():X}");

            using var probe = new InputOriginProbe(target, resolver);
            probe.Start();

            RunStage(probe, "NOTEPAD", "Focus Notepad and perform one mouse action and one key action now", 12);
            RunStage(probe, "QNYH", "Focus the exact qnyh window and perform one mouse action and one key action now", 12);

            var snapshot = probe.Snapshot();
            probe.Stop();

            var rawCount = snapshot.Stages.Sum(stage => stage.RawInput.Count);
            var rawDeviceHandleCount = snapshot.Stages.Sum(stage => stage.RawInput.Count(item =>
                !string.Equals(item.DeviceHandle, "0x0", StringComparison.OrdinalIgnoreCase)));
            var nonInjectedCount = snapshot.Stages.Sum(stage =>
                stage.LowLevel.Count(item => !item.Injected));
            var status = nonInjectedCount > 0
                ? "NONINJECTED_OBSERVED"
                : rawDeviceHandleCount > 0
                    ? "RAW_DEVICE_HANDLE_OBSERVED_LOWLEVEL_INJECTED_ONLY"
                    : rawCount > 0
                        ? "RAW_INPUT_NO_DEVICE_HANDLE_LOWLEVEL_INJECTED_ONLY"
                    : "NO_RAW_INPUT_OBSERVED";

            var evidence = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                status,
                scope = new
                {
                    registryPath,
                    processName = config.ProcessName,
                    roleIdRegex = config.RoleIdRegex,
                    target = new
                    {
                        target.ClientId,
                        target.RoleId,
                        target.ProcessId,
                        hwnd = $"0x{target.WindowHandle.ToInt64():X}"
                    },
                    recorderUsed = false,
                    replayUsed = false,
                    dispatchUsed = false
                },
                registration = snapshot.Registration,
                stages = snapshot.Stages.Select(SummarizeStage).ToArray(),
                notes = new[]
                {
                    "Raw Input is diagnostic evidence only; it is not used to record or replay actions.",
                    "LLKHF_INJECTED and LLMHF_INJECTED remain unchanged as the P2 recording gate.",
                    status == "NONINJECTED_OBSERVED"
                        ? "At least one non-injected low-level event was observed; continue P2 acceptance review."
                        : status == "RAW_DEVICE_HANDLE_OBSERVED_LOWLEVEL_INJECTED_ONLY"
                            ? "Raw Input exposed a device handle, but every low-level event was injected. Keep P2 blocked and investigate the environment only."
                            : status == "RAW_INPUT_NO_DEVICE_HANDLE_LOWLEVEL_INJECTED_ONLY"
                                ? "Raw Input messages arrived without a device handle, and every low-level event was injected. Physical-device attribution is unavailable; keep P2 blocked."
                        : "No Raw Input device event was observed during the timed matrix."
                }
            };

            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
            File.WriteAllText(logPath, BuildLog(snapshot, status), Encoding.UTF8);
            Console.WriteLine($"P2.1 input-origin diagnostic: {status}");
            Console.WriteLine($"Evidence: {evidencePath}");
            Console.WriteLine($"Log: {logPath}");
            return 0;
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
            Console.Error.WriteLine($"P2.1 input-origin diagnostic: ERROR — {exception.Message}");
            Console.Error.WriteLine($"Evidence: {evidencePath}");
            return 3;
        }
    }

    private static void RunStage(InputOriginProbe probe, string stage, string prompt, int seconds)
    {
        probe.BeginStage(stage);
        Console.WriteLine($"P2.1_{stage}_STARTED {prompt} ({seconds}s)");
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            while (PeekMessage(out var message, nint.Zero, 0, 0, 1))
            {
                if (message.MessageId == WmQuit)
                {
                    return;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            Thread.Sleep(5);
        }

        Console.WriteLine($"P2.1_{stage}_ENDED");
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

    private static TargetIdentity ResolveTarget(P0Config config, RoleIdResolver resolver)
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
        var binding = new BindingEngine(config.RegisteredClients).Evaluate(classified)
            .Where(static item => item.State == BindingState.Ready)
            .Select(item => new
            {
                Binding = item,
                Observation = classified.FirstOrDefault(observation =>
                    observation.ProcessId == item.ProcessId && observation.WindowHandle == item.WindowHandle)
            })
            .Where(static item => item.Observation?.ClientRect is not null)
            .OrderByDescending(static item => item.Observation!.IsVisible)
            .ThenBy(static item => item.Observation!.IsMinimized)
            .FirstOrDefault();

        if (binding is null || binding.Observation is null)
        {
            throw new InvalidOperationException("No READY qnyh binding with a client rectangle was found.");
        }

        return new TargetIdentity(
            binding.Binding.ClientId,
            binding.Binding.RoleId,
            binding.Binding.ProcessId,
            binding.Binding.WindowHandle);
    }

    private static object SummarizeStage(StageCapture stage)
    {
        return new
        {
            stage = stage.Name,
            rawInputCount = stage.RawInput.Count,
            rawInputDeviceCount = stage.RawInput
                .Select(item => item.DeviceName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            rawDeviceNames = stage.RawInput
                .Select(item => item.DeviceName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            lowLevelCount = stage.LowLevel.Count,
            lowLevelInjectedCount = stage.LowLevel.Count(static item => item.Injected),
            lowLevelNonInjectedCount = stage.LowLevel.Count(static item => !item.Injected),
            lowLevelExactQnyhCount = stage.LowLevel.Count(static item => item.Foreground.ForegroundExactQnyh),
            lowLevelNotepadCount = stage.LowLevel.Count(static item => item.Foreground.ForegroundIsNotepad),
            rawInput = stage.RawInput,
            lowLevel = stage.LowLevel
        };
    }

    private static string BuildLog(InputOriginSnapshot snapshot, string status)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{DateTimeOffset.Now:O} status={status}");
        builder.AppendLine($"{DateTimeOffset.Now:O} rawRegistration={snapshot.Registration.Registered} error={snapshot.Registration.Error}");
        foreach (var stage in snapshot.Stages)
        {
            builder.AppendLine(
                $"{DateTimeOffset.Now:O} stage={stage.Name} raw={stage.RawInput.Count} " +
                $"lowLevel={stage.LowLevel.Count} injected={stage.LowLevel.Count(item => item.Injected)} " +
                $"nonInjected={stage.LowLevel.Count(item => !item.Injected)} " +
                $"exactQnyh={stage.LowLevel.Count(item => item.Foreground.ForegroundExactQnyh)} " +
                $"notepad={stage.LowLevel.Count(item => item.Foreground.ForegroundIsNotepad)}");
        }

        return builder.ToString();
    }

    private const uint WmQuit = 0x0012;

    private sealed record TargetIdentity(string ClientId, string RoleId, uint ProcessId, nint WindowHandle);

    private sealed class StageCapture
    {
        public StageCapture(string name) => Name = name;

        public string Name { get; }
        public List<RawInputObservation> RawInput { get; } = new();
        public List<LowLevelObservation> LowLevel { get; } = new();
    }

    private sealed record InputOriginSnapshot(
        RegistrationSnapshot Registration,
        IReadOnlyList<StageCapture> Stages);

    private sealed record RegistrationSnapshot(bool Registered, int Error);

    private sealed record RawInputObservation(
        DateTimeOffset TimestampUtc,
        string Stage,
        uint RawType,
        string DeviceHandle,
        string DeviceName,
        uint InputCode,
        ForegroundSnapshot Foreground);

    private sealed record LowLevelObservation(
        DateTimeOffset TimestampUtc,
        string Stage,
        string Kind,
        bool Injected,
        uint Flags,
        ForegroundSnapshot Foreground);

    private sealed record ForegroundSnapshot(
        string Hwnd,
        uint ProcessId,
        string ProcessName,
        string Title,
        string? RoleId,
        bool ForegroundExactQnyh,
        bool ForegroundIsNotepad);

    private sealed class InputOriginProbe : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const uint WmInput = 0x00FF;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevDevNotify = 0x00002000;
        private const uint RidInput = 0x10000003;
        private const uint RidiDeviceName = 0x20000007;
        private const uint RimInput = 0;
        private const uint RimInputSink = 1;
        private const uint LlkhfInjected = 0x00000010;
        private const uint LlmhfInjected = 0x00000001;
        private static readonly nint HwndMessage = new(-3);

        private static InputOriginProbe? _active;
        private readonly TargetIdentity _target;
        private readonly RoleIdResolver _resolver;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private readonly LowLevelMouseProc _mouseProc;
        private readonly WndProc _wndProc;
        private readonly List<StageCapture> _stages = new();
        private string _stage = "NONE";
        private nint _window;
        private nint _keyboardHook;
        private nint _mouseHook;
        private nint _module;
        private ushort _classAtom;
        private bool _registered;
        private int _registrationError;
        private bool _disposed;

        public InputOriginProbe(TargetIdentity target, RoleIdResolver resolver)
        {
            _target = target;
            _resolver = resolver;
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
            _wndProc = WindowCallback;
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _module = GetModuleHandle(null);
            var className = $"AutoGhost.P2.1.RawInput.{Environment.ProcessId}";
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProc = _wndProc,
                Instance = _module,
                ClassName = className
            };
            _classAtom = RegisterClassEx(windowClass);
            if (_classAtom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
            }

            _window = CreateWindowEx(
                0,
                className,
                "AutoGhost P2.1 Raw Input",
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                nint.Zero,
                _module,
                nint.Zero);
            if (_window == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx message-only window failed.");
            }

            var devices = new[]
            {
                new RawInputDevice(0x01, 0x02, RidevInputSink | RidevDevNotify, _window),
                new RawInputDevice(0x01, 0x06, RidevInputSink | RidevDevNotify, _window)
            };
            _registered = RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputDevice>());
            _registrationError = _registered ? 0 : Marshal.GetLastWin32Error();
            if (!_registered)
            {
                throw new Win32Exception(_registrationError, "RegisterRawInputDevices failed.");
            }

            _active = this;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, _module, 0);
            if (_keyboardHook == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx keyboard failed.");
            }

            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, _module, 0);
            if (_mouseHook == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx mouse failed.");
            }
        }

        public void BeginStage(string stage)
        {
            _stage = stage;
            _stages.Add(new StageCapture(stage));
        }

        public InputOriginSnapshot Snapshot()
        {
            return new InputOriginSnapshot(
                new RegistrationSnapshot(_registered, _registrationError),
                _stages.ToArray());
        }

        public void Stop()
        {
            if (_keyboardHook != nint.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = nint.Zero;
            }

            if (_mouseHook != nint.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = nint.Zero;
            }

            if (_window != nint.Zero)
            {
                DestroyWindow(_window);
                _window = nint.Zero;
            }

            if (_classAtom != 0)
            {
                UnregisterClass(_classAtom, _module);
                _classAtom = 0;
            }

            _active = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        private nint WindowCallback(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == WmInput)
            {
                ReadRawInput(wParam, lParam);
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private void ReadRawInput(nint wParam, nint lParam)
        {
            var stage = CurrentStage();
            if (stage is null)
            {
                return;
            }

            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            if (GetRawInputData(lParam, RidInput, nint.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
                {
                    return;
                }

                var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                stage.RawInput.Add(new RawInputObservation(
                    DateTimeOffset.UtcNow,
                    stage.Name,
                    header.Type,
                    $"0x{header.Device.ToInt64():X}",
                    GetDeviceName(header.Device),
                    (uint)wParam.ToInt64(),
                    ReadForeground()));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private string GetDeviceName(nint device)
        {
            if (device == nint.Zero)
            {
                return string.Empty;
            }

            uint size = 0;
            if (GetRawInputDeviceInfo(device, RidiDeviceName, null, ref size) == uint.MaxValue || size == 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder((int)size + 1);
            return GetRawInputDeviceInfo(device, RidiDeviceName, buffer, ref size) == uint.MaxValue
                ? string.Empty
                : buffer.ToString();
        }

        private nint KeyboardCallback(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                AddLowLevel("keyboard", (data.Flags & LlkhfInjected) != 0, data.Flags);
            }

            return CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        private nint MouseCallback(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                AddLowLevel("mouse", (data.Flags & LlmhfInjected) != 0, data.Flags);
            }

            return CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        private void AddLowLevel(string kind, bool injected, uint flags)
        {
            var stage = CurrentStage();
            if (stage is null)
            {
                return;
            }

            stage.LowLevel.Add(new LowLevelObservation(
                DateTimeOffset.UtcNow,
                stage.Name,
                kind,
                injected,
                flags,
                ReadForeground()));
        }

        private StageCapture? CurrentStage()
        {
            return _stages.LastOrDefault(stage => string.Equals(stage.Name, _stage, StringComparison.Ordinal));
        }

        private ForegroundSnapshot ReadForeground()
        {
            var hwnd = GetForegroundWindow();
            uint processId = 0;
            if (hwnd != nint.Zero)
            {
                _ = GetWindowThreadProcessId(hwnd, out processId);
            }

            var title = ReadWindowText(hwnd);
            var roleId = _resolver.Resolve(title);
            var processName = TryGetProcessName(processId);
            return new ForegroundSnapshot(
                $"0x{hwnd.ToInt64():X}",
                processId,
                processName,
                title,
                roleId,
                hwnd == _target.WindowHandle && processId == _target.ProcessId &&
                    string.Equals(roleId, _target.RoleId, StringComparison.Ordinal),
                string.Equals(processName, "notepad", StringComparison.OrdinalIgnoreCase));
        }

        private static string TryGetProcessName(uint processId)
        {
            if (processId == 0)
            {
                return string.Empty;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadWindowText(nint window)
        {
            if (window == nint.Zero)
            {
                return string.Empty;
            }

            var text = new StringBuilder(512);
            _ = GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }
    }

    private const uint WmInputDeviceChange = 0x00FE;

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;

        public RawInputDevice(ushort usagePage, ushort usage, uint flags, nint target)
        {
            UsagePage = usagePage;
            Usage = usage;
            Flags = flags;
            Target = target;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint MessageId;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(ushort classAtom, nint instance);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint count,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, Delegate callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint minFilter, uint maxFilter, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);
}
