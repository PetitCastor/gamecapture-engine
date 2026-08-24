using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

internal sealed class RetainedFrameLease : IDisposable
{
    private RetainedFrameEntry? _entry;
    private readonly SemaphoreSlim _operationGate;

    public RetainedFrameLease(RetainedFrameEntry entry, SemaphoreSlim operationGate)
    {
        _entry = entry;
        _operationGate = operationGate;
    }

    public SoftwareBitmap Bitmap
        => _entry?.Bitmap ?? throw new ObjectDisposedException(nameof(RetainedFrameLease));

    public void Dispose()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is null)
            return;

        try
        {
            entry.Release();
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
