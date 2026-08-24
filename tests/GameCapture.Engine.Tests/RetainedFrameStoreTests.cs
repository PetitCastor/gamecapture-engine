using Windows.Graphics.Imaging;
using Xunit;

namespace GameCapture.Engine.Tests;

public class RetainedFrameStoreTests
{
    [Fact]
    public async Task SwapAsync_UpdatesTheRetainedFrameBeforeRunningTheManualHandler()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);

        var sawRetainedFrame = false;
        var gateWasHeld = true;

        await store.SwapAsync(frame, bitmap =>
        {
            sawRetainedFrame = ReferenceEquals(store.Frame, bitmap);
            gateWasHeld = !store.Gate.Wait(0);
            return Task.CompletedTask;
        });

        Assert.True(sawRetainedFrame);
        Assert.True(gateWasHeld);
        Assert.Same(frame, store.Frame);
    }

    [Fact]
    public async Task SwapAsync_WhenManualHandlerFails_KeepsTheFrameAndDoesNotPropagate()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);

        await store.SwapAsync(
            frame,
            _ => Task.FromException(new InvalidOperationException("save failed")));

        Assert.Same(frame, store.Frame);
        Assert.Equal(["[frames] failed to save frame: save failed"], messages);
    }

    [Fact]
    public async Task SwapAsync_DisposesTheSupersededFrame()
    {
        var messages = new List<string>();
        using var store = new RetainedFrameStore(messages.Add);
        var first = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);
        var second = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);

        await store.SwapAsync(first, manualFrameHandler: null);
        await store.SwapAsync(second, manualFrameHandler: null);

        AssertDisposed(first);
        Assert.Same(second, store.Frame);
    }

    [Fact]
    public async Task Dispose_DisposesAndClearsTheCurrentFrame()
    {
        var messages = new List<string>();
        var store = new RetainedFrameStore(messages.Add);
        var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 6, BitmapAlphaMode.Ignore);

        await store.SwapAsync(frame, manualFrameHandler: null);

        store.Dispose();

        Assert.Null(store.Frame);
        AssertDisposed(frame);
    }

    private static void AssertDisposed(SoftwareBitmap bitmap)
        => Assert.Throws<ObjectDisposedException>(
            () => bitmap.LockBuffer(BitmapBufferAccessMode.Read));
}
