using System.Diagnostics;
using System.Windows.Forms;
using GameCapture.Engine.Metrics;
using GameCapture.Engine.Plugins;
using GameCapture.Engine.Tray;

namespace GameCapture.Engine;

/// <summary>
/// Owns the process's desktop-facing lifetime: Ctrl+C and global-hotkey cancellation, optional
/// frame dumping, metrics and tray UI, settings-triggered restart signaling, and their disposal.
/// The engine/web-host lifetime remains owned by <see cref="EngineHost"/>.
/// </summary>
internal sealed class EngineDesktopLifetime : IDisposable
{
    private readonly EngineHost _engine;
    private readonly EngineConfig _config;
    private readonly string _configPath;
    private readonly string[] _args;
    private readonly FrameSourceSelection _sourceSelection;
    private readonly bool _saveFrames;
    private readonly ConsoleSink _sink;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConsoleCancelEventHandler _cancelHandler;

    private FrameDumpService? _frameDumper;
    private HotkeyListener? _hotkey;
    private MetricsReporter? _metrics;
    private TrayApplication? _tray;
    private PluginInstaller? _pluginInstaller;
    private PluginLauncher? _pluginLauncher;
    private bool _restartRequested;
    private bool _stopped;
    private bool _disposed;

    private EngineDesktopLifetime(
        EngineHost engine,
        EngineConfig config,
        string configPath,
        string[] args,
        FrameSourceSelection sourceSelection,
        bool saveFrames,
        ConsoleSink sink)
    {
        _engine = engine;
        _config = config;
        _configPath = configPath;
        _args = args;
        _sourceSelection = sourceSelection;
        _saveFrames = saveFrames;
        _sink = sink;

        _cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _shutdown.Cancel();
        };
        Console.CancelKeyPress += _cancelHandler;
    }

    public CancellationToken CancellationToken => _shutdown.Token;

    public static EngineDesktopLifetime Create(
        EngineHost engine,
        EngineConfig config,
        string configPath,
        string[] args,
        FrameSourceSelection sourceSelection,
        bool saveFrames,
        ConsoleSink sink)
    {
        var lifetime = new EngineDesktopLifetime(
            engine, config, configPath, args, sourceSelection, saveFrames, sink);

        try
        {
            lifetime.InitializeHotkey();
            return lifetime;
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    /// <summary>Starts metrics and tray services after the console startup banner is complete.</summary>
    public void Start()
    {
        if (!_sourceSelection.Source.Mode.IsInteractive())
            return;

        // Created after the banner so it disposes before the sink: the timer is fully stopped
        // (in-flight tick drained) before the sink erases the status line on shutdown.
        if (_config.MetricsEnabled)
            _metrics = new MetricsReporter(_sink, TimeSpan.FromMilliseconds(_config.MetricsIntervalMs));

        if (!_config.TrayEnabled)
            return;

        var currentSettings = new EngineSettings(
            _config.OutputDir,
            _config.OcrLanguage,
            Math.Clamp(_config.ScanIntervalMs, 100, 60_000),
            _config.Hotkey,
            _config.PipeName,
            _config.MetricsEnabled,
            Math.Clamp(_config.MetricsIntervalMs, 250, 60_000),
            _config.TrayEnabled);

        // Plugin management is scoped to the tray: it is the engine's only interactive surface, and a
        // headless run has nobody to click Install. Neither service touches engine-config.json, so a
        // plugin install never takes the restart path the settings callbacks below do.
        _pluginInstaller = new PluginInstaller(PluginPaths.DefaultRoot());
        _pluginLauncher = new PluginLauncher();

        var controls = new TrayControls(
            _sourceSelection.MonitorLabels,
            _sourceSelection.CurrentMonitorIndex,
            currentSettings,
            OcrPipeline.AvailableLanguageTags,
            OnSelectMonitor: index =>
                PersistAndRestart(new Dictionary<string, object> { ["monitorIndex"] = index }),
            OnSaveSettings: settings => SaveSettings(currentSettings, settings),
            OnExit: _shutdown.Cancel,
            Plugins: new PluginServices(_pluginInstaller, _pluginLauncher));

        _tray = new TrayApplication(
            _sink,
            _engine.Status,
            _config.MetricsEnabled,
            TimeSpan.FromMilliseconds(Math.Max(250, _config.MetricsIntervalMs)),
            controls);
        _tray.Start();

        // Feed the same sample stream the console status bar uses; the tray never ticks its own
        // sampler (MetricsSampler is stateful and single-threaded by contract).
        if (_metrics is not null)
            _metrics.Sampled += _tray.OnMetrics;

    }

    /// <summary>Relaunches after the engine has stopped and released its named pipe, if requested.</summary>
    public void RestartIfRequested()
    {
        if (!Volatile.Read(ref _restartRequested))
            return;

        // Only self-relaunch when running as the engine apphost. Under `dotnet run`, ProcessPath is
        // the shared dotnet muxer and cannot restart the application from the engine's arguments.
        if (EngineRelaunch.IsSelfRelaunchable(Environment.ProcessPath))
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = false,
                };
                foreach (var argument in EngineRelaunch.StripPersistedOverrides(_args))
                    startInfo.ArgumentList.Add(argument);

                _sink.WriteLine("Restarting to apply settings…");
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _sink.WriteLine($"Automatic restart failed ({ex.Message}); the change is saved — restart manually to apply it.");
            }
        }
        else
        {
            _sink.WriteLine("Automatic restart is unavailable (running under 'dotnet run' or an unknown host); "
                + "the change is saved — restart manually to apply it.");
        }
    }

    /// <summary>Stops desktop services before the engine begins its client-drain period.</summary>
    public void Stop()
    {
        if (_stopped)
            return;

        _tray?.Dispose();    // remove the icon before the console summary prints
        // After the tray, so no menu entry can start a plugin the engine is about to stop tracking.
        // A settings change restarts the engine through this same path: plugins launched from the
        // tray are stopped with it and are not brought back by the relaunched process.
        _pluginLauncher?.Dispose();
        _pluginInstaller?.Dispose();
        _metrics?.Dispose(); // stop status updates before the console summary prints
        _hotkey?.Dispose();
        _stopped = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        Console.CancelKeyPress -= _cancelHandler;
        _shutdown.Dispose();
        _disposed = true;
    }

    private void InitializeHotkey()
    {
        if (!_sourceSelection.Source.Mode.IsInteractive())
            return;

        var (modifiers, virtualKey) = HotkeyListener.ParseHotkey(_config.Hotkey);
        Action onHotkey;
        if (_saveFrames)
        {
            _frameDumper = new FrameDumpService(_config.OutputDir, _sink);
            onHotkey = () => _engine.ScanLoop.TriggerManual(_frameDumper.DumpFrameAsync);
        }
        else
        {
            onHotkey = _engine.ScanLoop.TriggerManual;
        }

        _hotkey = new HotkeyListener(modifiers, virtualKey, onHotkey);
        _sink.WriteLine($"Hotkey:    {_config.Hotkey} (manual trigger{(_saveFrames ? " + save frame" : "")})");
        _sink.WriteLine($"Metrics:   {(_config.MetricsEnabled ? $"live status bar every {_config.MetricsIntervalMs} ms" : "disabled")}");
        _sink.WriteLine($"Tray:      {(_config.TrayEnabled ? "on" : "off")}");
    }

    private void SaveSettings(EngineSettings currentSettings, EngineSettings settings)
    {
        // An unavailable OCR pack would make the relaunched process exit before the tray exists;
        // retain the existing fallback to automatic language selection instead.
        var language = settings.OcrLanguage;
        if (language.Length > 0
            && !OcrPipeline.AvailableLanguageTags.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            language = "";
        }

        // Patch only changed fields. Reserializing the loaded config would bake a relative outputDir
        // into the absolute path resolved in memory.
        var changes = new Dictionary<string, object>();
        if (settings.OutputDir != currentSettings.OutputDir)
            changes["outputDir"] = settings.OutputDir;
        if (language != currentSettings.OcrLanguage)
            changes["ocrLanguage"] = language;
        if (settings.ScanIntervalMs != currentSettings.ScanIntervalMs)
            changes["scanIntervalMs"] = settings.ScanIntervalMs;
        if (settings.Hotkey != currentSettings.Hotkey)
            changes["hotkey"] = settings.Hotkey;
        if (settings.PipeName != currentSettings.PipeName)
            changes["pipeName"] = settings.PipeName;
        if (settings.MetricsEnabled != currentSettings.MetricsEnabled)
            changes["metricsEnabled"] = settings.MetricsEnabled;
        if (settings.MetricsIntervalMs != currentSettings.MetricsIntervalMs)
            changes["metricsIntervalMs"] = settings.MetricsIntervalMs;
        if (settings.TrayEnabled != currentSettings.TrayEnabled)
            changes["trayEnabled"] = settings.TrayEnabled;

        if (changes.Count > 0)
            PersistAndRestart(changes);
    }

    private void PersistAndRestart(IReadOnlyDictionary<string, object> changes)
    {
        try
        {
            File.WriteAllText(_configPath, ConfigPatch.Apply(File.ReadAllText(_configPath), changes));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings:\n{ex.Message}",
                "GameCapture",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Volatile.Write(ref _restartRequested, true);
        _shutdown.Cancel();
    }
}
