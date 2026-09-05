using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ocrx.Engine.Benchmarks;

internal static class BenchmarkBitmapFactory
{
    public static SoftwareBitmap CreateFrame(int width = 2560, int height = 1440)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var bytes = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                bytes[i] = (byte)(x % 256);
                bytes[i + 1] = (byte)(y % 256);
                bytes[i + 2] = (byte)((x + y) % 256);
                bytes[i + 3] = 255;
            }
        }

        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }
}
