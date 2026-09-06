using System.Runtime.InteropServices;

namespace AutoGhost.P0Probe;

internal static class DesktopThreadRunner
{
    public static DesktopActionResult<T> Run<T>(string desktopName, Func<T> action)
    {
        T? value = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            var desktop = OpenDesktop(
                desktopName,
                0,
                false,
                DESKTOP_READOBJECTS |
                DESKTOP_WRITEOBJECTS |
                DESKTOP_ENUMERATE |
                DESKTOP_CREATEWINDOW |
                DESKTOP_SWITCHDESKTOP);
            if (desktop == nint.Zero)
            {
                exception = new InvalidOperationException(
                    $"OpenDesktop('{desktopName}') failed with Win32 error {Marshal.GetLastWin32Error()}.");
                return;
            }

            try
            {
                if (!SetThreadDesktop(desktop))
                {
                    exception = new InvalidOperationException(
                        $"SetThreadDesktop('{desktopName}') failed with Win32 error {Marshal.GetLastWin32Error()}.");
                    return;
                }

                value = action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                CloseDesktop(desktop);
            }
        })
        {
            IsBackground = true,
            Name = $"AutoGhost.P0Probe.{desktopName}"
        };

        thread.Start();
        thread.Join();
        return new DesktopActionResult<T>(value, exception);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenDesktop(
        string desktopName,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
}

internal sealed record DesktopActionResult<T>(T? Value, Exception? Exception)
{
    public bool Succeeded => Exception is null;
}
