using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class FrameSourceModeTests
{
    [Theory]
    [InlineData(FrameSourceMode.LiveCapture, false, true)]
    [InlineData(FrameSourceMode.ReplayCorpus, true, false)]
    [InlineData(FrameSourceMode.DeterministicVideo, true, false)]
    [InlineData(FrameSourceMode.RealtimeVideo, true, true)]
    internal void Mode_PreservesReplayFlowAndInteractiveBehavior(
        FrameSourceMode mode,
        bool usesReplayFlow,
        bool isInteractive)
    {
        Assert.Equal(usesReplayFlow, mode.UsesReplayFlow());
        Assert.Equal(isInteractive, mode.IsInteractive());
    }
}
