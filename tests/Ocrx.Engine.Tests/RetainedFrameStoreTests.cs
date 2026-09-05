using Windows.Graphics.Imaging;
using Xunit;

namespace Ocrx.Engine.Tests;

public class RetainedFrameStoreTests
{
    [Fact]
    public async Task SwapAsync_UpdatesTheRetainedFrameBeforeRunningTheManualHandler()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var frame = CreateBitmap();
        var sawRetainedFrame = false;

        await store.SwapAsync(frame, async bitmap =>
        {
            using var lease = await store.AcquireLeaseAsync(CancellationToken.None);
            sawRetainedFrame = ReferenceEquals(lease?.Bitmap, bitmap);
        });

        Assert.True(sawRetainedFrame);
        using var retained = await store.AcquireLeaseAsync(CancellationToken.None);
        Assert.Same(frame, retained!.Bitmap);
    }

    [Fact]
    public async Task SwapAsync_WhenManualHandlerFails_KeepsTheFrameAndDoesNotPropagate()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var frame = CreateBitmap();

        await store.SwapAsync(
            frame,
            _ => Task.FromException(new InvalidOperationException("save failed")));

        using var retained = await store.AcquireLeaseAsync(CancellationToken.None);
        Assert.Same(frame, retained!.Bitmap);
        Assert.Equal(["[frames] failed to save frame: save failed"], messages);
    }

    [Fact]
    public async Task SwapAsync_DisposesTheSupersededFrameWithoutALease()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var first = CreateBitmap();
        var second = CreateBitmap();

        await store.SwapAsync(first, manualFrameHandler: null);
        await store.SwapAsync(second, manualFrameHandler: null);

        AssertDisposed(first);
        using var retained = await store.AcquireLeaseAsync(CancellationToken.None);
        Assert.Same(second, retained!.Bitmap);
    }

    [Fact]
    public async Task AcquireLeaseAsync_ReturnsNullWhenNoFrameIsAvailable()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);

        var lease = await store.AcquireLeaseAsync(CancellationToken.None);

        Assert.Null(lease);
    }

    [Fact]
    public async Task SwapAsync_KeepsTheSupersededFrameAliveUntilItsLeasesEnd()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var first = CreateBitmap();
        var second = CreateBitmap();

        await store.SwapAsync(first, manualFrameHandler: null);
        var firstLease = await store.AcquireLeaseAsync(CancellationToken.None);

        await store.SwapAsync(second, manualFrameHandler: null);

        Assert.Same(first, firstLease!.Bitmap);
        AssertNotDisposed(first);

        firstLease.Dispose();
        AssertDisposed(first);

        using var retained = await store.AcquireLeaseAsync(CancellationToken.None);
        Assert.Same(second, retained!.Bitmap);
    }

    [Fact]
    public async Task AcquireLeaseAsync_BoundsConcurrentRetainedFrameOperations()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var frame = CreateBitmap();
        await store.SwapAsync(frame, manualFrameHandler: null);

        var first = await store.AcquireLeaseAsync(CancellationToken.None);
        using var second = await store.AcquireLeaseAsync(CancellationToken.None);
        var third = store.AcquireLeaseAsync(CancellationToken.None).AsTask();

        Assert.False(third.IsCompleted);

        first!.Dispose();
        using var admitted = await third;
        Assert.Same(frame, admitted!.Bitmap);
    }

    [Fact]
    public async Task AcquireLeaseAsync_CanBeCancelledWhileTheOperationGateIsFull()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        await store.SwapAsync(CreateBitmap(), manualFrameHandler: null);
        using var first = await store.AcquireLeaseAsync(CancellationToken.None);
        using var second = await store.AcquireLeaseAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = store.AcquireLeaseAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task AcquireLeaseAsync_WaiterSeesShutdownAfterConcurrentReadsDrain()
    {
        var messages = new List<string>();
        var store = new RetainedFrameStore(messages.Add);
        await store.SwapAsync(CreateBitmap(), manualFrameHandler: null);
        using var first = await store.AcquireLeaseAsync(CancellationToken.None);
        using var second = await store.AcquireLeaseAsync(CancellationToken.None);
        var waiting = store.AcquireLeaseAsync(CancellationToken.None).AsTask();

        store.Dispose();
        first!.Dispose();
        second!.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
    }

    [Fact]
    public async Task Dispose_KeepsTheFrameAliveUntilOutstandingLeasesEnd()
    {
        var messages = new List<string>();
        var store = new RetainedFrameStore(messages.Add);
        var frame = CreateBitmap();
        await store.SwapAsync(frame, manualFrameHandler: null);
        var lease = await store.AcquireLeaseAsync(CancellationToken.None);

        store.Dispose();

        Assert.Same(frame, lease!.Bitmap);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.AcquireLeaseAsync(CancellationToken.None).AsTask());

        lease.Dispose();
        AssertDisposed(frame);
    }

    [Fact]
    public async Task Dispose_DisposesTheCurrentFrameWhenNoLeaseIsOutstanding()
    {
        var messages = new List<string>();
        var store = new RetainedFrameStore(messages.Add);
        var frame = CreateBitmap();
        await store.SwapAsync(frame, manualFrameHandler: null);

        store.Dispose();

        AssertDisposed(frame);
    }

    private static SoftwareBitmap CreateBitmap()
        => new(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);

    private static void AssertNotDisposed(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        Assert.NotNull(buffer);
    }

    private static void AssertDisposed(SoftwareBitmap bitmap)
        => Assert.Throws<ObjectDisposedException>(
            () => bitmap.LockBuffer(BitmapBufferAccessMode.Read));
}
