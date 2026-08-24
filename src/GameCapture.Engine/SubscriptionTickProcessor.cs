using System.Threading.Channels;
using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Builds and delivers one tick per subscribed client for a frame the scan loop has already
/// claimed. The loop keeps sequencing, cadence, and status ownership; this type owns only the
/// per-client distribution semantics.
/// </summary>
internal sealed class SubscriptionTickProcessor
{
    private readonly bool _replayMode;
    private readonly Func<SoftwareBitmap, RoiSpec, Task<RoiResult>> _readOneAsync;

    public SubscriptionTickProcessor(
        FrameSourceMode sourceMode,
        Func<SoftwareBitmap, RoiSpec, Task<RoiResult>> readOneAsync)
    {
        _replayMode = sourceMode.UsesReplayFlow();
        _readOneAsync = readOneAsync;
    }

    public async Task ProcessAsync(
        IReadOnlyList<ClientSubscription> clients,
        SoftwareBitmap bitmap,
        ulong frameSeq,
        bool manual,
        CancellationToken ct)
    {
        foreach (var client in clients)
        {
            var tick = new TickResult
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FrameSeq = frameSeq,
                FrameWidth = (uint)bitmap.PixelWidth,
                FrameHeight = (uint)bitmap.PixelHeight,
                Manual = manual,
            };

            foreach (var spec in client.Rois)
                tick.Results.Add(await _readOneAsync(bitmap, spec));

            var response = new TrackResponse { Tick = tick };
            if (_replayMode)
            {
                // Backpressure: determinism first. Unlike the live TryWrite, this write can fail —
                // a plugin that disposes its session (or dies) has its channel completed by the
                // registry, and the throw would otherwise end the run for every client.
                try
                {
                    await client.Out.Writer.WriteAsync(response, ct);
                }
                catch (ChannelClosedException)
                {
                    continue;
                }
            }
            else
            {
                client.Out.Writer.TryWrite(response); // DropOldest handles overflow
            }
        }
    }
}
