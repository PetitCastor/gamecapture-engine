using System.Threading.Channels;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine.Benchmarks;

internal sealed class BenchmarkFrameSource : IFrameSource
{
    private readonly Channel<SoftwareBitmap> _frames = Channel.CreateUnbounded<SoftwareBitmap>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public bool IsReplay => true;

    public Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
        => ReadNextAsync(ct);

    public void Publish(SoftwareBitmap frame)
    {
        if (!_frames.Writer.TryWrite(frame))
        {
            frame.Dispose();
            throw new InvalidOperationException("The benchmark frame source is closed.");
        }
    }

    public void Dispose()
    {
        _frames.Writer.TryComplete();
        while (_frames.Reader.TryRead(out var frame))
            frame.Dispose();
    }

    private async Task<SoftwareBitmap?> ReadNextAsync(CancellationToken ct)
        => await _frames.Reader.ReadAsync(ct);
}
