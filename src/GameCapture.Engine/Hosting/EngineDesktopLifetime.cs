using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
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
        {
            _metrics = new MetricsReporter(_sink, TimeSpan.FromMilliseconds(_config.MetricsIntervalMs));
            // The control API's /api/status shows the same numbers the tray does; both are fed from
            // this one sampler because MetricsSampler is stateful and only one timer may tick it.
            if (_engine.ControlApi is { } controlApi)
                _metrics.Sampled += controlApi.SetMetrics;
        }

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
            _config.TrayEnabled,
            _sourceSelection.CurrentMonitorIndex,
            _config.Theme);
        var settingsGate = new Lock();

        EngineSettings ReadSettings()
        {
            lock (settingsGate)
                return currentSettings;
        }

        SettingsSaveResult UpdateSettings(Func<EngineSettings, EngineSettings> update)
        {
            lock (settingsGate)
            {
                var result = SaveSettings(currentSettings, update(currentSettings));
                currentSettings = result.Settings;
                return result;
            }
        }

        // Plugin management is scoped to the tray: it is the engine's only interactive surface, and a
        // headless run has nobody to click Install. None of installer, launcher, or manager settings
        // touch engine-config.json, so a plugin install never takes the restart path the settings
        // callbacks below do.
        var pluginRoot = PluginPaths.DefaultRoot();
        _pluginInstaller = new PluginInstaller(pluginRoot);
        _pluginLauncher = new PluginLauncher();

        var controls = new TrayControls(
            _sourceSelection.MonitorLabels,
            _sourceSelection.CurrentMonitorIndex,
            ReadSettings,
            OcrPipeline.AvailableLanguageTags,
            OnSelectMonitor: index =>
                PersistAndRestart(new Dictionary<string, object> { ["monitorIndex"] = index }),
            OnUpdateSettings: UpdateSettings,
            OnExit: _shutdown.Cancel,
            Plugins: new PluginServices(
                _pluginInstaller,
                _pluginLauncher,
                PluginManagerSettings.Load(PluginPaths.SettingsFile(pluginRoot))));

        // The control API drives the same callbacks and plugin services the tray does, so the two
        // can never disagree about what an action did.
        _engine.ControlApi?.SetControls(controls);

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

    /// <summary>
    /// Validates and persists a settings change, falling back to a safe value for anything that would
    /// make the relaunched process exit before any UI exists. Shared by the tray's Settings dialog and
    /// the control API's <c>POST /api/settings</c> — both are doors into the same room, so both go
    /// through the same lock rather than each keeping their own copy of these guards.
    /// </summary>
    internal SettingsSaveResult SaveSettings(EngineSettings currentSettings, EngineSettings settings)
    {
        // An unavailable OCR pack would make the relaunched process exit before the tray exists;
        // retain the existing fallback to automatic language selection instead.
        var language = settings.OcrLanguage;
        if (language.Length > 0
            && !OcrPipeline.AvailableLanguageTags.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            language = "";
        }

        // An unparseable hotkey would make the relaunched process exit before the tray exists (see
        // InitializeHotkey); retain the previous, already-valid hotkey instead of persisting garbage.
        var hotkey = settings.Hotkey;
        try
        {
            HotkeyListener.ParseHotkey(hotkey);
        }
        catch (FormatException)
        {
            hotkey = currentSettings.Hotkey;
        }

        // Same failure mode as the hotkey: an unusable pipe name would make EngineHost.StartAsync
        // throw before the tray exists (see Program.cs), leaving no UI path back in. Only probe when
        // it actually changed — the current name is already bound by this running engine, so testing
        // it here would always collide with our own listener.
        var pipeName = settings.PipeName;
        if (pipeName != currentSettings.PipeName && !IsUsablePipeName(pipeName))
            pipeName = currentSettings.PipeName;

        // Patch only changed fields. Reserializing the loaded config would bake a relative outputDir
        // into the absolute path resolved in memory.
        var changes = new Dictionary<string, object>();
        if (settings.OutputDir != currentSettings.OutputDir)
            changes["outputDir"] = settings.OutputDir;
        if (language != currentSettings.OcrLanguage)
            changes["ocrLanguage"] = language;
        if (settings.ScanIntervalMs != currentSettings.ScanIntervalMs)
            changes["scanIntervalMs"] = settings.ScanIntervalMs;
        if (hotkey != currentSettings.Hotkey)
            changes["hotkey"] = hotkey;
        if (pipeName != currentSettings.PipeName)
            changes["pipeName"] = pipeName;
        if (settings.MetricsEnabled != currentSettings.MetricsEnabled)
            changes["metricsEnabled"] = settings.MetricsEnabled;
        if (settings.MetricsIntervalMs != currentSettings.MetricsIntervalMs)
            changes["metricsIntervalMs"] = settings.MetricsIntervalMs;
        if (settings.TrayEnabled != currentSettings.TrayEnabled)
            changes["trayEnabled"] = settings.TrayEnabled;
        // Neither field can make the relaunched process fail to start (unlike OCR language, hotkey
        // and pipe name above), so no fallback validation is needed — just patch them through.
        if (settings.MonitorIndex != currentSettings.MonitorIndex)
            changes["monitorIndex"] = settings.MonitorIndex;
        if (settings.Theme != currentSettings.Theme)
            changes["theme"] = settings.Theme.ToString().ToLowerInvariant();

        if (changes.Count == 0)
            return new SettingsSaveResult(currentSettings, RestartPending: false);

        // What was actually corrected above, so a caller (the control API) can show the value that
        // won rather than the one the client sent.
        var persisted = currentSettings with
        {
            OutputDir = settings.OutputDir,
            OcrLanguage = language,
            ScanIntervalMs = settings.ScanIntervalMs,
            Hotkey = hotkey,
            PipeName = pipeName,
            MetricsEnabled = settings.MetricsEnabled,
            MetricsIntervalMs = settings.MetricsIntervalMs,
            TrayEnabled = settings.TrayEnabled,
            MonitorIndex = settings.MonitorIndex,
            Theme = settings.Theme,
        };

        var restartRequired = SettingsRestartDecision.IsRestartRequired(changes);
        var persistError = Persist(changes);
        var persistedOk = persistError is null;
        if (persistedOk && restartRequired)
        {
            Volatile.Write(ref _restartRequested, true);
            _shutdown.Cancel();
        }

        // A failed write (see Persist) leaves the running config exactly as it was; report that
        // rather than a value that was never actually saved.
        return new SettingsSaveResult(
            persistedOk ? persisted : currentSettings,
            persistedOk && restartRequired,
            persistError is null ? null : $"Could not save settings: {persistError}");
    }

    // Probes usability the same way the OS ultimately will: by actually creating and immediately
    // disposing a server instance under that name, rather than reimplementing Windows' named-pipe
    // naming rules. Blank is rejected without probing since it always falls back to a config-file
    // path (Program.cs) rather than throwing from the pipe API.
    private static bool IsUsablePipeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        try
        {
            using var probe = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private void PersistAndRestart(IReadOnlyDictionary<string, object> changes)
    {
        if (Persist(changes) is { } error)
        {
            ShowPersistenceError(error);
            return;
        }

        Volatile.Write(ref _restartRequested, true);
        _shutdown.Cancel();
    }

    // Every field but theme is bound at startup and needs the restart above to take effect; theme
    // does not, so its own save path stops here.
    private string? Persist(IReadOnlyDictionary<string, object> changes)
    {
        try
        {
            File.WriteAllText(_configPath, ConfigPatch.Apply(File.ReadAllText(_configPath), changes));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void ShowPersistenceError(string error)
        => MessageBox.Show(
            $"Could not save settings:\n{error}",
            "GameCapture",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
}
