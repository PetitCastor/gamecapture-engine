using System.Globalization;
using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class FrameSourceFactoryTests
{
    [Fact]
    public void TryValidate_PreservesValidationOrderAndMonitorErrorText()
    {
        var missingReplay = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var valid = FrameSourceFactory.TryValidate(
            ["--monitor", "-1", "--replay", missingReplay],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var error);

        Assert.False(valid);
        Assert.Null(factory);
        Assert.Equal("--monitor expects a non-negative index, got '-1'.", error);
    }

    [Fact]
    public void TryValidate_ParsesVideoFpsInvariantly()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var valid = FrameSourceFactory.TryValidate(
                ["--video", EngineTestFixtures.VideoPath, "--video-fps", "2.5"],
                new EngineConfig(),
                saveFrames: false,
                out var factory,
                out var error);

            Assert.True(valid, error);
            Assert.NotNull(factory);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryValidate_RejectsVideoPacingWithoutVideo()
    {
        var valid = FrameSourceFactory.TryValidate(
            ["--video-realtime"],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var error);

        Assert.False(valid);
        Assert.Null(factory);
        Assert.Equal("--video-realtime and --video-loop require --video.", error);
    }

    [Fact]
    public void Create_SelectsReplayAndReturnsItsOperatorDescription()
    {
        Assert.True(FrameSourceFactory.TryValidate(
            ["--replay", EngineTestFixtures.ReplayDir],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var validationError), validationError);

        using var sink = new ConsoleSink();
        var selection = factory.Create(sink, out var creationError);

        Assert.NotNull(selection);
        using (selection.Source)
        {
            Assert.IsType<ReplayFrameSource>(selection.Source);
            Assert.False(selection.IsLivePaced);
            Assert.Empty(selection.MonitorLabels);
            Assert.Equal(0, selection.CurrentMonitorIndex);
            Assert.Equal(
                $"Replay:    {EngineTestFixtures.ExpectedFrameNames().Length} frame(s) from {EngineTestFixtures.ReplayDir}",
                selection.Description);
        }
        Assert.Null(creationError);
    }

    [Fact]
    public void Create_SelectsRealtimeVideoAndReturnsItsOperatorDescription()
    {
        Assert.True(FrameSourceFactory.TryValidate(
            ["--video", EngineTestFixtures.VideoPath, "--video-fps", "2.5", "--video-realtime", "--video-loop"],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var validationError), validationError);

        using var sink = new ConsoleSink();
        var selection = factory.Create(sink, out var creationError);

        Assert.NotNull(selection);
        using (selection.Source)
        {
            var formattedFps = 2.5.ToString("0.###", CultureInfo.CurrentCulture);
            Assert.IsType<VideoFrameSource>(selection.Source);
            Assert.True(selection.IsLivePaced);
            Assert.Empty(selection.MonitorLabels);
            Assert.Equal(0, selection.CurrentMonitorIndex);
            Assert.Equal(
                $"Video:     {EngineTestFixtures.VideoPath} 320x180, 00:03.000, {formattedFps} fps [realtime, loop]",
                selection.Description);
        }
        Assert.Null(creationError);
    }
}
