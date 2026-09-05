using System.Globalization;
using Xunit;

namespace Ocrx.Engine.Tests;

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
    public async Task CreateAsync_SelectsReplayAndReturnsItsOperatorDescription()
    {
        Assert.True(FrameSourceFactory.TryValidate(
            ["--replay", EngineTestFixtures.ReplayDir],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var validationError), validationError);

        using var sink = new ConsoleSink();
        var creation = await factory.CreateAsync(sink);
        Assert.True(creation.Succeeded);
        var selection = creation.Selection;

        Assert.NotNull(selection);
        using (selection.Source)
        {
            Assert.IsType<ReplayFrameSource>(selection.Source);
            Assert.Equal(FrameSourceMode.ReplayCorpus, selection.Source.Mode);
            Assert.Empty(selection.MonitorLabels);
            Assert.Equal(0, selection.CurrentMonitorIndex);
            Assert.Equal(
                $"Replay:    {EngineTestFixtures.ExpectedFrameNames().Length} frame(s) from {EngineTestFixtures.ReplayDir}",
                selection.Description);
        }
    }

    [Fact]
    public async Task CreateAsync_SelectsRealtimeVideoAndReturnsItsOperatorDescription()
    {
        Assert.True(FrameSourceFactory.TryValidate(
            ["--video", EngineTestFixtures.VideoPath, "--video-fps", "2.5", "--video-realtime", "--video-loop"],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var validationError), validationError);

        using var sink = new ConsoleSink();
        var creation = await factory.CreateAsync(sink);
        Assert.True(creation.Succeeded);
        var selection = creation.Selection;

        Assert.NotNull(selection);
        using (selection.Source)
        {
            var formattedFps = 2.5.ToString("0.###", CultureInfo.CurrentCulture);
            Assert.IsType<VideoFrameSource>(selection.Source);
            Assert.Equal(FrameSourceMode.RealtimeVideo, selection.Source.Mode);
            Assert.Empty(selection.MonitorLabels);
            Assert.Equal(0, selection.CurrentMonitorIndex);
            Assert.Equal(
                $"Video:     {EngineTestFixtures.VideoPath} 320x180, 00:03.000, {formattedFps} fps [realtime, loop]",
                selection.Description);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenVideoFpsExceedsNativeRate_ReturnsOnlyAnError()
    {
        Assert.True(FrameSourceFactory.TryValidate(
            ["--video", EngineTestFixtures.VideoPath, "--video-fps", "1000"],
            new EngineConfig(),
            saveFrames: false,
            out var factory,
            out var validationError), validationError);

        using var sink = new ConsoleSink();
        var creation = await factory.CreateAsync(sink);

        Assert.False(creation.Succeeded);
        Assert.Null(creation.Selection);
        Assert.NotNull(creation.Error);
        Assert.Contains("exceeds the video's native frame rate", creation.Error, StringComparison.Ordinal);
    }
}
