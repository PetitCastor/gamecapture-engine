using GameCapture.Contracts.Proto;
using Google.Protobuf;

namespace GameCapture.Contracts;

internal static class PixelPatchFactory
{
    public static PixelPatchSampler Create(RoiResult result)
    {
        var rect = result.FrameRect ?? new Rect();
        var bgra = result.PixelsBgra.ToByteArray();
        var stride = unchecked((int)result.PixelsStride);
        var width = unchecked((int)result.PixelsWidth);
        var height = unchecked((int)result.PixelsHeight);

        if (stride < 0 || width < 0 || height < 0)
            throw new RoiResultException(result.RoiId,
                $"pixel geometry overflows int (stride {result.PixelsStride}, " +
                $"{result.PixelsWidth}x{result.PixelsHeight}).",
                reportedByEngine: false);

        if (stride < width * 4L)
            throw new RoiResultException(result.RoiId,
                $"pixels_stride {stride} is shorter than one row of {width} BGRA pixels.",
                reportedByEngine: false);

        if (bgra.LongLength < (long)stride * height)
            throw new RoiResultException(result.RoiId,
                $"pixels_bgra has {bgra.LongLength} bytes, needs {(long)stride * height} for " +
                $"{width}x{height} at stride {stride}.",
                reportedByEngine: false);

        return new PixelPatchSampler(
            bgra,
            stride,
            width,
            height,
            unchecked((int)rect.X),
            unchecked((int)rect.Y));
    }
}
