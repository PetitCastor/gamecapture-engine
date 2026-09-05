using Ocrx.Engine.Shell;
using Xunit;

namespace Ocrx.Engine.Tests.Shell;

/// <summary>
/// Pins <see cref="WindowChrome"/>'s defensive contract: every <c>DwmSetWindowAttribute</c> call is
/// best-effort and must never throw, even when there is no real window to theme. Run in whatever
/// environment CI happens to be (no live display required — DWM P/Invoke calls fail harmlessly on
/// their own when there is no compositor, which is exactly the behavior under test).
/// </summary>
public sealed class WindowChromeTests
{
    [Fact]
    public void ApplyTheme_ZeroHandle_DoesNotThrow()
    {
        var exception = Record.Exception(() => WindowChrome.ApplyTheme(IntPtr.Zero, dark: true));

        Assert.Null(exception);
    }

    [Fact]
    public void ApplyTheme_BogusNonZeroHandle_DoesNotThrow()
    {
        // Not a real HWND — DwmSetWindowAttribute is expected to fail on it (a non-zero HRESULT),
        // which this must swallow exactly like it would an unsupported attribute on an older build.
        var bogusHandle = new IntPtr(0x7FFFFFFF);

        var exception = Record.Exception(() => WindowChrome.ApplyTheme(bogusHandle, dark: false));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyTheme_BothThemes_DoNotThrow(bool dark)
    {
        var exception = Record.Exception(() => WindowChrome.ApplyTheme(new IntPtr(12345), dark));

        Assert.Null(exception);
    }

    [Fact]
    public void IsSystemDarkModeEnabled_NeverThrows()
    {
        var exception = Record.Exception(() => WindowChrome.IsSystemDarkModeEnabled());

        Assert.Null(exception);
    }

    [Fact]
    public void IsSystemDarkModeEnabled_ReturnsABoolWithoutThrowing_RegardlessOfMachineState()
    {
        // Not asserting light vs dark — that depends on the machine running the test. The contract
        // under test is only that this always completes and returns, never throws, on any machine
        // (including one with no Personalize key at all, e.g. Windows Server / an older build).
        var result = WindowChrome.IsSystemDarkModeEnabled();

        Assert.IsType<bool>(result);
    }
}
