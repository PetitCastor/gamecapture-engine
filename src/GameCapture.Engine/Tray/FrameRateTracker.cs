namespace GameCapture.Engine.Tray;

/// <summary>
/// Turns successive <c>frame_seq</c> readings into a scanned frames-per-second figure — the rate the
/// engine is <em>actually</em> processing frames, which is not the configured cadence: a slow OCR
/// pass, a stalled game, or live backpressure all pull it below <c>1000 / scanIntervalMs</c>.
/// </summary>
/// <remarks>
/// Deliberately does not own a clock so it stays a pure, testable function of its inputs — the caller
/// (the tray's UI timer) measures the wall gap between polls and passes it in. Not thread-safe: the
/// tray observes it from one thread (the UI timer callback) only.
/// </remarks>
public sealed class FrameRateTracker
{
    private ulong? _lastSeq;

    /// <summary>Most recent computed rate, or <c>null</c> until two readings establish one.</summary>
    public double? Fps { get; private set; }

    /// <summary>
    /// Feeds one <paramref name="seq"/> reading taken <paramref name="elapsed"/> after the previous
    /// one. The first call only establishes a baseline (Fps stays null). A sequence that goes
    /// backwards — an engine restart, or a replay corpus rewinding — resets the baseline and clears
    /// the rate rather than reporting a negative or absurd spike. A non-positive gap is ignored so a
    /// double-fired timer can never divide by zero.
    /// </summary>
    public void Observe(ulong seq, TimeSpan elapsed)
    {
        if (_lastSeq is not { } last || seq < last)
        {
            _lastSeq = seq;
            Fps = null;
            return;
        }

        if (elapsed <= TimeSpan.Zero)
            return;

        Fps = (seq - last) / elapsed.TotalSeconds;
        _lastSeq = seq;
    }
}
