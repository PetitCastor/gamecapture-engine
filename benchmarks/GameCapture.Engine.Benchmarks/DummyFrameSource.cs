namespace GameCapture.Engine.Benchmarks;

internal sealed class DummyFrameSource : IFrameSource
{
    public ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken ct)
        => ValueTask.FromResult(FrameReadResult.Idle);

    public FrameSourceMode Mode => FrameSourceMode.LiveCapture;

    public void Dispose()
    {
    }
}
