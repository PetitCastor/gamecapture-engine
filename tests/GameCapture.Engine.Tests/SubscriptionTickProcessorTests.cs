using GameCapture.Contracts.Proto;
using Windows.Graphics.Imaging;
using Xunit;

namespace GameCapture.Engine.Tests;

public class SubscriptionTickProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WhenAReplayClientChannelIsClosed_StillProcessesAndServesTheRest()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var rois = new RoiSetUpdate
        {
            Rois = { new RoiSpec { Id = "first" }, new RoiSpec { Id = "second" } },
        };

        var departed = new ClientSubscription(replayMode: true);
        departed.SetRois(rois);
        departed.Out.Writer.TryComplete();

        var staying = new ClientSubscription(replayMode: true);
        staying.SetRois(rois);

        var calls = new List<string>();
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (frame, spec) =>
            {
                calls.Add(spec.Id);
                return Task.FromResult(new RoiResult { RoiId = spec.Id, Kind = RoiResultKind.Text, Text = spec.Id });
            });

        await processor.ProcessAsync(
            [departed, staying], bitmap, frameSeq: 7, manual: true, CancellationToken.None);

        Assert.Equal(["first", "second", "first", "second"], calls);

        Assert.True(staying.Out.Reader.TryRead(out var response));
        var tick = response.Tick;
        Assert.Equal(7ul, tick.FrameSeq);
        Assert.Equal((uint)bitmap.PixelWidth, tick.FrameWidth);
        Assert.Equal((uint)bitmap.PixelHeight, tick.FrameHeight);
        Assert.True(tick.Manual);
        Assert.Equal(["first", "second"], tick.Results.Select(result => result.RoiId));

        Assert.False(departed.Out.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ProcessAsync_InLiveMode_DropsOldestTicksWithoutBlocking()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var client = new ClientSubscription(replayMode: false);
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.LiveCapture,
            (_, spec) => Task.FromResult(new RoiResult { RoiId = spec.Id }));

        for (ulong frameSeq = 1; frameSeq <= 5; frameSeq++)
            await processor.ProcessAsync([client], bitmap, frameSeq, manual: false, CancellationToken.None);

        var retainedSequences = new List<ulong>();
        while (client.Out.Reader.TryRead(out var response))
            retainedSequences.Add(response.Tick.FrameSeq);

        Assert.Equal([2ul, 3ul, 4ul, 5ul], retainedSequences);
    }

    [Fact]
    public async Task ProcessAsync_WhenReplayChannelIsFull_WaitsForCapacity()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var client = new ClientSubscription(replayMode: true);
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) => Task.FromResult(new RoiResult { RoiId = spec.Id }));

        for (ulong frameSeq = 1; frameSeq <= 4; frameSeq++)
            await processor.ProcessAsync([client], bitmap, frameSeq, manual: false, CancellationToken.None);

        var blocked = processor.ProcessAsync(
            [client], bitmap, frameSeq: 5, manual: false, CancellationToken.None);
        Assert.False(blocked.IsCompleted);

        Assert.True(client.Out.Reader.TryRead(out var oldest));
        Assert.Equal(1ul, oldest.Tick.FrameSeq);

        await blocked;

        var retainedSequences = new List<ulong>();
        while (client.Out.Reader.TryRead(out var response))
            retainedSequences.Add(response.Tick.FrameSeq);

        Assert.Equal([2ul, 3ul, 4ul, 5ul], retainedSequences);
    }

    [Fact]
    public async Task ProcessAsync_WhenReplayWriteIsBlocked_PropagatesCancellation()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);
        using var cts = new CancellationTokenSource();

        var client = new ClientSubscription(replayMode: true);
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) => Task.FromResult(new RoiResult { RoiId = spec.Id }));

        for (ulong frameSeq = 1; frameSeq <= 4; frameSeq++)
            await processor.ProcessAsync([client], bitmap, frameSeq, manual: false, CancellationToken.None);

        var blocked = processor.ProcessAsync([client], bitmap, frameSeq: 5, manual: false, cts.Token);
        Assert.False(blocked.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }
}
