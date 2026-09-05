namespace Ocrx.Engine;

/// <summary>
/// Where the scan loop gets frames from. The abstraction exists so live capture and replay are
/// the same code path end to end: a replay run exercises the real registry, the real per-tick
/// assembly and the real gRPC streams, not a parallel implementation that can drift from them.
/// </summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>
    /// Next frame-state transition to scan. Live: the latest WGC frame downloaded to CPU, or
    /// <see cref="FrameReadStatus.Idle"/> while the screen is idle and no new frame has arrived.
    /// Replay/video: the next decoded frame, or <see cref="FrameReadStatus.EndOfStream"/> once the
    /// finite source is exhausted. Caller owns the returned bitmap when status is
    /// <see cref="FrameReadStatus.FrameReady"/>.
    /// </summary>
    ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken ct);

    /// <summary>
    /// The source category the loop is serving. Distinguishes live capture from finite replay
    /// inputs without overloading a boolean that video also had to pretend to satisfy.
    /// </summary>
    FrameSourceMode Mode { get; }
}
