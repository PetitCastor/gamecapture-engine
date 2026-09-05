using System.Runtime.InteropServices;
using System.Security;
using Ocrx.Engine.Tray;
using Microsoft.Win32;

namespace Ocrx.Engine.Shell;

/// <summary>
/// Native window theming for <see cref="MainWindow"/> via <c>DwmSetWindowAttribute</c>. The native
/// title bar is kept — it is accessible and far less code than a custom one — this only themes it:
/// dark caption, Windows 11 rounded corners, and a Mica backdrop.
/// </summary>
/// <remarks>
/// Every attribute here is best-effort. An older Windows build (or an attribute the running build
/// does not recognize) fails the call with a non-zero <c>HRESULT</c>, or the export/DLL can be
/// entirely absent (no desktop compositor — Session 0, some RDP configs); both are ignored rather
/// than thrown, the same defensive posture the rest of this UI layer takes toward a non-interactive
/// desktop.
/// </remarks>
internal static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies dark/light caption, rounded corners and Mica to <paramref name="hwnd"/>. Never throws —
    /// a zero <see cref="IntPtr"/> (no handle yet) or a failing attribute is silently skipped, and the
    /// window keeps whatever chrome Windows drew by default.
    /// </summary>
    public static void ApplyTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
            return;

        TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        TrySetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        TrySetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
    }

    private static void TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            // The HRESULT is intentionally not inspected: any failure (older build, unsupported
            // attribute) means "leave the default chrome alone", not something to report or retry.
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // dwmapi.dll (or this specific export) unavailable on this build/session — same
            // "not supported here" outcome as a non-zero HRESULT.
        }
    }

    /// <summary>
    /// Reads <c>HKCU\...\Themes\Personalize\AppsUseLightTheme</c> the same way Windows itself decides
    /// whether to draw its own chrome light or dark, for <see cref="MainWindow"/>'s reaction to
    /// <c>SystemEvents.UserPreferenceChanged</c> when the engine's theme setting is
    /// <see cref="EngineTheme.System"/>. A missing key/value or a locked-down <c>HKCU</c> reads as
    /// light, matching the Windows default.
    /// </summary>
    public static bool IsSystemDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
