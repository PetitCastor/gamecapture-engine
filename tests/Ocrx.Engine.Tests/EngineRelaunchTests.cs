using Ocrx.Engine;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// Pins <see cref="EngineRelaunch.StripPersistedOverrides"/>: the tray persists a monitor/settings
/// change to config and relaunches, so the CLI flags that would re-override those very fields
/// (<c>--monitor</c>, <c>--ocr-lang</c>, <c>--pipe</c>) must be dropped from the relaunch args while
/// every other flag survives intact.
/// </summary>
public class EngineRelaunchTests
{
    [Fact]
    public void Strips_monitor_flag_and_its_value()
    {
        var result = EngineRelaunch.StripPersistedOverrides(["--monitor", "1", "--verbose"]);
        Assert.Equal(["--verbose"], result);
    }

    [Fact]
    public void Strips_ocr_lang_flag_and_its_value()
    {
        var result = EngineRelaunch.StripPersistedOverrides(["--ocr-lang", "en-US", "--save-frames"]);
        Assert.Equal(["--save-frames"], result);
    }

    [Fact]
    public void Strips_pipe_flag_and_its_value()
    {
        // A tray-saved PipeName change must win on restart, not be immediately shadowed by the
        // original launch's --pipe argument (Program.cs prefers the CLI flag over config).
        var result = EngineRelaunch.StripPersistedOverrides(["--pipe", "custom", "--verbose"]);
        Assert.Equal(["--verbose"], result);
    }

    [Fact]
    public void Strips_all_three_overrides_when_present_together()
    {
        var result = EngineRelaunch.StripPersistedOverrides(
            ["--pipe", "custom", "--monitor", "2", "--ocr-lang", "fr-FR", "--verbose"]);
        Assert.Equal(["--verbose"], result);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var result = EngineRelaunch.StripPersistedOverrides(["--Monitor", "0"]);
        Assert.Empty(result);
    }

    [Fact]
    public void Preserves_unrelated_flags_verbatim()
    {
        string[] args = ["--video", "clip.mp4", "--video-fps", "2.5", "--verbose"];
        var result = EngineRelaunch.StripPersistedOverrides(args);
        Assert.Equal(args, result);
    }

    [Fact]
    public void Empty_args_yield_empty()
    {
        Assert.Empty(EngineRelaunch.StripPersistedOverrides([]));
    }

    [Fact]
    public void Trailing_override_without_a_value_drops_only_the_flag()
    {
        // A malformed tail (flag with no following value) must not throw or swallow a real arg.
        var result = EngineRelaunch.StripPersistedOverrides(["--verbose", "--monitor"]);
        Assert.Equal(["--verbose"], result);
    }

    [Theory]
    [InlineData(@"C:\app\Ocrx.Engine.exe", true)]
    [InlineData("/usr/local/bin/Ocrx.Engine", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)] // `dotnet run` — muxer, not the engine
    [InlineData("/usr/bin/dotnet", false)]
    [InlineData("DOTNET.EXE", false)]                          // case-insensitive
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSelfRelaunchable_recognizes_the_apphost_but_not_the_dotnet_muxer(string? path, bool expected)
    {
        Assert.Equal(expected, EngineRelaunch.IsSelfRelaunchable(path));
    }
}
