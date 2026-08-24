using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GameCapture.Engine;

/// <summary>
/// Validates frame-source command-line options, then creates the selected live, corpus, or video
/// source. Validation is deliberately separate from creation so startup retains its established
/// error precedence: source arguments are checked before OCR initialization, while devices and
/// media are opened afterwards.
/// </summary>
internal sealed class FrameSourceFactory
{
    private readonly EngineConfig _config;
    private readonly string? _replayDirectory;
    private readonly string? _videoPath;
    private readonly double? _videoFps;
    private readonly bool _videoRealtime;
    private readonly bool _videoLoop;

    private FrameSourceFactory(
        EngineConfig config,
        string? replayDirectory,
        string? videoPath,
        double? videoFps,
        bool videoRealtime,
        bool videoLoop)
    {
        _config = config;
        _replayDirectory = replayDirectory;
        _videoPath = videoPath;
        _videoFps = videoFps;
        _videoRealtime = videoRealtime;
        _videoLoop = videoLoop;
    }

    public static bool TryValidate(
        string[] args,
        EngineConfig config,
        bool saveFrames,
        [NotNullWhen(true)] out FrameSourceFactory? factory,
        [NotNullWhen(false)] out string? error)
    {
        factory = null;

        if (ArgValue(args, "--monitor") is { } monitorArg)
        {
            if (!int.TryParse(monitorArg, out var monitorIndex) || monitorIndex < 0)
                return Fail($"--monitor expects a non-negative index, got '{monitorArg}'.", out error);

            config.MonitorIndex = monitorIndex;
        }

        var replayDirectory = ArgValue(args, "--replay");
        if (replayDirectory is not null && !Directory.Exists(replayDirectory))
            return Fail($"Replay directory not found: {replayDirectory}", out error);

        if (saveFrames && replayDirectory is not null)
            return Fail("--save-frames cannot be combined with --replay.", out error);

        var videoPath = ArgValue(args, "--video");
        if (videoPath is not null && !File.Exists(videoPath))
            return Fail($"Video file not found: {videoPath}", out error);

        if (videoPath is not null && replayDirectory is not null)
            return Fail("--video cannot be combined with --replay.", out error);

        if (saveFrames && videoPath is not null)
            return Fail("--save-frames cannot be combined with --video.", out error);

        var videoRealtime = args.Contains("--video-realtime", StringComparer.OrdinalIgnoreCase);
        var videoLoop = args.Contains("--video-loop", StringComparer.OrdinalIgnoreCase);

        if (videoPath is null && (videoRealtime || videoLoop))
            return Fail("--video-realtime and --video-loop require --video.", out error);

        double? videoFps = null;
        if (ArgValue(args, "--video-fps") is { } videoFpsArg)
        {
            // Invariant, not the current culture: a CLI number is a machine-facing token, and the
            // SDK replay harness formats it invariantly.
            if (!double.TryParse(videoFpsArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
                || !double.IsFinite(parsedFps) || parsedFps <= 0)
            {
                return Fail($"--video-fps expects a positive number, got '{videoFpsArg}'.", out error);
            }

            if (videoPath is null)
                return Fail("--video-fps requires --video.", out error);

            videoFps = parsedFps;
        }

        factory = new FrameSourceFactory(
            config, replayDirectory, videoPath, videoFps, videoRealtime, videoLoop);
        error = null;
        return true;
    }

    public async Task<FrameSourceCreationResult> CreateAsync(ConsoleSink sink)
    {
        if (_videoPath is not null)
            return await CreateVideoAsync();

        if (_replayDirectory is not null)
        {
            var replay = new ReplayFrameSource(_replayDirectory);
            return FrameSourceCreationResult.Success(new FrameSourceSelection(
                replay,
                $"Replay:    {replay.FrameCount} frame(s) from {_replayDirectory}",
                MonitorLabels: [],
                CurrentMonitorIndex: 0));
        }

        var monitors = MonitorCapture.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            return FrameSourceCreationResult.Failure("No monitors found.");
        }

        var monitorIndex = _config.MonitorIndex;
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            sink.WriteLine($"monitorIndex {monitorIndex} out of range, falling back to 0 (primary).");
            monitorIndex = 0;
        }

        var monitor = monitors[monitorIndex];
        var capture = new MonitorCapture(monitor.Handle);
        if (!capture.BorderDisabled)
            sink.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

        var live = new LiveFrameSource(capture);
        return FrameSourceCreationResult.Success(new FrameSourceSelection(
            live,
            $"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}",
            MonitorLabels: monitors
                .Select((item, index) =>
                    $"[{index}] {item.DeviceName} {item.Width}x{item.Height}{(item.IsPrimary ? " (primary)" : "")}")
                .ToList(),
            CurrentMonitorIndex: monitorIndex));
    }

    private async Task<FrameSourceCreationResult> CreateVideoAsync()
    {
        var effectiveFps = _videoFps ?? 1000.0 / _config.ScanIntervalMs;

        VideoFrameSource video;
        try
        {
            video = await VideoFrameSource.CreateAsync(_videoPath!, new VideoFrameSourceOptions
            {
                FrameInterval = TimeSpan.FromSeconds(1.0 / effectiveFps),
                Realtime = _videoRealtime,
                Loop = _videoLoop,
            });
        }
        catch (Exception ex)
        {
            return FrameSourceCreationResult.Failure(
                $"Failed to open video '{_videoPath}': {ex.Message}");
        }

        if (video.NativeFrameRate > 0 && effectiveFps > video.NativeFrameRate)
        {
            video.Dispose();
            return FrameSourceCreationResult.Failure(
                $"--video-fps {effectiveFps:0.###} exceeds the video's native frame rate "
                    + $"({video.NativeFrameRate:0.###} fps).");
        }

        return FrameSourceCreationResult.Success(new FrameSourceSelection(
            video,
            $"Video:     {_videoPath} {video.Width}x{video.Height}, {video.Duration:mm\\:ss\\.fff}, "
                + $"{effectiveFps:0.###} fps [{(_videoRealtime ? "realtime" : "deterministic")}{(_videoLoop ? ", loop" : "")}]",
            MonitorLabels: [],
            CurrentMonitorIndex: 0));
    }

    private static string? ArgValue(IReadOnlyList<string> args, string name)
        => args
            .Select((argument, index) => (Argument: argument, Index: index))
            .Where(item => item.Argument.Equals(name, StringComparison.OrdinalIgnoreCase)
                && item.Index + 1 < args.Count)
            .Select(item => args[item.Index + 1])
            .FirstOrDefault();

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
