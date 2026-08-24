using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

internal readonly struct FrameReadResult
{
    private FrameReadResult(FrameReadStatus status, SoftwareBitmap? bitmap)
    {
        Status = status;
        Bitmap = bitmap;
    }

    public FrameReadStatus Status { get; }

    public SoftwareBitmap? Bitmap { get; }

    public static FrameReadResult Frame(SoftwareBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new FrameReadResult(FrameReadStatus.FrameReady, bitmap);
    }

    /// <summary>The default value is deliberately a valid idle read with no payload.</summary>
    public static FrameReadResult Idle => default;

    public static FrameReadResult EndOfStream { get; }
        = new(FrameReadStatus.EndOfStream, bitmap: null);
}
