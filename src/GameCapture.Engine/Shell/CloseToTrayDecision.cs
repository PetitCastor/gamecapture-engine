using System.Windows.Forms;

namespace GameCapture.Engine.Shell;

/// <summary>
/// Pure decision logic factored out of <see cref="MainWindow.OnFormClosing"/> so the one branch a
/// regression here would hit silently — cancelling a close that should have gone through, or letting
/// one through that should have hidden — is unit-testable without a real <see cref="Form"/> or
/// WebView2 runtime.
/// </summary>
internal static class CloseToTrayDecision
{
    /// <summary>
    /// Whether a close in progress must be cancelled and the window hidden instead of let through.
    /// </summary>
    /// <param name="exiting">Already true when a real exit (tray Exit, <c>POST /api/exit</c>) is
    /// underway — that must never be turned back into a hide.</param>
    /// <param name="reason">The form's close reason. Only <see cref="CloseReason.UserClosing"/> (the
    /// window's own X button) is ever eligible to hide; every other reason — Windows shutdown, task
    /// manager's "end task", the parent closing — must be let through unconditionally.</param>
    /// <param name="closeToTrayEnabled">False when there is no tray icon to fall back to
    /// (<c>trayEnabled: false</c>): with no way back to a hidden window, X really does exit.</param>
    public static bool ShouldHideInsteadOfClose(bool exiting, CloseReason reason, bool closeToTrayEnabled)
        => !exiting && reason == CloseReason.UserClosing && closeToTrayEnabled;
}
