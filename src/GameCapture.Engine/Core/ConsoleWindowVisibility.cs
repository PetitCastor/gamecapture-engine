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

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// Hides the console window unless a debugger is attached, or this process is not the console's
    /// sole owner. A console-subsystem exe launched from an already-open shell (a plain `dotnet run`
    /// or double-clicking the exe from inside a terminal, with no CREATE_NEW_CONSOLE) inherits that
    /// shell's console instead of getting one of its own — <see cref="GetConsoleWindow"/> then returns
    /// the *shared* window, and hiding it would hide the developer's whole terminal. Only a launch that
    /// got a fresh console of its own (double-click from Explorer, Task Scheduler, a shortcut) is safe
    /// to hide this way.
    /// </summary>
    public static void HideUnlessDebugging()
    {
        if (Debugger.IsAttached || !OwnsConsoleExclusively())
            return;

        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            ShowWindow(handle, SW_HIDE);
    }

    private static bool OwnsConsoleExclusively()
    {
        var buffer = new uint[2];
        var attached = GetConsoleProcessList(buffer, (uint)buffer.Length);
        return attached <= 1;
    }
}
