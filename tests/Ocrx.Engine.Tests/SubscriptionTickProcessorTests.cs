using Ocrx.Contracts.Proto;
using Windows.Graphics.Imaging;
using Xunit;

namespace Ocrx.Engine.Tests;

public class SubscriptionTickProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WhenAReplayClientChannelIsClosed_StillProcessesAndServesTheRest()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var departedRois = new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec { Id = "departed-first", Rect = new Rect { X = 1, Width = 1, Height = 1 } },
                new RoiSpec { Id = "departed-second", Rect = new Rect { X = 2, Width = 1, Height = 1 } },
            },
        };
        var stayingRois = new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec { Id = "first", Rect = new Rect { X = 3, Width = 1, Height = 1 } },
                new RoiSpec { Id = "second", Rect = new Rect { X = 4, Width = 1, Height = 1 } },
            },
        };

        var departed = new ClientSubscription(replayMode: true);
        departed.SetRois(departedRois);
        departed.Out.Writer.TryComplete();

        var staying = new ClientSubscription(replayMode: true);
        staying.SetRois(stayingRois);

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

        Assert.Equal(["departed-first", "departed-second", "first", "second"], calls);

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
    public async Task ProcessAsync_WhenRoisDescribeEquivalentWork_ReadsOnceAndClonesTheResult()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var first = new ClientSubscription(replayMode: true);
        first.SetRois(new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec
                {
                    Id = "first",
                    Mode = RoiMode.Text,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
            },
        });

        var second = new ClientSubscription(replayMode: true);
        second.SetRois(new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec
                {
                    Id = "second",
                    Mode = RoiMode.Text,
                    Scale = -1,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
            },
        });

        var calls = 0;
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) =>
            {
                calls++;
                return Task.FromResult(new RoiResult
                {
                    RoiId = spec.Id,
                    Kind = RoiResultKind.Text,
                    Text = "shared",
                });
            });

        await processor.ProcessAsync(
            [first, second], bitmap, frameSeq: 1, manual: false, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(first.Out.Reader.TryRead(out var firstResponse));
        Assert.True(second.Out.Reader.TryRead(out var secondResponse));

        var firstResult = Assert.Single(firstResponse.Tick.Results);
        var secondResult = Assert.Single(secondResponse.Tick.Results);
        Assert.NotSame(firstResult, secondResult);
        Assert.Equal("first", firstResult.RoiId);
        Assert.Equal("second", secondResult.RoiId);
        Assert.Equal("shared", secondResult.Text);

        firstResult.Text = "changed";
        Assert.Equal("shared", secondResult.Text);
    }

    [Fact]
    public async Task ProcessAsync_WhenRoiWorkDiffers_ReadsEveryUniqueRoi()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var client = new ClientSubscription(replayMode: true);
        client.SetRois(new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec
                {
                    Id = "base",
                    Mode = RoiMode.Text,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "different-x",
                    Mode = RoiMode.Text,
                    Scale = 2,
                    Rect = new Rect { X = 2, Y = 2, Width = 3, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "different-y",
                    Mode = RoiMode.Text,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 3, Width = 3, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "different-width",
                    Mode = RoiMode.Text,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 2, Width = 4, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "different-height",
                    Mode = RoiMode.Text,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 5 },
                },
                new RoiSpec
                {
                    Id = "different-scale",
                    Mode = RoiMode.Text,
                    Scale = 3,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "different-mode",
                    Mode = RoiMode.Detailed,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
            },
        });

        var calls = new List<string>();
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) =>
            {
                calls.Add(spec.Id);
                return Task.FromResult(new RoiResult { RoiId = spec.Id });
            });

        await processor.ProcessAsync(
            [client], bitmap, frameSeq: 1, manual: false, CancellationToken.None);

        Assert.Equal(
            ["base", "different-x", "different-y", "different-width", "different-height",
                "different-scale", "different-mode"],
            calls);
    }

    [Fact]
    public async Task ProcessAsync_WhenOnlyPixelScaleDiffers_ReadsOnce()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var client = new ClientSubscription(replayMode: true);
        client.SetRois(new RoiSetUpdate
        {
            Rois =
            {
                new RoiSpec
                {
                    Id = "first",
                    Mode = RoiMode.Pixels,
                    Scale = 2,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
                new RoiSpec
                {
                    Id = "second",
                    Mode = RoiMode.Pixels,
                    Scale = 99,
                    Rect = new Rect { X = 1, Y = 2, Width = 3, Height = 4 },
                },
            },
        });

        var calls = 0;
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) =>
            {
                calls++;
                return Task.FromResult(new RoiResult { RoiId = spec.Id });
            });

        await processor.ProcessAsync(
            [client], bitmap, frameSeq: 1, manual: false, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(client.Out.Reader.TryRead(out var response));
        Assert.Equal(["first", "second"], response.Tick.Results.Select(result => result.RoiId));
    }

    [Fact]
    public async Task ProcessAsync_BetweenTicks_DoesNotReuseThePreviousFramesResult()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 12, 7, BitmapAlphaMode.Ignore);

        var client = new ClientSubscription(replayMode: true);
        client.SetRois(new RoiSetUpdate { Rois = { new RoiSpec { Id = "same" } } });

        var calls = 0;
        var processor = new SubscriptionTickProcessor(
            FrameSourceMode.ReplayCorpus,
            (_, spec) =>
            {
                calls++;
                return Task.FromResult(new RoiResult { RoiId = spec.Id, Text = calls.ToString() });
            });

        await processor.ProcessAsync(
            [client], bitmap, frameSeq: 1, manual: false, CancellationToken.None);
        Assert.True(client.Out.Reader.TryRead(out var first));

        await processor.ProcessAsync(
            [client], bitmap, frameSeq: 2, manual: false, CancellationToken.None);
        Assert.True(client.Out.Reader.TryRead(out var second));

        Assert.Equal(2, calls);
        Assert.Equal("1", Assert.Single(first.Tick.Results).Text);
        Assert.Equal("2", Assert.Single(second.Tick.Results).Text);
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
