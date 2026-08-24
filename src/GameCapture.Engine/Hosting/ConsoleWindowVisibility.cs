using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameCapture.Engine;

/// <summary>
/// Hides the process's console window for anything but a local debug session. Installed and run as
/// a background/tray app, the engine has no console for a human to read; a debugger attached (F5 in
/// an IDE, or attach-to-process) is the signal that a developer wants to see it.
/// </summary>
internal static class ConsoleWindowVisibility
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>Whether this process currently has a console available for interactive output.</summary>
    public static bool HasConsole => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>
    /// Allocates the debug console before application startup creates its output sink. A normal
    /// WinExe launch remains console-free; an already-attached debugger is the explicit developer
    /// signal that the banner and status console should be available.
    /// </summary>
    public static void EnsureDebugConsole()
    {
        if (!Debugger.IsAttached || HasConsole)
            return;

        _ = AllocConsole();
    }
}
