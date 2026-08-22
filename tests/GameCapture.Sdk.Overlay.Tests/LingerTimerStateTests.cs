using GameCapture.Sdk.Overlay;

namespace GameCapture.Sdk.Overlay.Tests;

public class LingerTimerStateTests
{
    [Fact]
    public void Reset_MakesAnAlreadyQueuedTimerStale()
    {
        var state = new LingerTimerState();
        var oldTimer = state.Reset();
        state.Clear();

        var currentTimer = state.Reset();

        Assert.False(state.IsCurrent(oldTimer));
        Assert.True(state.IsCurrent(currentTimer));
    }

    [Fact]
    public void Clear_InvalidatesTheCurrentTimer()
    {
        var state = new LingerTimerState();
        var timer = state.Reset();

        state.Clear();

        Assert.False(state.IsCurrent(timer));
        Assert.Equal((nuint)0, state.Current);
    }
}
