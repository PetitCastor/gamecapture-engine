using System.Threading.Channels;
using Windows.Graphics.Imaging;

namespace Ocrx.Engine.Benchmarks;

internal sealed class BenchmarkFrameSource : IFrameSource
{
    private readonly Channel<SoftwareBitmap> _frames = Channel.CreateUnbounded<SoftwareBitmap>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public FrameSourceMode Mode => FrameSourceMode.ReplayCorpus;

    public ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken ct)
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

    private async ValueTask<FrameReadResult> ReadNextAsync(CancellationToken ct)
        => FrameReadResult.Frame(await _frames.Reader.ReadAsync(ct));
}
