using System.Windows.Forms;
using GameCapture.Engine.Shell;
using Xunit;

namespace GameCapture.Engine.Tests.Shell;

/// <summary>
/// Pins <see cref="MainWindow.OnFormClosing"/>'s branch in isolation from any real <see cref="Form"/>:
/// which combination of state cancels the close into a hide, versus letting it proceed and really
/// exit the engine.
/// </summary>
public class CloseToTrayDecisionTests
{
    [Fact]
    public void UserClosing_CloseToTrayEnabled_NotExiting_Hides()
        => Assert.True(CloseToTrayDecision.ShouldHideInsteadOfClose(
            exiting: false, CloseReason.UserClosing, closeToTrayEnabled: true));

    [Fact]
    public void UserClosing_CloseToTrayDisabled_ReallyExits()
        => Assert.False(CloseToTrayDecision.ShouldHideInsteadOfClose(
            exiting: false, CloseReason.UserClosing, closeToTrayEnabled: false));

    [Fact]
    public void AlreadyExiting_NeverHidesEvenWithCloseToTrayEnabled()
        // A real exit in progress (tray Exit, POST /api/exit) racing the user also clicking X must
        // not get turned back into a hidden window.
        => Assert.False(CloseToTrayDecision.ShouldHideInsteadOfClose(
            exiting: true, CloseReason.UserClosing, closeToTrayEnabled: true));

    [Theory]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.TaskManagerClosing)]
    [InlineData(CloseReason.ApplicationExitCall)]
    [InlineData(CloseReason.FormOwnerClosing)]
    public void NonUserClosingReasons_NeverHide(CloseReason reason)
        // Only the window's own X button is eligible to hide; every other reason must be let through
        // unconditionally, or the process could fail to exit when Windows asks it to.
        => Assert.False(CloseToTrayDecision.ShouldHideInsteadOfClose(
            exiting: false, reason, closeToTrayEnabled: true));
}
