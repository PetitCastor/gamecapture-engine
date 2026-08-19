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
/// and wall-clock pacing (mode B) are the same decode path with a different wait in front of it.
/// </summary>
/// <remarks>
/// <see cref="IsReplay"/> is true in both modes, including realtime: a video, like a PNG corpus, is
/// a finite source whose null means end of stream, not "screen went idle." Do not read
/// <c>IsReplay == true</c> as "this is a PNG corpus" — see TASK-25.
/// </remarks>
internal sealed class VideoFrameSource : IFrameSource
{
    private readonly MediaComposition _composition;
    private readonly VideoFrameSourceOptions _options;
    private readonly TimeSpan _duration;
    private TimeSpan _next = TimeSpan.Zero;
    private DateTime? _pacingStartedAt;

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

    /// <param name="path">MP4 file, opened and probed synchronously so a bad path fails at startup
    /// with a message, like <c>--replay</c>'s directory check.</param>
    public VideoFrameSource(string path, VideoFrameSourceOptions options)
    {
        _options = options;

        var opened = OpenAsync(path).GetAwaiter().GetResult();
        _composition = opened.Composition;
        _duration = opened.Duration;
        Width = opened.Width;
        Height = opened.Height;
        NativeFrameRate = opened.FrameRate;
    }

    public bool IsReplay => true;

    public async Task<SoftwareBitmap?> NextFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_next >= _duration)
        {
            if (!_options.Loop)
                return null; // end of stream

            _next = TimeSpan.Zero;
            _pacingStartedAt = null; // wrap re-anchors realtime pacing to "now"
        }

        if (_options.Realtime)
            await PaceAsync(ct);

        var timestamp = _next;
        _next += _options.FrameInterval;

        var thumbnail = await _composition
            .GetThumbnailAsync(timestamp, Width, Height, VideoFramePrecision.NearestFrame)
            .AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(ct);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(ct);
    }

    /// <summary>Waits until the frame at <see cref="_next"/> is due against a wall clock anchored
    /// at the first realtime call (or the last loop wrap).</summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        _pacingStartedAt ??= DateTime.UtcNow;
        var due = _pacingStartedAt.Value + _next;
        var wait = due - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
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
