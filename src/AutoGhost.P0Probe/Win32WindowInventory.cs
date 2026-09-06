using System.Runtime.InteropServices;
using System.Text;

namespace AutoGhost.P0Probe;

public sealed record WindowInventoryResult(
    IReadOnlyList<ProcessObservation> Processes,
    IReadOnlyList<WindowObservation> WindowCandidates,
    IReadOnlyList<WindowObservation> TargetWindows);

/// <summary>
/// P0 window discovery is intentionally system-wide. qnyh.exe may be a launcher,
/// while the visible game window belongs to a child, parent, or helper process.
/// </summary>
public sealed class Win32WindowInventory
{
    public WindowInventoryResult Scan(string processName, RoleIdResolver resolver)
    {
        var processTable = ReadProcessTable();
        var targetProcessIds = processTable.Values
            .Where(process => string.Equals(
                Path.GetFileNameWithoutExtension(process.ExecutableName),
                processName,
                StringComparison.OrdinalIgnoreCase))
            .Select(static process => process.ProcessId)
            .ToHashSet();

        var ancestryByProcessId = processTable.Keys.ToDictionary(
            processId => processId,
            processId => BuildAncestry(processId, processTable));
        var targetAncestorIds = targetProcessIds
            .SelectMany(processId => ancestryByProcessId.GetValueOrDefault(processId) ?? Array.Empty<uint>())
            .ToHashSet();

        var relationByProcessId = processTable.Keys.ToDictionary(
            processId => processId,
            processId => ClassifyRelation(
                processId,
                targetProcessIds,
                targetAncestorIds,
                ancestryByProcessId[processId]));

        var processes = processTable.Values
            .Where(process => relationByProcessId[process.ProcessId] != "Unrelated")
            .Select(process => ToObservation(
                process,
                ancestryByProcessId[process.ProcessId],
                relationByProcessId[process.ProcessId]))
            .OrderBy(static process => process.ProcessId)
            .ToArray();

        var allWindows = new List<WindowObservation>();

        void AddWindow(nint handle, string desktopName)
        {
            var threadId = GetWindowThreadProcessId(handle, out var processId);
            var process = processTable.GetValueOrDefault(processId) ?? ReadFallbackProcess(processId);
            var ancestry = ancestryByProcessId.GetValueOrDefault(processId)
                ?? BuildAncestry(processId, processTable);
            var relation = relationByProcessId.GetValueOrDefault(processId)
                ?? ClassifyRelation(processId, targetProcessIds, targetAncestorIds, ancestry);
            var title = ReadWindowText(handle);
            var className = ReadClassName(handle);
            var windowRect = ReadRectangle(handle, GetWindowRect);
            var clientRect = ReadRectangle(handle, GetClientRect);
            var roleId = resolver.Resolve(title);

            allWindows.Add(new WindowObservation(
                WindowHandle: handle,
                DesktopName: desktopName,
                Title: title,
                ClassName: className,
                IsVisible: IsWindowVisible(handle),
                IsEnabled: IsWindowEnabled(handle),
                OwnerWindowHandle: GetWindow(handle, GW_OWNER),
                ParentWindowHandle: GetParent(handle),
                ThreadId: threadId,
                ProcessId: processId,
                ProcessName: process.ProcessName,
                ProcessPath: process.ProcessPath,
                ParentProcessId: process.ParentProcessId == 0 ? null : process.ParentProcessId,
                ProcessAncestry: ancestry,
                ProcessTreeRelation: relation,
                IsTargetProcessTree: relation != "Unrelated",
                WindowRect: windowRect,
                ClientRect: clientRect,
                IsMinimized: IsIconic(handle),
                IsCloaked: IsCloaked(handle),
                IsForeground: GetForegroundWindow() == handle,
                RoleId: roleId,
                IdentityState: IdentityState.Identifying,
                IdentityReason: "Window discovered by system-wide enumeration; identity classification pending."));
        }

        var desktopCount = 0;
        var windowStation = GetProcessWindowStation();
        if (windowStation != nint.Zero)
        {
            var desktopCallback = new EnumDesktopsProc((desktopName, _) =>
            {
                desktopCount++;
                var desktop = OpenDesktop(
                    desktopName,
                    0,
                    false,
                    DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
                if (desktop != nint.Zero)
                {
                    try
                    {
                        var windowCallback = new EnumWindowsProc((handle, _) =>
                        {
                            AddWindow(handle, desktopName);
                            return true;
                        });
                        EnumDesktopWindows(desktop, windowCallback, nint.Zero);
                    }
                    finally
                    {
                        CloseHandle(desktop);
                    }
                }

                return true;
            });
            _ = EnumDesktops(windowStation, desktopCallback, nint.Zero);
        }

        if (desktopCount == 0)
        {
            var windowCallback = new EnumWindowsProc((handle, _) =>
            {
                AddWindow(handle, "<current-desktop>");
                return true;
            });
            _ = EnumWindows(windowCallback, nint.Zero);
        }

        var targetWindows = allWindows
            .Where(static window => window.IsTargetProcessTree)
            .ToArray();

        return new WindowInventoryResult(processes, allWindows, targetWindows);
    }

    private static ProcessObservation ToObservation(
        ProcessSnapshot process,
        IReadOnlyList<uint> ancestry,
        string relation) => new(
        ProcessId: process.ProcessId,
        ProcessName: process.ProcessName,
        ProcessPath: process.ProcessPath,
        ParentProcessId: process.ParentProcessId == 0 ? null : process.ParentProcessId,
        ProcessAncestry: ancestry,
        ProcessTreeRelation: relation);

    private static string ClassifyRelation(
        uint processId,
        IReadOnlySet<uint> targetProcessIds,
        IReadOnlySet<uint> targetAncestorIds,
        IReadOnlyList<uint> ancestry)
    {
        if (targetProcessIds.Contains(processId))
        {
            return "Target";
        }

        if (ancestry.Any(targetProcessIds.Contains))
        {
            return "Child";
        }

        return targetAncestorIds.Contains(processId) ? "Ancestor" : "Unrelated";
    }

    private static IReadOnlyList<uint> BuildAncestry(
        uint processId,
        IReadOnlyDictionary<uint, ProcessSnapshot> processTable)
    {
        var ancestry = new List<uint>();
        var visited = new HashSet<uint>();
        var current = processId;

        while (processTable.TryGetValue(current, out var process) &&
               process.ParentProcessId != 0 &&
               visited.Add(process.ParentProcessId))
        {
            ancestry.Add(process.ParentProcessId);
            current = process.ParentProcessId;
        }

        return ancestry;
    }

    private static Dictionary<uint, ProcessSnapshot> ReadProcessTable()
    {
        var table = new Dictionary<uint, ProcessSnapshot>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return table;
        }

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return table;
            }

            do
            {
                var processName = entry.szExeFile.TrimEnd('\0');
                table[entry.th32ProcessID] = new ProcessSnapshot(
                    ProcessId: entry.th32ProcessID,
                    ParentProcessId: entry.th32ParentProcessID,
                    ExecutableName: processName,
                    ProcessName: Path.GetFileNameWithoutExtension(processName),
                    ProcessPath: TryReadProcessPath(entry.th32ProcessID));
            }
            while (Process32Next(snapshot, ref entry));

            return table;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static ProcessSnapshot ReadFallbackProcess(uint processId)
    {
        var processName = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // The process may have exited between EnumWindows and process lookup.
        }

        return new ProcessSnapshot(
            ProcessId: processId,
            ParentProcessId: 0,
            ExecutableName: processName,
            ProcessName: processName,
            ProcessPath: TryReadProcessPath(processId));
    }

    private static string? TryReadProcessPath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(1024);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                ? path.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string ReadWindowText(nint handle)
    {
        var length = GetWindowTextLength(handle);
        var builder = new StringBuilder(Math.Max(length + 1, 512));
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadClassName(nint handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static WindowRectangle? ReadRectangle(
        nint handle,
        GetRectangleDelegate getter)
    {
        return getter(handle, out var rect)
            ? new WindowRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : null;
    }

    private static bool IsCloaked(nint handle)
    {
        var result = DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out var cloaked, sizeof(uint));
        return result == 0 && cloaked != 0;
    }

    private sealed record ProcessSnapshot(
        uint ProcessId,
        uint ParentProcessId,
        string ExecutableName,
        string ProcessName,
        string? ProcessPath);

    private delegate bool EnumWindowsProc(nint handle, nint lParam);
    private delegate bool EnumDesktopsProc([MarshalAs(UnmanagedType.LPWStr)] string desktopName, nint lParam);
    private delegate bool GetRectangleDelegate(nint handle, out RECT rect);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint GW_OWNER = 4;
    private const uint DWMWA_CLOAKED = 14;
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktops(nint windowStation, EnumDesktopsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenDesktop(
        string desktopName,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(nint desktop, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetParent(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint handle, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint handle,
        uint attribute,
        out uint value,
        int valueSize);
}
