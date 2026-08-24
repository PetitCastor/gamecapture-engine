using Windows.Graphics.Imaging;

namespace GameCapture.Engine.Benchmarks;

internal sealed class DummyFrameSource : IFrameSource
{
    public Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
        => Task.FromResult<SoftwareBitmap?>(null);

    public bool IsReplay => false;

    public void Dispose()
    {
    }
}
