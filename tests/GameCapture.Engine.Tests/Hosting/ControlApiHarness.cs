using System.Net.WebSockets;
using System.Text.Json;
using GameCapture.Engine.Plugins;
using GameCapture.Engine.Tray;

namespace GameCapture.Engine.Tests.Hosting;

/// <summary>
/// Spins up a real, interactive <see cref="EngineHost"/> — real Kestrel, real loopback listener,
/// real <see cref="ControlApi"/> routes — the same way <see cref="EngineHost.Create"/> is shared
/// between the engine and <c>GrpcHostTests</c>, so a control-API test cannot pass against wiring the
/// real engine does not use.
/// </summary>
/// <remarks>
/// <see cref="EngineDesktopLifetime.SaveSettings"/> is exercised for real — through a lifetime built
/// with a non-interactive <see cref="FrameSourceSelection"/> so <c>InitializeHotkey</c> never installs
/// a system-wide keyboard hook — rather than a stub, so the settings tests below prove the endpoint
/// actually routes through the tray's validation guards instead of reimplementing them. The tray
/// itself (<see cref="TrayApplication"/>) is never started: nothing in this class calls
/// <see cref="EngineDesktopLifetime.Start"/>, so no tray icon appears during the test run.
/// </remarks>
internal sealed class ControlApiHarness : IAsyncDisposable
{
    private readonly EngineHost _engine;
    private readonly EngineDesktopLifetime _lifetime;
    private readonly IFrameSource _dummySource;
    private readonly PluginInstaller _installer;
    private readonly PluginLauncher _launcher;
    private readonly TrayControls _controls;
    private readonly ConsoleSink _sink;
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly Func<int> _readExitCount;
    private readonly List<HttpClient> _clients = [];
    private bool _disposed;

    private ControlApiHarness(
        EngineHost engine, EngineDesktopLifetime lifetime, IFrameSource dummySource,
        PluginInstaller installer, PluginLauncher launcher, TrayControls controls,
        ConsoleSink sink, string tempDir, string configPath, Func<int> readExitCount)
    {
        _engine = engine;
        _lifetime = lifetime;
        _dummySource = dummySource;
        _installer = installer;
        _launcher = launcher;
        _controls = controls;
        _sink = sink;
        _tempDir = tempDir;
        _configPath = configPath;
        _readExitCount = readExitCount;
    }

    /// <summary>The settings baseline every <c>POST /api/settings</c> test patches from — matches what
    /// <see cref="EngineDesktopLifetime.SaveSettings"/> diffs against.</summary>
    public EngineSettings CurrentSettings => _controls.Settings;
    public int ExitCount => _readExitCount();
    public PluginInstaller Installer => _installer;
    public PluginLauncher Launcher => _launcher;

    public int Port => _engine.ControlApiPort!.Value;

    /// <summary>Fresh <see cref="HttpClient"/> with no <c>Authorization</c> header, base address set to
    /// this instance's loopback port. Disposed with the harness.</summary>
    public HttpClient UnauthenticatedClient() => NewClient();

    /// <summary>Fresh client with a valid <c>Bearer</c> header.</summary>
    public HttpClient AuthorizedClient()
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_engine.ControlApiToken!.Value}");
        return client;
    }

    /// <summary>Fresh client with whatever raw <c>Authorization</c> header value is given — used for the
    /// wrong-token and malformed-header cases.</summary>
    public HttpClient ClientWithAuthorizationHeader(string value)
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Add("Authorization", value);
        return client;
    }

    private HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/") };
        _clients.Add(client);
        return client;
    }

    public Task<ClientWebSocket> ConnectEventsAsync()
        => ConnectEventsAsync($"Bearer {_engine.ControlApiToken!.Value}");

    /// <summary>Attempts the <c>/api/events</c> handshake with the given raw <c>Authorization</c>
    /// header value, or none at all when <paramref name="authorizationHeader"/> is null. Used by both
    /// the happy path (a valid bearer token) and the rejection tests, which expect
    /// <see cref="WebSocketException"/> out of <c>ConnectAsync</c> itself — the same auth gate that
    /// protects every other <c>/api/*</c> route must also cover the upgrade.</summary>
    public async Task<ClientWebSocket> ConnectEventsAsync(string? authorizationHeader)
    {
        var socket = new ClientWebSocket();
        if (authorizationHeader is not null)
            socket.Options.SetRequestHeader("Authorization", authorizationHeader);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/api/events"), timeout.Token);
        return socket;
    }

    public void SetFrame(uint width, uint height, ulong sequence)
        => _engine.Status.OnFrame(width, height, sequence);

    public void BlockConfigWrites()
    {
        File.Delete(_configPath);
        Directory.CreateDirectory(_configPath);
    }

    public static async Task<JsonElement> ReceiveEventAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var message = new MemoryStream();
        var buffer = new byte[4096];

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The event socket closed before sending a message.");
            await message.WriteAsync(buffer.AsMemory(0, result.Count), timeout.Token);
        }
        while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(message.ToArray());
        return document.RootElement.Clone();
    }

    public static Task<ControlApiHarness> StartAsync()
        => StartAsync(null);

    public static async Task<ControlApiHarness> StartAsync(HttpMessageHandler? catalogHandler)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gc-controlapi-tests", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(tempDir, "engine-config.json");
        var config = EngineConfig.Load(configPath); // creates tempDir and a default file
        config.MetricsIntervalMs = 250;

        var pipeName = $"sc-controlapi-{Guid.NewGuid():N}";
        var sink = new ConsoleSink();

        // LiveCapture: the one thing EngineHost.Create needs to enable the control API. The source is
        // never read — nothing here calls RunScanAsync.
        var liveSource = new GatedFrameSource(EngineTestFixtures.ReplayDir, isReplay: false);
        var sourceSelection = new FrameSourceSelection(liveSource, "test", ["Monitor 1", "Monitor 2"], CurrentMonitorIndex: 0);

        var engine = EngineHost.Create(pipeName, config, new OcrPipeline(), liveSource, sink, verbose: false, sourceSelection);
        await engine.StartAsync();

        // Deliberately non-interactive, so EngineDesktopLifetime.Create's InitializeHotkey does not
        // install a real keyboard hook — SaveSettings itself never looks at source interactivity.
        var dummySource = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var dummySelection = new FrameSourceSelection(dummySource, "dummy", [], CurrentMonitorIndex: 0);
        var lifetime = EngineDesktopLifetime.Create(engine, config, configPath, [], dummySelection, saveFrames: false, sink);

        var currentSettings = new EngineSettings(
            config.OutputDir, config.OcrLanguage, config.ScanIntervalMs, config.Hotkey, config.PipeName,
            config.MetricsEnabled, config.MetricsIntervalMs, config.TrayEnabled,
            sourceSelection.CurrentMonitorIndex, config.Theme);
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
                var result = lifetime.SaveSettings(currentSettings, update(currentSettings));
                currentSettings = result.Settings;
                return result;
            }
        }

        var pluginRoot = Path.Combine(tempDir, "plugins");
        var installer = new PluginInstaller(pluginRoot, catalogHandler ?? new EmptyCatalogHandler());
        var launcher = new PluginLauncher();
        var exitCount = 0;

        var controls = new TrayControls(
            sourceSelection.MonitorLabels,
            sourceSelection.CurrentMonitorIndex,
            ReadSettings,
            OcrPipeline.AvailableLanguageTags,
            OnSelectMonitor: _ => { },
            // The real validation, exactly as the tray calls it — not a test double.
            OnUpdateSettings: UpdateSettings,
            OnExit: () => Interlocked.Increment(ref exitCount),
            Plugins: new PluginServices(installer, launcher, PluginManagerSettings.Load(PluginPaths.SettingsFile(pluginRoot))));

        engine.ControlApi!.SetControls(controls);

        return new ControlApiHarness(
            engine, lifetime, dummySource, installer, launcher, controls,
            sink, tempDir, configPath, () => exitCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var client in _clients)
            client.Dispose();

        _lifetime.Dispose();
        _dummySource.Dispose();
        _launcher.Dispose();
        _installer.Dispose();

        await _engine.DisposeAsync();
        _sink.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp folder costs disk space, not correctness.
        }
    }
}
