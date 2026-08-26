using System.Diagnostics;
using System.Windows.Forms;

namespace GameCapture.Engine;

/// <summary>Reports fatal startup failures when the normal tray process has no console.</summary>
internal static class StartupDiagnostics
{
    private const string EventLogName = "Application";
    private const string StartupLogFileName = "startup.log";

    public static void Report(string? message, Exception? exception = null)
    {
        var displayMessage = message ?? exception?.Message ?? "The engine failed during startup.";
        var detail = exception is null ? displayMessage : $"{displayMessage}{Environment.NewLine}{exception}";

        // A console window is one way to have a readable stderr, but not the only one: a CLI/replay
        // invocation (ReplayHarness, a plugin's CI, a script) launches this WinExe with stderr
        // redirected to a pipe and no console window at all. HasConsole alone would silently drop
        // those errors into the Event Log instead of the pipe the caller is actually reading.
        if (ConsoleWindowVisibility.HasConsole || Console.IsErrorRedirected)
        {
            Console.Error.WriteLine(displayMessage);
            return;
        }

        if (!TryWriteEventLog(detail))
            TryWriteFileLog(detail);
        TryShowMessageBox(displayMessage);
    }

    private static bool TryWriteEventLog(string detail)
    {
        try
        {
            EventLog.WriteEntry(EventLogName, detail, EventLogEntryType.Error);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteFileLog(string detail)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameCapture");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, StartupLogFileName),
                $"[{DateTimeOffset.Now:O}] {detail}{Environment.NewLine}");
        }
        catch
        {
            // A non-interactive session may have no writable profile; reporting must never mask the
            // original startup failure.
        }
    }

    private static void TryShowMessageBox(string message)
    {
        try
        {
            MessageBox.Show(message, "GameCapture Engine", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Window stations without an interactive desktop cannot display UI; there is no further
            // user-facing channel available in a tray-only process.
        }
    }
}
