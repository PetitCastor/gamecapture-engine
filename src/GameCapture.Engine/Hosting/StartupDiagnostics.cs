using System.Diagnostics;
using System.Windows.Forms;

namespace GameCapture.Engine;

/// <summary>Reports fatal startup failures when the normal tray process has no console.</summary>
internal static class StartupDiagnostics
{
    private const string EventSource = "GameCapture.Engine";
    private const string EventLogName = "Application";

    public static void Report(string? message, Exception? exception = null)
    {
        var displayMessage = message ?? exception?.Message ?? "The engine failed during startup.";
        var detail = exception is null ? displayMessage : $"{displayMessage}{Environment.NewLine}{exception}";

        if (ConsoleWindowVisibility.HasConsole)
        {
            Console.Error.WriteLine(displayMessage);
            return;
        }

        TryWriteEventLog(detail);
        TryShowMessageBox(displayMessage);
    }

    private static void TryWriteEventLog(string detail)
    {
        try
        {
            using var log = new EventLog(EventLogName) { Source = EventSource };
            log.WriteEntry(detail, EventLogEntryType.Error);
        }
        catch
        {
            // Event source registration can require elevation. The message box below remains the
            // interactive fallback, and startup must never fail a second time while reporting.
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
