using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoGhost.ActionModel;

public sealed record RecordingTarget(
    string ClientId,
    nint WindowHandle,
    int ScreenLeft,
    int ScreenTop,
    int ClientWidth,
    int ClientHeight);

/// <summary>
/// A foreground-only, observation-only Windows recorder. It captures low-level
/// mouse/keyboard events but never sends input. The caller must provide a live
/// identity validator so a stale/reused HWND cannot be recorded as the client.
/// Start/Stop should be called from the WPF dispatcher thread, which owns the
/// low-level hook message pump.
/// </summary>
public sealed class WindowsInputRecorder : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseWheel = 0x020A;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlmhfInjected = 0x00000001;
    private const int XButton1 = 1;

    private readonly object _gate = new();
    private readonly Func<RecordingTarget, bool> _identityValidator;
    private readonly Func<nint> _foregroundWindow;
    private readonly Func<RecordingTarget, bool> _foregroundValidator;
    private readonly LowLevelKeyboardProc _keyboardCallback;
    private readonly LowLevelMouseProc _mouseCallback;
    private readonly List<ActionStep> _steps = new();
    private RecordingTarget? _target;
    private nint _keyboardHook;
    private nint _mouseHook;
    private long _lastCapturedTimestamp;
    private NormalizedPoint? _lastMovePoint;
    private long _hookCallbackCount;
    private long _rejectedForegroundOrIdentityCount;
    private long _rejectedInjectedCount;
    private bool _disposed;

    public WindowsInputRecorder(
        Func<RecordingTarget, bool> identityValidator,
        Func<nint>? foregroundWindow = null,
        Func<RecordingTarget, bool>? foregroundValidator = null)
    {
        _identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
        _foregroundWindow = foregroundWindow ?? GetForegroundWindow;
        _foregroundValidator = foregroundValidator ?? (target => _foregroundWindow() == target.WindowHandle);
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;
    }

    public bool IsRecording { get; private set; }

    public event EventHandler? StepRecorded;

    public int StepCount
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count;
            }
        }
    }

    public long HookCallbackCount => Interlocked.Read(ref _hookCallbackCount);

    public long RejectedForegroundOrIdentityCount =>
        Interlocked.Read(ref _rejectedForegroundOrIdentityCount);

    public long RejectedInjectedCount => Interlocked.Read(ref _rejectedInjectedCount);

    public void Start(RecordingTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
        {
            throw new InvalidOperationException("A recording is already active.");
        }

        ValidateTarget(target);
        _target = target;
        lock (_gate)
        {
            _steps.Clear();
            _lastMovePoint = null;
            _lastCapturedTimestamp = Stopwatch.GetTimestamp();
        }
        Interlocked.Exchange(ref _hookCallbackCount, 0);
        Interlocked.Exchange(ref _rejectedForegroundOrIdentityCount, 0);
        Interlocked.Exchange(ref _rejectedInjectedCount, 0);

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == nint.Zero)
        {
            _target = null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(Keyboard) failed.");
        }

        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == nint.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
            _target = null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(Mouse) failed.");
        }

        IsRecording = true;
    }

    public ActionDefinition Stop(string actionName)
    {
        if (!IsRecording || _target is null)
        {
            throw new InvalidOperationException("No active recording exists.");
        }

        IsRecording = false;
        UnhookWindowsHookEx(_keyboardHook);
        UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = nint.Zero;
        _mouseHook = nint.Zero;

        var target = _target;
        _target = null;
        ActionStep[] steps;
        lock (_gate)
        {
            steps = _steps.ToArray();
        }

        return ActionDefinition.Create(actionName, target.ClientId, target.ClientWidth, target.ClientHeight, steps);
    }

    public void Cancel()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        UnhookWindowsHookEx(_keyboardHook);
        UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = nint.Zero;
        _mouseHook = nint.Zero;
        _target = null;
        lock (_gate)
        {
            _steps.Clear();
            _lastMovePoint = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
    }

    private void ValidateTarget(RecordingTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ClientId) || target.WindowHandle == nint.Zero ||
            target.ClientWidth <= 0 || target.ClientHeight <= 0)
        {
            throw new ArgumentException("A verified Client ID, HWND, and positive client dimensions are required.", nameof(target));
        }

        if (!IsWindow(target.WindowHandle) || !_foregroundValidator(target) || !_identityValidator(target))
        {
            throw new InvalidOperationException(
                "Recording requires a live verified binding and the bound client in the foreground.");
        }
    }

    private bool IsTargetForeground()
    {
        var target = _target;
        return IsRecording && target is not null &&
               IsWindow(target.WindowHandle) &&
               _foregroundValidator(target) &&
               _identityValidator(target);
    }

    private void Record(ActionStep step)
    {
        ActionStep recorded;
        lock (_gate)
        {
            recorded = step with { Sequence = _steps.Count };
            _steps.Add(recorded);
            _lastCapturedTimestamp = Stopwatch.GetTimestamp();
        }

        StepRecorded?.Invoke(this, EventArgs.Empty);
    }

    private int ConsumeDelay()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var elapsedMs = (now - _lastCapturedTimestamp) * 1000d / Stopwatch.Frequency;
            _lastCapturedTimestamp = now;
            return (int)Math.Clamp(Math.Round(elapsedMs), 0d, int.MaxValue);
        }
    }

    private NormalizedPoint ToNormalizedPoint(Point point)
    {
        var target = _target ?? throw new InvalidOperationException("Recorder target disappeared.");
        if (!ScreenToClient(target.WindowHandle, ref point))
        {
            throw new InvalidOperationException("ScreenToClient failed while recording a bound client.");
        }

        return NormalizedPoint.FromClientPixel(
            point.X,
            point.Y,
            target.ClientWidth,
            target.ClientHeight);
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            Interlocked.Increment(ref _hookCallbackCount);
            if (!IsTargetForeground())
            {
                Interlocked.Increment(ref _rejectedForegroundOrIdentityCount);
            }
            else
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if ((data.Flags & LlkhfInjected) != 0)
                {
                    Interlocked.Increment(ref _rejectedInjectedCount);
                }
                else
                {
                    var message = unchecked((int)wParam);
                    var keyEvent = message is WmKeyDown or WmSysKeyDown
                        ? RecordedKeyEvent.Down
                        : message is WmKeyUp or WmSysKeyUp
                            ? RecordedKeyEvent.Up
                            : (RecordedKeyEvent?)null;
                    if (keyEvent is not null)
                    {
                        Record(new ActionStep(
                            Sequence: 0,
                            Kind: RecordedActionKind.Key,
                            DelayBeforeMs: ConsumeDelay(),
                            VirtualKey: unchecked((int)data.VirtualKeyCode),
                            KeyEvent: keyEvent));
                    }
                }
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            Interlocked.Increment(ref _hookCallbackCount);
            if (!IsTargetForeground())
            {
                Interlocked.Increment(ref _rejectedForegroundOrIdentityCount);
            }
            else
            {
                var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                if ((data.Flags & LlmhfInjected) != 0)
                {
                    Interlocked.Increment(ref _rejectedInjectedCount);
                }
                else
                {
                    var message = unchecked((int)wParam);
                    var point = ToNormalizedPoint(data.Point);
                    if (message == WmMouseMove)
                    {
                        var shouldRecord = _lastMovePoint is null ||
                            Math.Abs(_lastMovePoint.X - point.X) >= 0.003 ||
                            Math.Abs(_lastMovePoint.Y - point.Y) >= 0.003;
                        if (shouldRecord)
                        {
                            _lastMovePoint = point;
                            Record(new ActionStep(0, RecordedActionKind.MouseMove, ConsumeDelay(), Point: point));
                        }
                    }
                    else if (TryGetMouseButton(message, data.MouseData, out var button, out var isDown))
                    {
                        Record(new ActionStep(0, RecordedActionKind.MouseButton, ConsumeDelay(), point, button, isDown));
                    }
                    else if (message == WmMouseWheel)
                    {
                        var delta = unchecked((short)((data.MouseData >> 16) & 0xffff));
                        Record(new ActionStep(0, RecordedActionKind.MouseWheel, ConsumeDelay(), point, WheelDelta: delta));
                    }
                }
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private static bool TryGetMouseButton(
        int message,
        nuint mouseData,
        out RecordedMouseButton button,
        out bool isDown)
    {
        button = RecordedMouseButton.Left;
        isDown = false;
        switch (message)
        {
            case WmLButtonDown:
                button = RecordedMouseButton.Left;
                isDown = true;
                return true;
            case WmLButtonUp:
                button = RecordedMouseButton.Left;
                return true;
            case WmRButtonDown:
                button = RecordedMouseButton.Right;
                isDown = true;
                return true;
            case WmRButtonUp:
                button = RecordedMouseButton.Right;
                return true;
            case WmMButtonDown:
                button = RecordedMouseButton.Middle;
                isDown = true;
                return true;
            case WmMButtonUp:
                button = RecordedMouseButton.Middle;
                return true;
            case WmXButtonDown:
            case WmXButtonUp:
                button = ((mouseData >> 16) & 0xffff) == XButton1
                    ? RecordedMouseButton.X1
                    : RecordedMouseButton.X2;
                isDown = message == WmXButtonDown;
                return true;
            default:
                return false;
        }
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, Delegate lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hWnd, ref Point point);
}
