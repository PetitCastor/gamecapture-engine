using System.Drawing;
using Ocrx.Contracts;
using Windows.Graphics.Imaging;

namespace Ocrx.Engine;

/// <summary>Maps plugin reference-space ROIs to the frame-space crop the engine actually reads.</summary>
internal static class RoiFrameMapper
{
    public static BitmapBounds MapAccepted(RoiRect reference, int frameWidth, int frameHeight)
    {
        EnsureInFrame(reference, frameWidth, frameHeight);
        return OcrPipeline.ClampToBitmap(
            RoiScaler.ToFrame(reference, frameWidth, frameHeight).ToBounds(), frameWidth, frameHeight);
    }

    /// <summary>
    /// Projects an invalid requested rectangle without the engine's clamping. Used only to explain a
    /// rejected subscription in the diagnostic overlay; never use it as an OCR crop.
    /// </summary>
    public static Rectangle ProjectRequested(RoiRect reference, int frameWidth, int frameHeight)
    {
        var left = (int)Math.Round(reference.X * (double)frameWidth / RoiScaler.ReferenceWidth);
        var top = (int)Math.Round(reference.Y * (double)frameHeight / RoiScaler.ReferenceHeight);
        var right = (int)Math.Round((reference.X + (double)reference.Width) * frameWidth / RoiScaler.ReferenceWidth);
        var bottom = (int)Math.Round((reference.Y + (double)reference.Height) * frameHeight / RoiScaler.ReferenceHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static void EnsureInFrame(RoiRect reference, int frameWidth, int frameHeight)
    {
        var x = reference.X * (double)frameWidth / RoiScaler.ReferenceWidth;
        var y = reference.Y * (double)frameHeight / RoiScaler.ReferenceHeight;
        if (reference.Width > 0 && reference.Height > 0 && x < frameWidth && y < frameHeight)
            return;

        throw new ArgumentOutOfRangeException(nameof(reference),
            $"ROI {reference.Width}x{reference.Height} at {reference.X},{reference.Y} (reference space) " +
            $"lies outside the {frameWidth}x{frameHeight} frame.");
    }
}
