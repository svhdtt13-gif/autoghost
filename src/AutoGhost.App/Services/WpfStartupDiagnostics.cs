using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AutoGhost.App.Services;

/// <summary>
/// Records the WPF host lifecycle and interactive-session context before any
/// live restart acceptance is allowed to claim a visible UI window.
/// </summary>
public static class WpfStartupDiagnostics
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static string LogPath => _logPath ??= ResolveLogPath();

    public static void InstallExceptionHooks(Application application)
    {
        application.DispatcherUnhandledException += (_, args) =>
        {
            LogException("DispatcherUnhandledException", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogException("AppDomain.UnhandledException", exception);
            }
            else
            {
                Log("AppDomain.UnhandledException", $"ExceptionObject={args.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("TaskScheduler.UnobservedTaskException", args.Exception);
        };
    }

    public static void LogStage(string stage, string? detail = null)
    {
        var context = ReadProcessContext();
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        Log(stage, $"{context}{suffix}");
    }

    public static void LogWindowState(string stage, Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var title = ReadWindowTitle(handle);
        var context = ReadProcessContext();
        Log(
            stage,
            $"{context} hwnd=0x{handle.ToInt64():X} hwndNonZero={handle != nint.Zero} " +
            $"isVisible={window.IsVisible} isLoaded={window.IsLoaded} " +
            $"presentationSource={PresentationSource.FromVisual(window) is not null} " +
            $"windowTitle='{title}'");
    }

    public static void LogException(string stage, Exception exception)
    {
        Log(stage, $"exceptionType={exception.GetType().FullName} message='{exception.Message}' stack='{exception.StackTrace}'");
    }

    private static void Log(string stage, string details)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} stage={stage} {details}{Environment.NewLine}";
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent the WPF shell from starting.
        }
    }

    private static string ReadProcessContext()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var explorer = Process.GetProcessesByName("explorer")
                .Where(process =>
                {
                    try
                    {
                        return process.SessionId == current.SessionId;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(process => process.Id.ToString())
                .ToArray();
            var foreground = GetForegroundWindow();
            var foregroundPid = GetWindowThreadProcessId(foreground, out var pid);
            return $"pid={current.Id} session={current.SessionId} user='{Environment.UserName}' " +
                   $"explorerSameSession=[{string.Join(',', explorer)}] " +
                   $"foregroundHwnd=0x{foreground.ToInt64():X} foregroundPid={foregroundPid} " +
                   $"foregroundOwnerPid={pid}";
        }
        catch (Exception exception)
        {
            return $"processContextError='{exception.Message}'";
        }
    }

    private static string ResolveLogPath()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("AUTOGHOST_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            return Path.Combine(Path.GetFullPath(evidenceDirectory), "p1-wpf-startup.log");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AutoGhost", "logs", "p1-wpf-startup.log");
    }

    private static string ReadWindowTitle(nint handle)
    {
        if (handle == nint.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(512);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
}
