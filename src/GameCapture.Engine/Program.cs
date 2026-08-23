using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using GameCapture.Engine;
using GameCapture.Engine.Metrics;
using GameCapture.Engine.Tray;

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== GameCapture — Capture Engine ===");

var configPath = EngineConfig.GetDefaultPath();
var config = EngineConfig.Load(configPath);

// CLI: --pipe <name>, --ocr-lang <bcp47>, --monitor <index> (each overrides config),
//      --replay <dir> (feed saved PNGs through the engine instead of live capture),
//      --video <path> (feed an MP4 through the engine instead of live capture; TASK-25),
//      --video-fps <n>, --video-realtime, --video-loop (video-only pacing knobs),
//      --save-frames (save full-frame PNG on manual trigger), --verbose
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
var saveFrames = args.Contains("--save-frames", StringComparer.OrdinalIgnoreCase);

string? ArgValue(string name) => args
    .Select((a, i) => (a, i))
    .Where(t => t.a.Equals(name, StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .FirstOrDefault();

var pipeName = ArgValue("--pipe") ?? config.PipeName;
if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("Pipe name must not be blank (set \"pipeName\" in engine-config.json or pass --pipe).");
    return 1;
}

if (ArgValue("--monitor") is { } monitorArg)
{
    if (!int.TryParse(monitorArg, out var monitorIndex) || monitorIndex < 0)
    {
        Console.Error.WriteLine($"--monitor expects a non-negative index, got '{monitorArg}'.");
        return 1;
    }
    config.MonitorIndex = monitorIndex;
}

var replayDir = ArgValue("--replay");
if (replayDir is not null && !Directory.Exists(replayDir))
{
    Console.Error.WriteLine($"Replay directory not found: {replayDir}");
    return 1;
}

if (saveFrames && replayDir is not null)
{
    Console.Error.WriteLine("--save-frames cannot be combined with --replay.");
    return 1;
}

var videoPath = ArgValue("--video");
if (videoPath is not null && !File.Exists(videoPath))
{
    Console.Error.WriteLine($"Video file not found: {videoPath}");
    return 1;
}

if (videoPath is not null && replayDir is not null)
{
    Console.Error.WriteLine("--video cannot be combined with --replay.");
    return 1;
}

if (saveFrames && videoPath is not null)
{
    Console.Error.WriteLine("--save-frames cannot be combined with --video.");
    return 1;
}

var videoRealtime = args.Contains("--video-realtime", StringComparer.OrdinalIgnoreCase);
var videoLoop = args.Contains("--video-loop", StringComparer.OrdinalIgnoreCase);

if (videoPath is null && (videoRealtime || videoLoop))
{
    Console.Error.WriteLine("--video-realtime and --video-loop require --video.");
    return 1;
}

double? videoFps = null;
if (ArgValue("--video-fps") is { } videoFpsArg)
{
    // Invariant, not the current culture: a CLI number is a machine-facing token, and the SDK test
    // harness (ReplayHarness, TASK-26) formats it invariantly. Parsing it against the process culture
    // would read "2.5" as 25 on a comma-decimal machine (de-DE) or reject it outright (fr-FR).
    if (!double.TryParse(videoFpsArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
        || !double.IsFinite(parsedFps) || parsedFps <= 0)
    {
        Console.Error.WriteLine($"--video-fps expects a positive number, got '{videoFpsArg}'.");
        return 1;
    }
    if (videoPath is null)
    {
        Console.Error.WriteLine("--video-fps requires --video.");
        return 1;
    }
    videoFps = parsedFps;
}

// Live and --video-realtime both unfold at their own pace, so the hotkey and metrics status bar
// mean something for them; --replay and plain --video are batch drains with no "now" to trigger
// against.
var livePaced = replayDir is null && (videoPath is null || videoRealtime);

// Missing/unsupported pack is user setup, not a bug: fail with the fix instructions, no stack trace.
OcrPipeline ocr;
try
{
    ocr = new OcrPipeline(ArgValue("--ocr-lang") ?? config.OcrLanguage);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Replay is offline and deterministic: no capture session, no hotkey (there is no live screen to
// trigger against), and no metrics timer — a 1 Hz status bar over a batch run is pure flicker.
IFrameSource source;
string captureLine;

// Populated only on the live-capture path; feeds the tray's monitor submenu.
IReadOnlyList<string> monitorLabels = [];
var currentMonitorIndex = 0;

if (videoPath is not null)
{
    var effectiveFps = videoFps ?? 1000.0 / config.ScanIntervalMs;

    VideoFrameSource video;
    try
    {
        video = new VideoFrameSource(videoPath, new VideoFrameSourceOptions
        {
            FrameInterval = TimeSpan.FromSeconds(1.0 / effectiveFps),
            Realtime = videoRealtime,
            Loop = videoLoop,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to open video '{videoPath}': {ex.Message}");
        return 1;
    }

    if (video.NativeFrameRate > 0 && effectiveFps > video.NativeFrameRate)
    {
        video.Dispose();
        Console.Error.WriteLine(
            $"--video-fps {effectiveFps:0.###} exceeds the video's native frame rate ({video.NativeFrameRate:0.###} fps).");
        return 1;
    }

    source = video;
    captureLine = $"Video:     {videoPath} {video.Width}x{video.Height}, {video.Duration:mm\\:ss\\.fff}, " +
        $"{effectiveFps:0.###} fps [{(videoRealtime ? "realtime" : "deterministic")}{(videoLoop ? ", loop" : "")}]";
}
else if (replayDir is not null)
{
    var replay = new ReplayFrameSource(replayDir);
    source = replay;
    captureLine = $"Replay:    {replay.FrameCount} frame(s) from {replayDir}";
}
else
{
    var monitors = MonitorCapture.EnumerateMonitors();
    if (monitors.Count == 0)
    {
        Console.Error.WriteLine("No monitors found.");
        return 1;
    }

    var monitorIndex = config.MonitorIndex;
    if (monitorIndex < 0 || monitorIndex >= monitors.Count)
    {
        sink.WriteLine($"monitorIndex {monitorIndex} out of range, falling back to 0 (primary).");
        monitorIndex = 0;
    }

    var monitor = monitors[monitorIndex];
    var capture = new MonitorCapture(monitor.Handle);
    if (!capture.BorderDisabled)
        sink.WriteLine("Note: OS refused to remove the yellow capture border (cosmetic only).");

    source = new LiveFrameSource(capture);
    captureLine = $"Capturing: [{monitorIndex}] {monitor.DeviceName} {monitor.Width}x{monitor.Height}";

    monitorLabels = monitors
        .Select((m, i) => $"[{i}] {m.DeviceName} {m.Width}x{m.Height}{(m.IsPrimary ? " (primary)" : "")}")
        .ToList();
    currentMonitorIndex = monitorIndex;
}

await using var engine = EngineHost.Create(pipeName, config, ocr, source, sink, verbose);

// Same fail-with-a-message contract as the OCR pack check above: a pipe name collision
// (second instance already bound, or an invalid name) is user error, not a bug.
try
{
    await engine.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start on pipe '{pipeName}': {ex.Message}");
    return 1;
}

sink.WriteLine($"Pipe:      {pipeName}");
sink.WriteLine(captureLine);
var otherOcrPacks = OcrPipeline.AvailableLanguageTags.Where(t => t != ocr.LanguageTag).ToArray();
sink.WriteLine($"OCR:       {ocr.Language}{(otherOcrPacks.Length > 0
    ? $" — also installed: {string.Join(", ", otherOcrPacks)}"
    : "")}");
sink.WriteLine($"Dumps:     {config.OutputDir}");
sink.WriteLine($"SaveFrames: {(saveFrames ? "on" : "off")}");
sink.WriteLine($"Verbose:   {(verbose ? "on" : "off")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Set by a tray monitor/settings change (on the tray STA thread): the new value is persisted to
// config, then the process relaunches so it loads cleanly. Deferred to after StopAsync so the pipe
// is released before the replacement instance tries to bind it. Written/read across threads via
// Volatile so the main thread's post-cancellation read is guaranteed to see the tray thread's write.
var restartRequested = false;

HotkeyListener? hotkey = null;
MetricsReporter? metrics = null;
TrayApplication? tray = null;

if (livePaced)
{
    var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(config.Hotkey);
    Action onHotkey;
    if (saveFrames)
    {
        var frameDumper = new FrameDumpService(config.OutputDir, sink);
        onHotkey = () =>
        {
            engine.ScanLoop.TriggerManual(frameDumper.DumpFrameAsync);
        };
    }
    else
    {
        onHotkey = engine.ScanLoop.TriggerManual;
    }

    hotkey = new HotkeyListener(modifiers, virtualKey, onHotkey);
    sink.WriteLine($"Hotkey:    {config.Hotkey} (manual trigger{(saveFrames ? " + save frame" : "")})");
    sink.WriteLine($"Metrics:   {(config.MetricsEnabled ? $"live status bar every {config.MetricsIntervalMs} ms" : "disabled")}");
    sink.WriteLine($"Tray:      {(config.TrayEnabled ? "on" : "off")}");
}

sink.WriteLine();
sink.WriteLine(livePaced
    ? "Scanning. Ctrl+C to quit."
    : "Waiting for a plugin to subscribe before replaying. Ctrl+C to quit.");
sink.WriteLine();

try
{
    // Created after the banner so it disposes before the sink: the timer is fully stopped
    // (in-flight tick drained) before the sink erases the status line on shutdown.
    if (livePaced && config.MetricsEnabled)
        metrics = new MetricsReporter(sink, TimeSpan.FromMilliseconds(config.MetricsIntervalMs));

    if (livePaced && config.TrayEnabled)
    {
        // Persist only the changed keys straight to disk — never mutate the live `config`, which the
        // gRPC service and other running components read from — then trigger the restart that applies
        // them. If the write fails, surface it and leave the engine running rather than half-applying.
        void PersistAndRestart(IReadOnlyDictionary<string, object> changes)
        {
            try
            {
                File.WriteAllText(configPath, ConfigPatch.Apply(File.ReadAllText(configPath), changes));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not save settings:\n{ex.Message}",
                    "GameCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Volatile.Write(ref restartRequested, true);
            cts.Cancel();
        }

        // Baseline the settings screen is seeded from and diffed against. ScanIntervalMs is clamped to
        // the dialog's own range so that re-opening a config with an out-of-range value and clicking OK
        // untouched is a true no-op rather than a phantom "change" that forces a restart.
        var currentSettings = new EngineSettings(
            config.OutputDir,
            config.OcrLanguage,
            Math.Clamp(config.ScanIntervalMs, 100, 60_000));

        var controls = new TrayControls(
            monitorLabels,
            currentMonitorIndex,
            currentSettings,
            OcrPipeline.AvailableLanguageTags,
            OnSelectMonitor: index =>
                PersistAndRestart(new Dictionary<string, object> { ["monitorIndex"] = index }),
            OnSaveSettings: settings =>
            {
                // Guard the one field that can kill startup: an OCR tag whose pack is not installed
                // makes the relaunched process throw in OcrPipeline's ctor and exit before the tray
                // exists — unrecoverable without hand-editing the config. Fall back to auto instead.
                var language = settings.OcrLanguage;
                if (language.Length > 0 &&
                    !OcrPipeline.AvailableLanguageTags.Contains(language, StringComparer.OrdinalIgnoreCase))
                    language = "";

                // Patch only the fields that actually changed. Writing a field that was untouched — above
                // all outputDir — would round-trip the value Load() resolved in memory back to disk,
                // baking a relative outputDir into an absolute path. That is the whole reason ConfigPatch
                // patches keys instead of reserializing the config object.
                var changes = new Dictionary<string, object>();
                if (settings.OutputDir != currentSettings.OutputDir)
                    changes["outputDir"] = settings.OutputDir;
                if (language != currentSettings.OcrLanguage)
                    changes["ocrLanguage"] = language;
                if (settings.ScanIntervalMs != currentSettings.ScanIntervalMs)
                    changes["scanIntervalMs"] = settings.ScanIntervalMs;

                if (changes.Count > 0)
                    PersistAndRestart(changes);
            },
            OnExit: cts.Cancel);

        tray = new TrayApplication(
            sink,
            engine.Status,
            config.MetricsEnabled,
            TimeSpan.FromMilliseconds(Math.Max(250, config.MetricsIntervalMs)),
            controls);
        tray.Start();
        // Feed the same sample stream the console status bar uses; the tray never ticks its own
        // sampler (MetricsSampler is stateful and single-threaded by contract).
        if (metrics is not null)
            metrics.Sampled += tray.OnMetrics;

        // Only hide the console once the tray has actually taken over as the running UI. If tray
        // startup itself failed (Session 0 / no interactive desktop — TrayApplication logs and
        // disables itself rather than throwing) the console stays as the sole fallback UI instead of
        // going dark with nothing left to observe or exit from.
        if (tray.IsActive)
            ConsoleWindowVisibility.HideUnlessDebugging();
    }

    await engine.RunScanAsync(cts.Token);
}
finally
{
    tray?.Dispose();    // remove the icon before the console summary prints
    metrics?.Dispose(); // stop status updates before the summary prints
    hotkey?.Dispose();
}

await engine.StopAsync();

sink.WriteLine();
sink.WriteLine($"Engine stopped after {engine.Status.Snapshot().FrameSeq} frame(s).");

// A tray monitor/settings change has been persisted; the pipe is now released, so relaunch the
// same run (minus the CLI overrides the config change supersedes) to apply it.
if (Volatile.Read(ref restartRequested))
{
    // Only self-relaunch when we were launched as our own apphost exe. Under `dotnet run` (the
    // documented dev workflow) ProcessPath is the shared dotnet muxer, not a command that restarts
    // the engine — spawning it with the app's args would silently fail to start anything.
    if (EngineRelaunch.IsSelfRelaunchable(Environment.ProcessPath))
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
            foreach (var arg in EngineRelaunch.StripPersistedOverrides(args))
                psi.ArgumentList.Add(arg);

            sink.WriteLine("Restarting to apply settings…");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            sink.WriteLine($"Automatic restart failed ({ex.Message}); the change is saved — restart manually to apply it.");
        }
    }
    else
    {
        sink.WriteLine("Automatic restart is unavailable (running under 'dotnet run' or an unknown host); "
            + "the change is saved — restart manually to apply it.");
    }
}

return 0;
