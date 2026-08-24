using Windows.Graphics.Imaging;
using Xunit;

namespace GameCapture.Engine.Tests;

public class FrameDumpServiceTests
{
    [Fact]
    public async Task DumpFrameAsync_saves_loadable_corpus_frames_in_ordinal_order()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"frame-dump-test-{Guid.NewGuid():N}");
        try
        {
            var dumper = new FrameDumpService(tempDir);
            const int width = 320;
            const int height = 240;

            // Create 3 synthetic frames and save them with distinct timestamps
            for (var i = 0; i < 3; i++)
            {
                using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
                var savedPath = await dumper.DumpFrameAsync(bitmap);
                Assert.True(File.Exists(savedPath));
                // Ensure distinct millisecond timestamp across files
                await Task.Delay(25);
            }

            // Load back through ReplayFrameSource
            using var replay = new ReplayFrameSource(tempDir);
            Assert.Equal(3, replay.FrameCount);

            var frameNames = Directory.GetFiles(tempDir, "*.png")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(Path.GetFileName)
                .ToArray();

            Assert.Equal(3, frameNames.Length);

            for (var i = 0; i < 3; i++)
            {
                var loaded = await replay.ReadFrameAsync(CancellationToken.None);
                Assert.Equal(FrameReadStatus.FrameReady, loaded.Status);
                using var bitmap = loaded.Bitmap;
                Assert.NotNull(bitmap);
                Assert.Equal(width, bitmap.PixelWidth);
                Assert.Equal(height, bitmap.PixelHeight);
                Assert.Equal(frameNames[i], replay.LastFrameName);
            }

            // Corpus exhausted
            var afterEnd = await replay.ReadFrameAsync(CancellationToken.None);
            Assert.Equal(FrameReadStatus.EndOfStream, afterEnd.Status);
            Assert.Null(afterEnd.Bitmap);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
