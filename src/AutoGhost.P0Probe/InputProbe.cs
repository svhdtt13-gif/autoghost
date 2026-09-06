using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoGhost.P0Probe;

public sealed class InputProbe
{
    public IReadOnlyList<InputProbeEvidence> Run(
        IReadOnlyList<RuntimeBinding> bindings,
        IReadOnlyList<WindowObservation> observations)
    {
        var selected = SelectForegroundBinding(bindings, observations);
        if (selected is null)
        {
            return Array.Empty<InputProbeEvidence>();
        }

        return new[] { RunWindowProbe("foreground-safe-f13", selected.Value.Binding, selected.Value.Observation) };
    }

    public IReadOnlyList<InputProbeEvidence> RunNotepadProbe()
    {
        var target = FindOrStartNotepadWindow();
        if (target is null)
        {
            return new[]
            {
                CreateFailureEvidence(
                    "notepad-safe-f13",
                    "No Notepad process with a top-level window was available; no input was sent.")
            };
        }

        var window = target.Value.WindowHandle;
        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)target.Value.ProcessId);
        }
        catch
        {
            // The window is still a valid control target; process metadata is optional for this test.
        }

        var desktop = GetWindowDesktop(window);
        var observation = new WindowObservation(
            WindowHandle: window,
            DesktopName: desktop,
            Title: ReadWindowText(window),
            ClassName: string.Empty,
            IsVisible: true,
            IsEnabled: true,
            OwnerWindowHandle: nint.Zero,
            ParentWindowHandle: nint.Zero,
            ThreadId: 0,
            ProcessId: target.Value.ProcessId,
            ProcessName: "notepad",
            ProcessPath: TryReadProcessPath(process),
            ParentProcessId: 0,
            ProcessAncestry: Array.Empty<uint>(),
            ProcessTreeRelation: "Diagnostic",
            IsTargetProcessTree: false,
            WindowRect: null,
            ClientRect: null,
            IsMinimized: false,
            IsCloaked: false,
            IsForeground: false,
            RoleId: null,
            IdentityState: IdentityState.RoleIdUnavailable,
            IdentityReason: "Notepad is a diagnostic control target, not a registered game client.");

        var evidence = RunWindowProbe("notepad-safe-f13", null, observation);
        process?.Dispose();
        return new[] { evidence };
    }

    private static (RuntimeBinding Binding, WindowObservation Observation)? SelectForegroundBinding(
        IReadOnlyList<RuntimeBinding> bindings,
        IReadOnlyList<WindowObservation> observations)
    {
        RuntimeBinding? selectedBinding = null;
        WindowObservation? selectedObservation = null;
        foreach (var binding in bindings)
        {
            var observation = observations.FirstOrDefault(candidate =>
                candidate.ProcessId == binding.ProcessId &&
                candidate.WindowHandle == binding.WindowHandle);
            if (observation is null)
            {
                continue;
            }

            selectedBinding ??= binding;
            selectedObservation ??= observation;
            var foreground = DesktopThreadRunner.Run(
                observation.DesktopName,
                () => GetForegroundWindow() == observation.WindowHandle);
            if (foreground.Succeeded && foreground.Value)
            {
                selectedBinding = binding;
                selectedObservation = observation;
                break;
            }
        }

        return selectedBinding is not null && selectedObservation is not null
            ? (selectedBinding, selectedObservation)
            : null;
    }

    private static InputProbeEvidence RunWindowProbe(
        string scenario,
        RuntimeBinding? binding,
        WindowObservation observation)
    {
        RawInputResult? rawResult = null;
        Exception? probeException = null;
        if (string.Equals(GetCurrentThreadDesktopName(), observation.DesktopName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                rawResult = PerformRawInputProbe(observation);
            }
            catch (Exception caught)
            {
                probeException = caught;
            }
        }
        else
        {
            var result = DesktopThreadRunner.Run(observation.DesktopName, () => PerformRawInputProbe(observation));
            rawResult = result.Value;
            probeException = result.Exception;
        }

        if (rawResult is null)
        {
            return CreateFailureEvidence(scenario, probeException?.Message ?? "Desktop input action failed.") with
            {
                ProcessId = observation.ProcessId,
                WindowHandle = observation.WindowHandle,
                DesktopName = observation.DesktopName,
                ForegroundWindowBefore = nint.Zero,
                ForegroundWindowAfter = nint.Zero,
                PInvokeLayout = DescribeLayout()
            };
        }

        var raw = rawResult;
        var expectedSize = ExpectedInputSize();
        var limitation = raw.DispatchSucceeded
            ? "OS SendInput accepted F13 down/up; game-level handling is not observable without a non-destructive in-game acknowledgement."
            : $"Foreground={raw.ForegroundObserved}; SendInput returned {raw.EventsInjected} event(s), GetLastError={raw.LastWin32Error}; no claim of game input acceptance.";

        return new InputProbeEvidence(
            Scenario: scenario,
            ProcessId: observation.ProcessId,
            WindowHandle: observation.WindowHandle,
            DesktopName: observation.DesktopName,
            Key: "F13",
            ForegroundRequestSucceeded: raw.ForegroundRequestSucceeded,
            ForegroundObserved: raw.ForegroundObserved,
            EventsInjected: raw.EventsInjected,
            DispatchSucceeded: raw.DispatchSucceeded && raw.StructSize == expectedSize,
            Limitation: limitation)
        {
            ForegroundWindowBefore = raw.ForegroundWindowBefore,
            ForegroundProcessIdBefore = raw.ForegroundProcessIdBefore,
            ForegroundWindowAfter = raw.ForegroundWindowAfter,
            ForegroundProcessIdAfter = raw.ForegroundProcessIdAfter,
            ForegroundWindowTitleBefore = raw.ForegroundWindowTitleBefore,
            CurrentIntegrityLevel = raw.CurrentToken.IntegrityLevel,
            TargetIntegrityLevel = raw.TargetToken.IntegrityLevel,
            CurrentProcessElevated = raw.CurrentToken.IsElevated,
            TargetProcessElevated = raw.TargetToken.IsElevated,
            SendInputLastWin32Error = raw.LastWin32Error,
            InputStructSize = raw.StructSize,
            InputStructSizeExpected = expectedSize,
            PInvokeLayout = DescribeLayout()
        };
    }

    private static RawInputResult PerformRawInputProbe(WindowObservation observation)
    {
        var currentToken = ReadTokenEvidence(GetCurrentProcess());
        var targetToken = ReadProcessTokenEvidence(observation.ProcessId);
        var foregroundRequestSucceeded = ShowWindowAsync(observation.WindowHandle, SW_RESTORE) &&
                                         SetForegroundWindow(observation.WindowHandle);
        Thread.Sleep(150);

        var foregroundBefore = GetForegroundWindow();
        GetWindowThreadProcessId(foregroundBefore, out var foregroundPidBefore);
        var foregroundObserved = foregroundBefore == observation.WindowHandle;
        var titleBefore = ReadWindowText(foregroundBefore);
        var sendResult = foregroundObserved
            ? SendSafeF13()
            : new RawSendResult(0, Marshal.GetLastWin32Error(), Marshal.SizeOf<INPUT>());
        var foregroundAfter = GetForegroundWindow();
        GetWindowThreadProcessId(foregroundAfter, out var foregroundPidAfter);

        return new RawInputResult(
            foregroundRequestSucceeded,
            foregroundObserved,
            sendResult.EventsInjected,
            sendResult.EventsInjected == 2,
            foregroundBefore,
            foregroundPidBefore,
            foregroundAfter,
            foregroundPidAfter,
            titleBefore,
            currentToken,
            targetToken,
            sendResult.LastWin32Error,
            sendResult.StructSize);
    }

    private static (nint WindowHandle, uint ProcessId)? FindOrStartNotepadWindow()
    {
        var existing = FindNotepadWindow();
        if (existing is not null)
        {
            return existing;
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = true
        });

        for (var attempt = 0; attempt < 30; attempt++)
        {
            Thread.Sleep(100);
            var window = FindNotepadWindow();
            if (window is not null)
            {
                return window;
            }
        }

        return null;
    }

    private static (nint WindowHandle, uint ProcessId)? FindNotepadWindow()
    {
        (nint WindowHandle, uint ProcessId)? found = null;
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var title = ReadWindowText(window);
            GetWindowThreadProcessId(window, out var processId);
            var processName = TryReadProcessName(processId);
            if (title.Contains("Notepad", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "Notepad", StringComparison.OrdinalIgnoreCase))
            {
                found = (window, processId);
                return false;
            }

            return true;
        }, nint.Zero);
        return found;
    }

    private static string TryReadProcessName(uint processId)
    {
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

    private static string TryReadProcessPath(Process? process)
    {
        try
        {
            return process?.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowDesktop(nint window)
    {
        var threadId = GetWindowThreadProcessId(window, out _);
        var desktop = GetThreadDesktop(threadId);
        if (desktop == nint.Zero)
        {
            return string.Empty;
        }

        var length = GetUserObjectName(desktop, 256, null);
        if (length <= 0)
        {
            return string.Empty;
        }

        var name = new StringBuilder(length + 1);
        _ = GetUserObjectName(desktop, name.Capacity, name);
        return name.ToString();
    }

    private static string GetCurrentThreadDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        return desktop == nint.Zero ? string.Empty : GetUserObjectName(desktop);
    }

    private static string GetUserObjectName(nint desktop)
    {
        _ = GetUserObjectInformation(desktop, UOI_NAME, null, 0, out var byteLength);
        if (byteLength <= 0)
        {
            return string.Empty;
        }

        var name = new StringBuilder((byteLength / sizeof(char)) + 1);
        return GetUserObjectInformation(
                   desktop,
                   UOI_NAME,
                   name,
                   name.Capacity * sizeof(char),
                   out _) != 0
            ? name.ToString()
            : string.Empty;
    }

    private static TokenEvidence ReadProcessTokenEvidence(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == nint.Zero)
        {
            return new TokenEvidence("Unknown", false);
        }

        try
        {
            return ReadTokenEvidence(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static TokenEvidence ReadTokenEvidence(nint process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            return new TokenEvidence("Unknown", false);
        }

        try
        {
            var elevation = new TOKEN_ELEVATION();
            var elevationSize = Marshal.SizeOf<TOKEN_ELEVATION>();
            var elevated = GetTokenInformation(
                token,
                TOKEN_INFORMATION_CLASS.TokenElevation,
                ref elevation,
                elevationSize,
                out _)
                && elevation.TokenIsElevated != 0;

            var integrity = ReadIntegrityLevel(token);
            return new TokenEvidence(integrity, elevated);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string ReadIntegrityLevel(nint token)
    {
        _ = GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, nint.Zero, 0, out var size);
        if (size <= 0)
        {
            return "Unknown";
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, size, out _))
            {
                return "Unknown";
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == nint.Zero)
            {
                return "Unknown";
            }

            var subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                return "Unknown";
            }

            var ridOffset = 8 + (4 * (subAuthorityCount - 1));
            var rid = (uint)Marshal.ReadInt32(sid, ridOffset);
            return rid switch
            {
                >= 0x4000 => "System",
                >= 0x3000 => "High",
                >= 0x2000 => "Medium",
                >= 0x1000 => "Low",
                _ => $"RID-0x{rid:X}"
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RawSendResult SendSafeF13()
    {
        var size = Marshal.SizeOf<INPUT>();
        var keyboardOffset = IntPtr.Size == 8 ? 8 : 4;
        var bytes = new byte[size * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), INPUT_KEYBOARD);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(keyboardOffset, 2), VK_F13);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(size, 4), INPUT_KEYBOARD);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(size + keyboardOffset, 2), VK_F13);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(size + keyboardOffset + 4, 4), KEYEVENTF_KEYUP);

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var injected = (int)SendInputRaw(2, buffer, size);
            var lastError = Marshal.GetLastWin32Error();
            return new RawSendResult(injected, lastError, size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static InputProbeEvidence CreateFailureEvidence(string scenario, string limitation)
    {
        return new InputProbeEvidence(
            Scenario: scenario,
            ProcessId: 0,
            WindowHandle: nint.Zero,
            DesktopName: string.Empty,
            Key: "F13",
            ForegroundRequestSucceeded: false,
            ForegroundObserved: false,
            EventsInjected: 0,
            DispatchSucceeded: false,
            Limitation: limitation)
        {
            InputStructSize = Marshal.SizeOf<INPUT>(),
            InputStructSizeExpected = ExpectedInputSize(),
            PInvokeLayout = DescribeLayout()
        };
    }

    private static int ExpectedInputSize() => IntPtr.Size == 8 ? 40 : 28;

    private static string DescribeLayout() =>
        $"INPUT={Marshal.SizeOf<INPUT>()}; INPUT_UNION={Marshal.SizeOf<INPUT_UNION>()}; KEYBDINPUT={Marshal.SizeOf<KEYBDINPUT>()}; pointerSize={IntPtr.Size}";

    private static string ReadWindowText(nint window)
    {
        if (window == nint.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        _ = GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    private sealed record RawInputResult(
        bool ForegroundRequestSucceeded,
        bool ForegroundObserved,
        int EventsInjected,
        bool DispatchSucceeded,
        nint ForegroundWindowBefore,
        uint ForegroundProcessIdBefore,
        nint ForegroundWindowAfter,
        uint ForegroundProcessIdAfter,
        string ForegroundWindowTitleBefore,
        TokenEvidence CurrentToken,
        TokenEvidence TargetToken,
        int LastWin32Error,
        int StructSize);

    private sealed record RawSendResult(int EventsInjected, int LastWin32Error, int StructSize);

    private sealed record TokenEvidence(string IntegrityLevel, bool IsElevated);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUT_UNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_F13 = 0x7C;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetUserObjectInformation(
        nint objectHandle,
        int index,
        StringBuilder? information,
        int length,
        out int returnedLength);

    private static int GetUserObjectName(nint desktop, int capacity, StringBuilder? name)
    {
        return GetUserObjectInformation(desktop, UOI_NAME, name, capacity, out var returnedLength) != 0
            ? returnedLength
            : 0;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        ref TOKEN_ELEVATION tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputRaw(uint count, nint inputs, int inputSize);
}
