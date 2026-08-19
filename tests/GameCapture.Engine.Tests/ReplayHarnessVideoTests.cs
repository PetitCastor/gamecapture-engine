using GameCapture.Sdk;
using GameCapture.Sdk.Testing;
using Xunit;
using Xunit.Abstractions;

namespace GameCapture.Engine.Tests;

/// <summary>
/// <see cref="ReplayHarness"/> driving a real spawned engine from an MP4 (<c>--video</c>, TASK-25)
/// rather than a PNG corpus — the video counterpart to <see cref="ReplayHarnessTests"/>'s corpus
/// smoke, proving the harness's <see cref="ReplayOptions.VideoPath"/> path reaches a running engine
/// and drains to EOF like a corpus does. Uses the same synthetic fixture the engine's own
/// <c>VideoFrameSourceTests</c> use, so no new binary fixture is committed here.
/// </summary>
[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayHarnessVideoTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task SmokeVideo_DrainsTheClipAndEndsWithReplayCompleted()
    {
        var enginePath = EngineLocator.Resolve();
        var plugin = new NullPlugin();

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            VideoPath = ReplayCorpus.Resolve(EngineTestFixtures.VideoPath),
            Plugin = plugin,
            Timeout = TestTimeout,
        });

        output.WriteLine($"{plugin.TickCount} tick(s) dispatched, {result.Records.Count} record(s), " +
            $"exit {result.ExitCode}, reason {result.Reason}");

        Assert.Equal(0, result.ExitCode);

        // A video drains through the same null-frame EOF path as a PNG corpus (IsReplay == true in
        // both), so the host reports the same end reason — this is the end-to-end proof of that.
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        // The synthetic clip is non-trivial, so a real decode produces ticks; zero would mean the
        // video never reached the scan loop (the failure this test exists to catch).
        Assert.True(plugin.TickCount > 0, "expected at least one tick decoded from the video");
    }

    [Fact]
    public async Task SmokeVideo_WithFractionalFps_RoundTripsThroughTheEngineCli()
    {
        var enginePath = EngineLocator.Resolve();
        var plugin = new NullPlugin();

        // 2.5 fps over the 3.0s/30fps fixture is fractional (so it exercises a non-integer fps all
        // the way through BuildEngineArgs → CLI → the engine's double.TryParse, not just the integer
        // path) and well under the clip's native rate (so the engine's above-native-fps guard doesn't
        // reject it). It can't reproduce the comma-decimal-culture misparse the TASK-26 review flagged
        // — the child engine process runs under the OS culture regardless of this test's thread — so
        // that fix lives in Program.cs parsing invariantly, not in an assertion here.
        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = enginePath,
            VideoPath = ReplayCorpus.Resolve(EngineTestFixtures.VideoPath),
            VideoFps = 2.5,
            Plugin = plugin,
            Timeout = TestTimeout,
        });

        output.WriteLine($"{plugin.TickCount} tick(s) dispatched at 2.5 fps, exit {result.ExitCode}, " +
            $"reason {result.Reason}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        // An fps that never reached the engine (dropped arg, rejected value) would make it exit
        // before the pipe opened, timing the run out rather than reaching here with ticks.
        Assert.True(plugin.TickCount > 0, "expected ticks decoded at the requested fps");
    }
}
