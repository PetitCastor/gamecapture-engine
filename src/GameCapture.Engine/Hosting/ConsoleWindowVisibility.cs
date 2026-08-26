using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameCapture.Engine;

/// <summary>
/// Keeps normal launches console-free and allocates a console for a debugger-attached startup. The
/// check runs before top-level startup creates the output sink, so Visual Studio F5 has the same
/// banner and status output as the former console-subsystem executable.
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
