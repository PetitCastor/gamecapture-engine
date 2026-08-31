using GameCapture.Engine;
using GameCapture.Engine.Shell;
using GameCapture.Engine.Updates;
using Velopack;

// Ahead of every other statement, including the console allocation below, because this is not
// normal startup: the Velopack Setup.exe re-invokes this same executable with hook arguments
// (--veloapp-install, --veloapp-updated, --veloapp-uninstall) during install and update, and Run()
// services those and exits the process. The arg parsing further down matches only flags it knows
// and ignores the rest, so without this line a hook invocation would fall through into a full
// engine start — tray icon, gRPC listener — and never exit, leaving Setup blocked on its hook
// timeout with a stray engine running. Run() is a no-op on an ordinary user launch.
VelopackApp.Build().Run();

// Immediately after the Velopack hook check above — never before it, or an install/update
// re-invocation would be mistaken for a duplicate launch and hang Setup on its hook timeout — and
// before anything else: no console, no banner, no pipe bind. A second launch signals the running
// instance (which brings its window forward) and exits 0 here with no console noise, instead of
// falling through to the pipe-collision path further down that used to read as a broken app.
using var singleInstance = SingleInstance.Acquire();
if (singleInstance is null)
    return 0;

ConsoleWindowVisibility.EnsureDebugConsole();

// First statement so every later write goes through it and disposal (status-bar erase,
// cursor restore) is guaranteed on every return path.
using var sink = new ConsoleSink();

sink.WriteLine("=== GameCapture — Capture Engine ===");

// Awaited, not fire-and-forget: a "yes" to the update prompt restarts the process, so this must
// run to completion before the pipe is bound and plugins can connect — an update accepted mid-session
// would be an abrupt kill instead of a clean startup gate.
await EngineUpdateChecker.CheckAsync(sink);

var configPath = EngineConfig.GetDefaultPath();
EngineConfig config;
try
{
    config = EngineConfig.Load(configPath);
}
catch (Exception ex)
{
    StartupDiagnostics.Report($"Could not load engine configuration '{configPath}'.", ex);
    return 1;
}

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
    StartupDiagnostics.Report("Pipe name must not be blank (set \"pipeName\" in engine-config.json or pass --pipe).");
    return 1;
}

if (!FrameSourceFactory.TryValidate(args, config, saveFrames, out var sourceFactory, out var sourceError))
{
    StartupDiagnostics.Report(sourceError);
    return 1;
}

// Missing/unsupported pack is user setup, not a bug: fail with the fix instructions, no stack trace.
OcrPipeline ocr;
try
{
    ocr = new OcrPipeline(ArgValue("--ocr-lang") ?? config.OcrLanguage);
}
catch (InvalidOperationException ex)
{
    StartupDiagnostics.Report(ex.Message, ex);
    return 1;
}

var sourceCreation = await sourceFactory.CreateAsync(sink);
if (!sourceCreation.Succeeded)
{
    StartupDiagnostics.Report(sourceCreation.Error);
    return 1;
}

var sourceSelection = sourceCreation.Selection!;
var source = sourceSelection.Source;
var livePaced = source.Mode.IsInteractive();

await using var engine = EngineHost.Create(pipeName, config, ocr, source, sink, verbose, sourceSelection);

// Same fail-with-a-message contract as the OCR pack check above: a pipe name collision
// (second instance already bound, or an invalid name) is user error, not a bug.
try
{
    await engine.StartAsync();
}
catch (Exception ex)
{
    StartupDiagnostics.Report($"Failed to start on pipe '{pipeName}': {ex.Message}", ex);
    return 1;
}

sink.WriteLine($"Pipe:      {pipeName}");
// The token itself is never written here (or anywhere) — the port is the only thing a developer
// needs to curl the control API by hand; the token stays in memory only.
if (engine.ControlApiPort is { } controlApiPort)
    sink.WriteLine($"Control:   http://127.0.0.1:{controlApiPort}/ (token required for /api/*)");
sink.WriteLine(sourceSelection.Description);
var otherOcrPacks = OcrPipeline.AvailableLanguageTags.Where(t => t != ocr.LanguageTag).ToArray();
sink.WriteLine($"OCR:       {ocr.Language}{(otherOcrPacks.Length > 0
    ? $" — also installed: {string.Join(", ", otherOcrPacks)}"
    : "")}");
sink.WriteLine($"Dumps:     {config.OutputDir}");
sink.WriteLine($"SaveFrames: {(saveFrames ? "on" : "off")}");
sink.WriteLine($"Verbose:   {(verbose ? "on" : "off")}");

EngineDesktopLifetime desktop;
try
{
    desktop = EngineDesktopLifetime.Create(
        engine, config, configPath, args, sourceSelection, saveFrames, sink, singleInstance);
}
catch (Exception ex)
{
    StartupDiagnostics.Report("Failed to initialize engine desktop services.", ex);
    await engine.StopAsync();
    return 1;
}

using (desktop)
{
    sink.WriteLine();
    sink.WriteLine(livePaced
        ? "Scanning. Ctrl+C to quit."
        : "Waiting for a plugin to subscribe before replaying. Ctrl+C to quit.");
    sink.WriteLine();

    try
    {
        desktop.Start();
        await engine.RunScanAsync(desktop.CancellationToken);
    }
    finally
    {
        desktop.Stop();
    }
}

await engine.StopAsync();

sink.WriteLine();
sink.WriteLine($"Engine stopped after {engine.Status.Snapshot().FrameSeq} frame(s).");

desktop.RestartIfRequested();

return 0;
