using GameCapture.Engine.Tray;
using Xunit;

namespace GameCapture.Engine.Tests.Tray;

/// <summary>
/// Pins <see cref="FrameRateTracker"/>'s arithmetic: it turns successive frame_seq readings into a
/// scanned rate, needs two readings before it reports one, and refuses to produce a negative or
/// divide-by-zero figure when the engine restarts or a timer double-fires.
/// </summary>
public class FrameRateTrackerTests
{
    [Fact]
    public void FirstObservation_OnlyEstablishesBaseline()
    {
        var tracker = new FrameRateTracker();

        tracker.Observe(100, TimeSpan.FromSeconds(1));

        Assert.Null(tracker.Fps);
    }

    [Fact]
    public void TwoObservations_ComputeFramesPerSecond()
    {
        var tracker = new FrameRateTracker();

        tracker.Observe(100, TimeSpan.FromSeconds(1));
        tracker.Observe(110, TimeSpan.FromSeconds(2));

        Assert.Equal(5.0, tracker.Fps!.Value, precision: 6);
    }

    [Fact]
    public void SequenceGoingBackwards_ResetsAndClearsTheRate()
    {
        var tracker = new FrameRateTracker();
        tracker.Observe(100, TimeSpan.FromSeconds(1));
        tracker.Observe(110, TimeSpan.FromSeconds(1));
        Assert.NotNull(tracker.Fps);

        // An engine restart rewinds frame_seq; the old delta would be a huge negative spike.
        tracker.Observe(3, TimeSpan.FromSeconds(1));

        Assert.Null(tracker.Fps);
    }

    [Fact]
    public void NonPositiveGap_LeavesThePreviousRateUntouched()
    {
        var tracker = new FrameRateTracker();
        tracker.Observe(100, TimeSpan.FromSeconds(1));
        tracker.Observe(110, TimeSpan.FromSeconds(1));
        var established = tracker.Fps;

        tracker.Observe(120, TimeSpan.Zero);

        Assert.Equal(established, tracker.Fps);
    }

    [Fact]
    public void StalledSequence_ReadsZero()
    {
        var tracker = new FrameRateTracker();
        tracker.Observe(100, TimeSpan.FromSeconds(1));
        tracker.Observe(100, TimeSpan.FromSeconds(1));

        Assert.Equal(0.0, tracker.Fps!.Value, precision: 6);
    }
}
