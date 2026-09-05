using System.Diagnostics;
using System.Text;
using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// The redirect and decode decisions, pinned away from the process edge. These are ordinary choices
/// with real consequences — a redirected stdin hangs a plugin that reads it, and the decoder decides
/// whether output renders or turns to mojibake — so they live outside the coverage-excluded
/// <see cref="PluginLauncher"/> where they can be asserted.
/// </summary>
public class PluginProcessCaptureTests
{
    [Fact]
    public void Configure_RedirectsBothOutputStreams()
    {
        var startInfo = new ProcessStartInfo();

        PluginProcessCapture.Configure(startInfo);

        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    /// <summary>
    /// Standard input stays inherited on purpose: redirecting it without ever closing the write end
    /// would hang any plugin that reads from it, and the engine has nothing to say down that channel.
    /// </summary>
    [Fact]
    public void Configure_DoesNotRedirectStandardInput()
    {
        var startInfo = new ProcessStartInfo();

        PluginProcessCapture.Configure(startInfo);

        Assert.False(startInfo.RedirectStandardInput);
    }

    /// <summary>
    /// Decoding is pinned to UTF-8 for determinism, not fidelity. Left null, Process would decode with
    /// the engine's own Console.OutputEncoding — the console code page in a debug run that allocated a
    /// console, UTF-8 in a normal windowless one — so the same plugin would render differently
    /// depending on how the engine was started. A measured .NET child actually emits the console code
    /// page and best-fit-maps non-ASCII on the way out (the SDK's em dash arrives as a hyphen), so
    /// nothing here can recover characters the child already flattened; what this does buy is that
    /// ASCII is exact everywhere and a stray high byte becomes U+FFFD rather than a plausible-looking
    /// wrong character. The preamble assertion keeps a BOM out of the first captured line.
    /// </summary>
    [Fact]
    public void Configure_DecodesAsUtf8WithoutAPreamble()
    {
        var startInfo = new ProcessStartInfo();

        PluginProcessCapture.Configure(startInfo);

        Assert.IsType<UTF8Encoding>(startInfo.StandardOutputEncoding);
        Assert.IsType<UTF8Encoding>(startInfo.StandardErrorEncoding);
        Assert.Empty(startInfo.StandardOutputEncoding!.GetPreamble());
        Assert.Empty(startInfo.StandardErrorEncoding!.GetPreamble());
    }

    /// <summary>
    /// Capture is additive. The launcher has already set the working directory to the plugin's own
    /// folder — the SDK reads its seeded config relative to that — and asked for no window.
    /// </summary>
    [Fact]
    public void Configure_LeavesTheCallersOtherStartupChoicesAlone()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\plugins\mission\MissionPlugin.exe",
            WorkingDirectory = @"C:\plugins\mission",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        PluginProcessCapture.Configure(startInfo);

        Assert.Equal(@"C:\plugins\mission", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }
}
