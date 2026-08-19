using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// A video is a stand-in for either a live session or a PNG corpus, so it has to earn both
/// contracts: deterministic stepping must land on the exact interval a caller asked for (a plugin
/// under dev reasons about "one tick per 0.5s of footage"), and end-of-stream/loop/cancellation
/// must match what <c>ScanLoop</c> already assumes about <see cref="IFrameSource"/>. The fixture's
/// burned-in per-frame timestamp exists so "order matches interval" is an assertion about actual
/// decoded pixels, not just a call count that would pass even if every sample silently returned
/// frame zero.
/// </summary>
public class VideoFrameSourceTests
{
    private static readonly TimeSpan FixtureDuration = TimeSpan.FromSeconds(3);

    private static byte[] ToBytes(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task NextFrameAsync_SamplesAtRequestedInterval_ReturnsDistinctFramesInOrder()
    {
        // 0.5s steps over a 3.0s/30fps fixture land exactly on frame boundaries (15 frames apart),
        // so _next hits Duration exactly on the 7th call: 0, 0.5, 1.0, 1.5, 2.0, 2.5, then 3.0 >= 3.0.
        var options = new VideoFrameSourceOptions { FrameInterval = TimeSpan.FromSeconds(0.5) };
        using var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);

        Assert.Equal(320, source.Width);
        Assert.Equal(180, source.Height);
        Assert.Equal(FixtureDuration, source.Duration);

        var frames = new List<byte[]>();
        while (await source.NextFrameAsync(CancellationToken.None) is { } bitmap)
        {
            using (bitmap)
            {
                Assert.Equal(BitmapPixelFormat.Bgra8, bitmap.BitmapPixelFormat);
                Assert.Equal(source.Width, bitmap.PixelWidth);
                Assert.Equal(source.Height, bitmap.PixelHeight);
                frames.Add(ToBytes(bitmap));
            }
        }

        Assert.Equal(6, frames.Count);

        // Each sample's burned-in timestamp differs from its neighbour's, so a decode that got
        // stuck re-reading the same source frame (e.g. a timestamp unit bug) shows up as a
        // duplicate here instead of silently passing on count alone.
        for (var i = 1; i < frames.Count; i++)
            Assert.False(frames[i - 1].AsSpan().SequenceEqual(frames[i]), $"frame {i - 1} and {i} were pixel-identical");
    }

    [Fact]
    public async Task NextFrameAsync_WithoutLoop_ReturnsNullAtEndOfStream()
    {
        // 1.0s steps: 0, 1.0, 2.0 succeed; the 4th call's _next is 3.0, which is >= Duration.
        var options = new VideoFrameSourceOptions { FrameInterval = TimeSpan.FromSeconds(1) };
        using var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);

        for (var i = 0; i < 3; i++)
        {
            using var bitmap = await source.NextFrameAsync(CancellationToken.None);
            Assert.NotNull(bitmap);
        }

        Assert.Null(await source.NextFrameAsync(CancellationToken.None));

        // Exhausted stays exhausted, same contract ReplayFrameSource gives the scan loop.
        Assert.Null(await source.NextFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NextFrameAsync_WithLoop_WrapsToStartInsteadOfEnding()
    {
        var options = new VideoFrameSourceOptions { FrameInterval = TimeSpan.FromSeconds(1), Loop = true };
        using var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);

        byte[] firstFrame;
        using (var bitmap = await source.NextFrameAsync(CancellationToken.None))
        {
            Assert.NotNull(bitmap);
            firstFrame = ToBytes(bitmap!);
        }

        // Two more samples (t=1.0, t=2.0) before the wrap point.
        for (var i = 0; i < 2; i++)
        {
            using var bitmap = await source.NextFrameAsync(CancellationToken.None);
            Assert.NotNull(bitmap);
        }

        // 4th call: _next was 3.0 (>= Duration), so this wraps to 0.0 and decodes the same
        // timestamp as the very first call, rather than returning null.
        using (var wrapped = await source.NextFrameAsync(CancellationToken.None))
        {
            Assert.NotNull(wrapped);
            Assert.True(firstFrame.AsSpan().SequenceEqual(ToBytes(wrapped!)), "wrapped frame should decode the same timestamp as the first frame");
        }

        // One more call past the wrap proves the stream keeps going rather than ending right there
        // — bounded to a single extra sample so this test doesn't drain an infinite loop source.
        using var afterWrap = await source.NextFrameAsync(CancellationToken.None);
        Assert.NotNull(afterWrap);
    }

    [Fact]
    public async Task NextFrameAsync_CancelledMidDecode_ThrowsOperationCanceled()
    {
        var options = new VideoFrameSourceOptions { FrameInterval = TimeSpan.FromSeconds(0.5) };
        using var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);
        using var cts = new CancellationTokenSource();

        var pending = source.NextFrameAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task NextFrameAsync_Realtime_PacesMonotonicallyWithAtLeastTheRequestedInterval()
    {
        var interval = TimeSpan.FromMilliseconds(150);
        var options = new VideoFrameSourceOptions { FrameInterval = interval, Realtime = true };
        using var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);
        var stopwatch = Stopwatch.StartNew();

        // Only a lower bound per frame: decode time, GC pauses, and CI scheduler noise can only
        // push actual elapsed time up, never down, so an upper-bound assertion here would flake.
        var tolerance = TimeSpan.FromMilliseconds(20);
        var elapsedAtFrame = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            using var bitmap = await source.NextFrameAsync(CancellationToken.None);
            Assert.NotNull(bitmap);
            elapsedAtFrame.Add(stopwatch.Elapsed);
        }

        for (var i = 1; i < elapsedAtFrame.Count; i++)
            Assert.True(elapsedAtFrame[i] >= elapsedAtFrame[i - 1], "pacing went backwards between frames");

        Assert.True(elapsedAtFrame[1] >= interval - tolerance,
            $"frame 1 due at ~{interval.TotalMilliseconds}ms, arrived at {elapsedAtFrame[1].TotalMilliseconds}ms");
        Assert.True(elapsedAtFrame[2] >= interval + interval - tolerance,
            $"frame 2 due at ~{(interval + interval).TotalMilliseconds}ms, arrived at {elapsedAtFrame[2].TotalMilliseconds}ms");
    }

    [Fact]
    public void Dispose_ReleasesTheFileHandle_SoTheFileCanBeReopened()
    {
        var options = new VideoFrameSourceOptions { FrameInterval = TimeSpan.FromSeconds(1) };
        var source = new VideoFrameSource(EngineTestFixtures.VideoPath, options);
        source.Dispose();

        // If Dispose left the file open, this re-open would throw (or the ctor's own probe would).
        using var reopened = new VideoFrameSource(EngineTestFixtures.VideoPath, options);
        Assert.Equal(320, reopened.Width);
    }
}
