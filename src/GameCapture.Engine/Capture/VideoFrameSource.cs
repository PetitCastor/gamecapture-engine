using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace GameCapture.Engine;

/// <summary>
/// Video-backed frame source (TASK-25): decodes an MP4 through
/// <see cref="MediaComposition.GetThumbnailAsync"/> and hands the scan loop one
/// <see cref="SoftwareBitmap"/> per sampled timestamp. Pull-based on demand, like
/// <see cref="ReplayFrameSource"/> — nothing is buffered ahead — so deterministic stepping (mode A)
/// and monotonic realtime pacing (mode B) are the same decode path with a different wait in front
/// of it.
/// </summary>
/// <remarks>
/// Videos use replay flow in both deterministic and realtime modes and distinguish end-of-stream
/// from live-capture idle. Their explicit mode also preserves realtime video's interactive desktop
/// controls without conflating those controls with scan-loop backpressure.
/// </remarks>
internal sealed class VideoFrameSource : IFrameSource
{
    private readonly MediaComposition _composition;
    private readonly VideoFrameSourceOptions _options;
    private readonly TimeSpan _duration;
    private TimeSpan _next = TimeSpan.Zero;
    private long? _pacingStartedAtTimestamp;

    /// <summary>Native frame width, probed from the file at construction.</summary>
    public int Width { get; }

    /// <summary>Native frame height, probed from the file at construction.</summary>
    public int Height { get; }

    /// <summary>Video timeline length; one loop of sampling covers exactly this span.</summary>
    public TimeSpan Duration => _duration;

    /// <summary>
    /// Native frame rate from the container's own metadata, or 0 when the shell property system
    /// has none to offer. Callers validating a requested sampling rate against it should treat 0
    /// as "unknown" and skip the check rather than reject every video with unusual metadata.
    /// </summary>
    public double NativeFrameRate { get; }

    private VideoFrameSource(
        MediaComposition composition,
        VideoFrameSourceOptions options,
        TimeSpan duration,
        int width,
        int height,
        double frameRate)
    {
        _composition = composition;
        _options = options;
        _duration = duration;
        Width = width;
        Height = height;
        NativeFrameRate = frameRate;
    }

    /// <param name="path">MP4 file, opened and probed asynchronously so a bad path still fails at
    /// startup with a message, like <c>--replay</c>'s directory check.</param>
    public static async Task<VideoFrameSource> CreateAsync(string path, VideoFrameSourceOptions options)
    {
        var opened = await OpenAsync(path);
        return new VideoFrameSource(
            opened.Composition,
            options,
            opened.Duration,
            opened.Width,
            opened.Height,
            opened.FrameRate);
    }

    public FrameSourceMode Mode => _options.Realtime
        ? FrameSourceMode.RealtimeVideo
        : FrameSourceMode.DeterministicVideo;

    public async ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_next >= _duration)
        {
            if (!_options.Loop)
                return FrameReadResult.EndOfStream; // end of stream

            _next = TimeSpan.Zero;
            _pacingStartedAtTimestamp = null; // wrap re-anchors realtime pacing to "now"
        }

        if (_options.Realtime)
            await PaceAsync(ct);

        var timestamp = _next;
        _next += _options.FrameInterval;

        var thumbnail = await _composition
            .GetThumbnailAsync(timestamp, Width, Height, VideoFramePrecision.NearestFrame)
            .AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(ct);
        return FrameReadResult.Frame(
            await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(ct));
    }

    /// <summary>Waits until the frame at <see cref="_next"/> is due against a monotonic clock
    /// anchored at the first realtime call (or the last loop wrap).</summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        _pacingStartedAtTimestamp ??= _options.TimeProvider.GetTimestamp();
        var elapsed = _options.TimeProvider.GetElapsedTime(_pacingStartedAtTimestamp.Value);
        var wait = _next - elapsed;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _options.TimeProvider, ct);
    }

    public void Dispose()
    {
        // MediaComposition/MediaClip are WinRT-projected COM objects with no unmanaged handle of
        // ours to release; the StorageFile reference drops with the composition itself.
    }

    private static async Task<(MediaComposition Composition, int Width, int Height, TimeSpan Duration, double FrameRate)> OpenAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Video not found: {path}", path);

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var properties = await file.Properties.GetVideoPropertiesAsync();
        var frameRate = await ReadFrameRateAsync(file);

        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        return (composition, (int)properties.Width, (int)properties.Height, composition.Duration, frameRate);
    }

    /// <summary>
    /// <c>System.Video.FrameRate</c> is a shell property-system key, not a WinRT media API: it
    /// reports frames per second multiplied by 1000, and is absent for some containers/codecs the
    /// property handler doesn't recognise — hence the null-coalesce to "unknown" rather than 0 fps.
    /// </summary>
    private static async Task<double> ReadFrameRateAsync(StorageFile file)
    {
        var props = await file.Properties.RetrievePropertiesAsync(["System.Video.FrameRate"]);
        return props.TryGetValue("System.Video.FrameRate", out var value) && value is uint milliHertz
            ? milliHertz / 1000.0
            : 0.0;
    }
}
