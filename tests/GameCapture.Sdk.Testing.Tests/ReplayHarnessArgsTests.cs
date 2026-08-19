using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;

namespace GameCapture.Sdk.Testing.Tests;

/// <summary>
/// Unit coverage for the source-selection seam on <see cref="ReplayHarness"/>: the exactly-one-of
/// guard and the arg mapping <see cref="ReplayHarness.BuildEngineArgs"/> produces. No engine is
/// spawned — these assert the wiring that <see cref="ReplayHarness.RunAsync"/> hands the process,
/// which is where a wrong flag or a dropped source would otherwise only surface as a slow,
/// hard-to-read integration failure.
/// </summary>
public class ReplayHarnessArgsTests
{
    private const string Pipe = "test-pipe";

    private static ReplayOptions Options(string? corpus = null, string? video = null, double? fps = null) => new()
    {
        EnginePath = @"C:\does-not-need-to-exist\GameCapture.Engine.exe",
        CorpusDir = corpus,
        VideoPath = video,
        VideoFps = fps,
        Plugin = new StubPlugin(),
    };

    [Fact]
    public void BuildEngineArgs_Corpus_EmitsReplayFlag()
    {
        var args = ReplayHarness.BuildEngineArgs(Options(corpus: @"C:\corpus\refinery"), Pipe);

        Assert.Equal(["--replay", @"C:\corpus\refinery", "--pipe", Pipe], args);
    }

    [Fact]
    public void BuildEngineArgs_Video_EmitsVideoFlagAndNoFpsWhenUnset()
    {
        var args = ReplayHarness.BuildEngineArgs(Options(video: @"C:\clips\run.mp4"), Pipe);

        Assert.Equal(["--video", @"C:\clips\run.mp4", "--pipe", Pipe], args);
        Assert.DoesNotContain("--video-fps", args);
    }

    [Fact]
    public void BuildEngineArgs_VideoFps_ReachesTheEngineArgsInvariantFormatted()
    {
        var args = ReplayHarness.BuildEngineArgs(Options(video: @"C:\clips\run.mp4", fps: 2.5), Pipe);

        Assert.Equal(["--video", @"C:\clips\run.mp4", "--video-fps", "2.5", "--pipe", Pipe], args);
    }

    [Fact]
    public void BuildEngineArgs_CorpusWithFps_IgnoresFpsWhichHasNoMeaningForACorpus()
    {
        var args = ReplayHarness.BuildEngineArgs(Options(corpus: @"C:\corpus\refinery", fps: 2.5), Pipe);

        Assert.DoesNotContain("--video-fps", args);
    }

    [Fact]
    public async Task RunAsync_NeitherSourceSet_ThrowsBeforeSpawning()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ReplayHarness.RunAsync(Options()));

        Assert.Contains("exactly one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_BothSourcesSet_ThrowsBeforeSpawning()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => ReplayHarness.RunAsync(Options(corpus: @"C:\corpus\refinery", video: @"C:\clips\run.mp4")));

        Assert.Contains("exactly one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task RunAsync_BadVideoFps_ThrowsBeforeSpawning(double fps)
    {
        // Eager guard rather than letting the engine reject the value and exit before the pipe opens,
        // which the plugin host would only surface as an opaque Timeout after minutes.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => ReplayHarness.RunAsync(Options(video: @"C:\clips\run.mp4", fps: fps)));

        Assert.Contains("VideoFps", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Minimal <see cref="IGameCapturePlugin"/>: the arg/guard tests never reach a tick, so
    /// only the two non-defaulted members need a body.</summary>
    private sealed class StubPlugin : IGameCapturePlugin
    {
        public string Name => "stub";
        public IReadOnlyList<RoiSubscription> Rois => [];
        public Task OnTickAsync(TickContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
