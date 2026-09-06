using System.Runtime.InteropServices;
using System.Text;

namespace AutoGhost.P0Probe;

public sealed class CaptureProbe
{
    public IReadOnlyList<CaptureEvidence> Run(
        IReadOnlyList<RuntimeBinding> bindings,
        IReadOnlyList<WindowObservation> observations,
        string evidenceDirectory,
        bool includeMinimized)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var results = new List<CaptureEvidence>();

        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var observation = observations.FirstOrDefault(candidate =>
                candidate.ProcessId == binding.ProcessId &&
                candidate.WindowHandle == binding.WindowHandle);
            if (observation is null)
            {
                continue;
            }

            if (index == 0)
            {
                var focusResult = DesktopThreadRunner.Run(observation.DesktopName, () =>
                {
                    var requestSucceeded = ShowWindowAsync(observation.WindowHandle, SW_RESTORE) &&
                                           SetForegroundWindow(observation.WindowHandle);
                    Thread.Sleep(150);
                    return requestSucceeded && GetForegroundWindow() == observation.WindowHandle;
                });
                observation = observation with
                {
                    IsForeground = focusResult.Succeeded && focusResult.Value
                };
            }

            results.Add(CaptureWindow(
                observation,
                Path.Combine(evidenceDirectory, $"capture-{binding.ProcessId}-normal.ppm"),
                observation.IsForeground ? "foreground" : "background-non-minimized"));
        }

        if (includeMinimized && bindings.Count > 0)
        {
            var binding = bindings[0];
            var observation = observations.FirstOrDefault(candidate =>
                candidate.ProcessId == binding.ProcessId &&
                candidate.WindowHandle == binding.WindowHandle);
            if (observation is not null)
            {
                results.Add(CaptureMinimized(
                    observation,
                    Path.Combine(evidenceDirectory, $"capture-{binding.ProcessId}-minimized.ppm")));
            }
        }

        return results;
    }

    private static CaptureEvidence CaptureMinimized(
        WindowObservation observation,
        string evidencePath)
    {
        var stateResult = DesktopThreadRunner.Run(observation.DesktopName, () =>
        {
            _ = ShowWindowAsync(observation.WindowHandle, SW_MINIMIZE);
            Thread.Sleep(250);
            return IsIconic(observation.WindowHandle);
        });

        CaptureEvidence result;
        try
        {
            result = CaptureWindow(
                observation with { IsMinimized = stateResult.Succeeded && stateResult.Value },
                evidencePath,
                "minimized");
        }
        finally
        {
            DesktopThreadRunner.Run(observation.DesktopName, () =>
            {
                ShowWindowAsync(observation.WindowHandle, SW_RESTORE);
                Thread.Sleep(250);
                return true;
            });
        }

        if (!stateResult.Succeeded || !stateResult.Value)
        {
            result = result with
            {
                OperationSucceeded = false,
                Limitation = $"Could not verify minimized state on desktop '{observation.DesktopName}': {stateResult.Exception?.Message ?? "IsIconic=false"}"
            };
        }

        return result;
    }

    private static CaptureEvidence CaptureWindow(
        WindowObservation observation,
        string evidencePath,
        string scenario)
    {
        var rectResult = DesktopThreadRunner.Run(observation.DesktopName, () =>
        {
            return GetWindowRect(observation.WindowHandle, out var rect)
                ? rect
                : new RECT();
        });

        if (!rectResult.Succeeded)
        {
            return Failed(observation, evidencePath, scenario, rectResult.Exception?.Message ?? "GetWindowRect failed.");
        }

        var rect = rectResult.Value;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return Failed(observation, evidencePath, scenario, "Window rectangle is empty.");
        }

        var captureResult = DesktopThreadRunner.Run(observation.DesktopName, () =>
            CapturePixels(observation.WindowHandle, width, height, evidencePath));
        if (!captureResult.Succeeded || captureResult.Value is null)
        {
            return Failed(observation, evidencePath, scenario, captureResult.Exception?.Message ?? "Capture failed.");
        }

        var pixels = captureResult.Value;
        return new CaptureEvidence(
            Scenario: scenario,
            ProcessId: observation.ProcessId,
            WindowHandle: observation.WindowHandle,
            DesktopName: observation.DesktopName,
            Method: pixels.Method,
            OperationSucceeded: pixels.OperationSucceeded,
            ForegroundObserved: observation.IsForeground,
            MinimizedObserved: observation.IsMinimized,
            Width: pixels.Width,
            Height: pixels.Height,
            NonBlankRatio: pixels.NonBlankRatio,
            EvidencePath: Path.GetFullPath(evidencePath),
            Limitation: pixels.Limitation);
    }

    private static CaptureEvidence Failed(
        WindowObservation observation,
        string evidencePath,
        string scenario,
        string limitation) => new(
        Scenario: scenario,
        ProcessId: observation.ProcessId,
        WindowHandle: observation.WindowHandle,
        DesktopName: observation.DesktopName,
        Method: "none",
        OperationSucceeded: false,
        ForegroundObserved: observation.IsForeground,
        MinimizedObserved: observation.IsMinimized,
        Width: 0,
        Height: 0,
        NonBlankRatio: 0,
        EvidencePath: Path.GetFullPath(evidencePath),
        Limitation: limitation);

    private static PixelCaptureResult CapturePixels(
        nint window,
        int width,
        int height,
        string evidencePath)
    {
        var windowDc = GetWindowDC(window);
        if (windowDc == nint.Zero)
        {
            throw new InvalidOperationException($"GetWindowDC failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        if (memoryDc == nint.Zero || bitmap == nint.Zero)
        {
            ReleaseDC(window, windowDc);
            throw new InvalidOperationException("Could not create a compatible capture surface.");
        }

        var oldBitmap = SelectObject(memoryDc, bitmap);
        var printed = PrintWindow(window, memoryDc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
        var method = printed ? "PrintWindow" : "BitBlt";
        if (!printed)
        {
            _ = BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SRCCOPY | CAPTUREBLT);
        }

        var pixels = new byte[width * height * 4];
        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BI_RGB
            }
        };
        var rows = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS);
        SelectObject(memoryDc, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(memoryDc);
        ReleaseDC(window, windowDc);

        if (rows == 0)
        {
            throw new InvalidOperationException($"GetDIBits failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var nonBlankPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 8 || pixels[index + 1] > 8 || pixels[index + 2] > 8)
            {
                nonBlankPixels++;
            }
        }

        var ratio = (double)nonBlankPixels / (width * height);
        SavePpm(evidencePath, pixels, width, height);
        return new PixelCaptureResult(
            Method: method,
            OperationSucceeded: ratio > 0.01,
            Width: width,
            Height: height,
            NonBlankRatio: ratio,
            Limitation: ratio > 0.01
                ? "Frame contains non-background pixels; PrintWindow/BitBlt is not a GPU-present guarantee."
                : "Frame is blank/near-black; Unity/GPU surface may not be capturable through this Win32 path.");
    }

    private static void SavePpm(string path, byte[] pixels, int width, int height)
    {
        using var stream = File.Create(path);
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        var rgb = new byte[width * height * 3];
        var source = 0;
        var destination = 0;
        while (source < pixels.Length)
        {
            rgb[destination++] = pixels[source + 2];
            rgb[destination++] = pixels[source + 1];
            rgb[destination++] = pixels[source];
            source += 4;
        }

        stream.Write(rgb);
    }

    private sealed record PixelCaptureResult(
        string Method,
        bool OperationSucceeded,
        int Width,
        int Height,
        double NonBlankRatio,
        string Limitation);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowDC(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines,
        [Out] byte[] pixels, ref BITMAPINFO info, uint usage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RECT rect);

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
    private static extern bool IsIconic(nint window);
}
