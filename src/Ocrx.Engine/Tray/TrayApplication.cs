using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Ocrx.Engine.Metrics;
using Ocrx.Engine.Plugins;
using Ocrx.Engine.Shell;

namespace Ocrx.Engine.Tray;

/// <summary>
/// Hosts the engine's desktop UI thread: a real <see cref="MainWindow"/> (TASK-UI-04's primary
/// interactive surface) plus, when <c>trayEnabled</c>, a Windows tray <see cref="NotifyIcon"/> on the
/// same STA thread and message loop. A UI timer polls <see cref="EngineStatus"/> and the latest
/// metrics sample, composes a <see cref="TrayView"/>, and repaints the icon and tooltip — but only
/// while the icon exists; with no icon there is nothing for that to update. The right-click context
/// menu is <b>Show Ocrx</b> (default, brings the window forward), the per-installed-plugin
/// launch/stop entries, a separator, and <b>Exit</b>.
/// </summary>
/// <remarks>
/// UI/threading edge, excluded from the coverage gate. The decisions it makes about <em>what</em> to
/// show live in <see cref="TrayViewBuilder"/> / <see cref="FrameRateTracker"/>, which are tested; this
/// class is the wiring that cannot run without a desktop. <see cref="Shell.MainWindow"/>,
/// <see cref="Shell.WindowChrome"/> and <see cref="Shell.SingleInstance"/> carry the rest of TASK-UI-04's
/// new surface — the latter two factor out cleanly enough to unit test on their own.
/// </remarks>
public sealed class TrayApplication : IDisposable
{
    private readonly ConsoleSink _sink;
    private readonly EngineStatus _status;
    private readonly bool _metricsEnabled;
    private readonly TimeSpan _pollInterval;
    private readonly int _controlApiPort;
    private readonly string _controlApiToken;
    private readonly EngineTheme _theme;
    private readonly bool _trayIconEnabled;
    private readonly bool _closeToTrayNoticeAlreadyShown;
    private readonly Action _markCloseToTrayNoticeShown;
    private readonly TrayControls? _controls;
    private readonly SingleInstance? _singleInstance;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly FrameRateTracker _fps = new();

    /// <summary>
    /// Whether the UI thread is actually up and running. False until <see cref="Start"/> returns, and
    /// stays false if setup threw (no interactive desktop — Session 0, some RDP configs) instead of
    /// reaching the message loop. Callers that only want to strip other UI (e.g. hide the console) once
    /// this is confirmed as its replacement should gate on this rather than assume <see cref="Start"/>
    /// succeeding.
    /// </summary>
    public bool IsActive { get; private set; }

    private Thread? _thread;
    private NotifyIcon? _icon;
    private MainWindow? _mainWindow;
    private ContextMenuStrip? _menu;
    private System.Windows.Forms.Timer? _timer;
    private TrayIconFactory? _icons;
    private ToolStripItem? _showAnchor;
    private long _lastPollTimestamp;

    // The launch/stop entries currently spliced in below "Show Ocrx", tracked so they can be
    // removed before each rebuild without disturbing the fixed items around them.
    private readonly List<ToolStripItem> _pluginItems = [];

    // Written from the metrics timer thread, read on the UI thread. A reference assignment is atomic
    // and the tray only ever wants the most recent sample, so no lock is needed.
    private volatile MetricsSnapshot? _latestMetrics;

    public TrayApplication(
        ConsoleSink sink,
        EngineStatus status,
        bool metricsEnabled,
        TimeSpan pollInterval,
        int controlApiPort,
        string controlApiToken,
        EngineTheme theme,
        bool trayIconEnabled,
        bool closeToTrayNoticeAlreadyShown,
        Action markCloseToTrayNoticeShown,
        TrayControls? controls = null,
        SingleInstance? singleInstance = null)
    {
        _sink = sink;
        _status = status;
        _metricsEnabled = metricsEnabled;
        _pollInterval = pollInterval;
        _controlApiPort = controlApiPort;
        _controlApiToken = controlApiToken;
        _theme = theme;
        _trayIconEnabled = trayIconEnabled;
        _closeToTrayNoticeAlreadyShown = closeToTrayNoticeAlreadyShown;
        _markCloseToTrayNoticeShown = markCloseToTrayNoticeShown;
        _controls = controls;
        _singleInstance = singleInstance;
    }

    /// <summary>Starts the UI thread and blocks until the window is live, so the caller can wire metrics.</summary>
    public void Start()
    {
        _thread = new Thread(RunUiLoop) { IsBackground = true, Name = "OCRX UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Latest process-health sample from <see cref="MetricsReporter"/>. Called off the UI thread.</summary>
    public void OnMetrics(MetricsSnapshot snapshot) => _latestMetrics = snapshot;

    /// <summary>Marks a real exit as already underway (tray Exit, or <c>POST /api/exit</c> via
    /// <see cref="TrayControls.OnExit"/>) so the main window's own close handler does not cancel it
    /// back into a hidden window. Safe to call from any thread; a no-op before the window exists.</summary>
    public void PrepareForExit() => _mainWindow?.PrepareForExit();

    /// <summary>Forwards a live theme change to the main window (TASK-UI-05 section 6). Safe to call
    /// from any thread; a no-op before the window exists.</summary>
    public void ApplyThemeSetting(EngineTheme theme) => _mainWindow?.ApplyThemeSetting(theme);

    /// <summary>Forwards a folder-picker request to the main window (TASK-UI-05 section 5). Safe to
    /// call from any thread; resolves to <c>null</c> before the window exists.</summary>
    public Task<string?> BrowseForFolderAsync(string? initialDirectory)
        => _mainWindow?.BrowseForFolderAsync(initialDirectory) ?? Task.FromResult<string?>(null);

    private void RunUiLoop()
    {
        try
        {
            // Stable in this TFM (no WFO5001 suppression needed): themes the WinForms surfaces that
            // are left once the client area is a self-theming WebView2 — chiefly the fallback error
            // label shown when WebView2 itself fails to initialize. Called before EnableVisualStyles,
            // matching documented WinForms dark-mode guidance for the two together.
            Application.SetColorMode(_theme switch
            {
                EngineTheme.Dark => SystemColorMode.Dark,
                EngineTheme.Light => SystemColorMode.Classic,
                _ => SystemColorMode.System,
            });
            Application.EnableVisualStyles();

            _mainWindow = new MainWindow(
                _controlApiPort,
                _controlApiToken,
                _theme,
                closeToTrayEnabled: _trayIconEnabled,
                closeToTrayNoticeAlreadyShown: _closeToTrayNoticeAlreadyShown,
                onExitRequested: () => _controls?.OnExit(),
                onFirstHideToTray: NotifyCloseToTrayFirstTime);

            if (_singleInstance is not null)
                _singleInstance.Signaled += OnSingleInstanceSignaled;

            if (_trayIconEnabled)
            {
                _icons = new TrayIconFactory();

                _menu = new ContextMenuStrip();
                BuildMenu(_menu);
                // The installed plugin set changes while the engine runs, but the menu is built once —
                // so the launch/stop entries are rebuilt each time the menu opens rather than pinned here.
                _menu.Opening += (_, _) => RebuildPluginItems();

                _icon = new NotifyIcon
                {
                    Visible = true,
                    Icon = _icons.For(TrayIconState.Idle),
                    Text = "OCRX engine",
                    ContextMenuStrip = _menu,
                };
                _icon.DoubleClick += (_, _) => _mainWindow.ShowAndActivate();

                _lastPollTimestamp = Stopwatch.GetTimestamp();
                Refresh();

                _timer = new System.Windows.Forms.Timer { Interval = (int)_pollInterval.TotalMilliseconds };
                _timer.Tick += (_, _) => Refresh();
                _timer.Start();
            }

            IsActive = true;
            _ready.Set();
            Application.Run(_mainWindow);
        }
        catch (Exception ex)
        {
            // No interactive desktop (Windows service, Session 0, some RDP configs): disable the tray
            // UI, never take the capture engine down with an unhandled exception on this STA thread.
            _sink.WriteLine($"[tray] disabled: {ex.Message}");
        }
        finally
        {
            _ready.Set(); // idempotent; guarantees Start() unblocks even if init threw before the Set above
            if (_singleInstance is not null)
                _singleInstance.Signaled -= OnSingleInstanceSignaled;
            _timer?.Dispose();
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            _menu?.Dispose();
            _mainWindow?.Dispose();
            _icons?.Dispose();
        }
    }

    // Runs on a thread-pool thread (see SingleInstance.Signaled); BeginInvoke marshals the actual
    // show/restore/activate onto the UI thread.
    private void OnSingleInstanceSignaled()
    {
        var window = _mainWindow;
        if (window is { IsDisposed: false })
        {
            try
            {
                window.BeginInvoke((Action)window.ShowAndActivate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The UI thread's handle is already gone; nothing left to show.
            }
        }
    }

    // Called from MainWindow when it hides to tray for the first time this process — itself gated to
    // fire only once per process by MainWindow's own latch, seeded from the persisted flag. Shows the
    // one-time balloon (only reachable when the icon exists, since that is the only path that can
    // hide-to-tray in the first place) and asks the host to persist the flag so it never fires again.
    private void NotifyCloseToTrayFirstTime()
    {
        _icon?.ShowBalloonTip(
            5000,
            "OCRX",
            "OCRX is still capturing. Right-click the tray icon to exit.",
            ToolTipIcon.Info);
        _markCloseToTrayNoticeShown();
    }

    // Builds the fixed part of the menu: "Show Ocrx" (bold, the default action) and, further
    // down, Exit. The per-plugin entries are spliced in between by RebuildPluginItems, anchored on
    // the "Show Ocrx" item.
    private void BuildMenu(ContextMenuStrip menu)
    {
        var show = new ToolStripMenuItem("Show OCRX", null, (_, _) => _mainWindow!.ShowAndActivate())
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        };
        menu.Items.Add(show);
        _showAnchor = show;

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _controls?.OnExit());
    }

    // Replaces the per-plugin launch/stop entries that sit directly under "Show Ocrx". They are
    // top-level rather than a submenu because starting a plugin is the action a user repeats.
    private void RebuildPluginItems()
    {
        if (_controls?.Plugins is not { } plugins || _menu is null || _showAnchor is null)
            return;

        foreach (var item in _pluginItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _pluginItems.Clear();

        var running = plugins.Launcher.RunningIds;
        var index = _menu.Items.IndexOf(_showAnchor) + 1;
        foreach (var installed in plugins.Installer.State.Entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isRunning = running.Contains(installed.Id);
            var entry = installed;
            var item = new ToolStripMenuItem(isRunning ? $"Stop {entry.Name}" : $"Launch {entry.Name}");
            item.Click += (_, _) => TogglePlugin(plugins, entry, isRunning);

            _menu.Items.Insert(index++, item);
            _pluginItems.Add(item);
        }
    }

    private void TogglePlugin(PluginServices plugins, InstalledPlugin plugin, bool isRunning)
    {
        try
        {
            if (isRunning)
                plugins.Launcher.Stop(plugin.Id);
            else
                plugins.Launcher.Start(plugin);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not {(isRunning ? "stop" : "start")} {plugin.Name}:\n{ex.Message}",
                "OCRX",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Refresh()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var snapshot = _status.Snapshot();
            _fps.Observe(snapshot.FrameSeq, Stopwatch.GetElapsedTime(_lastPollTimestamp, now));
            _lastPollTimestamp = now;

            var view = TrayViewBuilder.Build(snapshot, _latestMetrics, _fps.Fps, _metricsEnabled);
            _icon!.Icon = _icons!.For(view.IconState);
            _icon.Text = view.Tooltip;
        }
        catch (Exception ex)
        {
            // Runs on the UI thread; stop the poll timer so the tray freezes rather than letting an
            // unhandled exception tear down the message loop — and the whole engine — with it.
            _sink.WriteLine($"[tray] display stopped: {ex.Message}");
            _timer?.Stop();
        }
    }

    public void Dispose()
    {
        // Marshal the exit onto the UI thread; ExitThread ends Application.Run and lets RunUiLoop
        // dispose the window/icon so neither lingers after shutdown. MainWindow is created eagerly
        // and always has a handle by the time Dispose can run, so — unlike the StatusForm this class
        // used to force a handle onto for exactly this reason — BeginInvoke always has a valid target.
        var window = _mainWindow;
        if (window is { IsDisposed: false })
        {
            try
            {
                window.BeginInvoke((Action)(() => Application.ExitThread()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The UI thread's message loop / handle is already gone; nothing left to unwind.
            }
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
