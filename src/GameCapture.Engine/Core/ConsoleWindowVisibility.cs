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
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Hides the console window unless a debugger is attached to this process.</summary>
    public static void HideUnlessDebugging()
    {
        if (Debugger.IsAttached)
            return;

        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            ShowWindow(handle, SW_HIDE);
    }
}
