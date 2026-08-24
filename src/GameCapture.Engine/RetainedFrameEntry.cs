using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

internal sealed class RetainedFrameEntry
{
    private int _referenceCount = 1;

    public RetainedFrameEntry(SoftwareBitmap bitmap)
    {
        Bitmap = bitmap;
    }

    public SoftwareBitmap Bitmap { get; }

    public RetainedFrameLease AddLease(SemaphoreSlim operationGate)
    {
        Interlocked.Increment(ref _referenceCount);
        return new RetainedFrameLease(this, operationGate);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
            Bitmap.Dispose();
    }
}
