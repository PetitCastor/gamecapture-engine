using System.Diagnostics;
using System.Windows.Forms;
using GameCapture.Engine.Metrics;
using GameCapture.Engine.Plugins;

namespace GameCapture.Engine.Tray;

/// <summary>
/// Hosts the Windows tray icon inside the engine process. Runs its own STA thread with a WinForms
/// message loop; a UI timer polls <see cref="EngineStatus"/> and the latest metrics sample, composes
/// a <see cref="TrayView"/>, and repaints the icon and tooltip. Left-clicking the icon never pops
/// anything up. The right-click context menu carries a "Status…" popup entry, but only when a Visual
/// Studio debugger is attached to the process — it is absent for a normal player-facing launch. When a
/// <see cref="TrayControls"/> is supplied the menu also offers monitor selection, a settings screen and
/// an exit action (always present, debugger or not); those callbacks are the host's, since applying any
/// of them means persisting config and restarting. When that record also carries
/// <see cref="Plugins.PluginServices"/>, the menu gains the plugin manager and a launch/stop entry per
/// installed plugin — those act immediately, with no restart.
/// </summary>
/// <remarks>
/// UI/threading edge, excluded from the coverage gate. The decisions it makes about <em>what</em> to
/// show live in <see cref="TrayViewBuilder"/> / <see cref="FrameRateTracker"/>, which are tested; this
/// class is the wiring that cannot run without a desktop.
/// </remarks>
public sealed class TrayApplication : IDisposable
{
    private readonly ConsoleSink _sink;
    private readonly EngineStatus _status;
    private readonly bool _metricsEnabled;
    private readonly TimeSpan _pollInterval;
    private readonly TrayControls? _controls;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly FrameRateTracker _fps = new();

    /// <summary>
    /// Whether the tray icon is actually up and running. False until <see cref="Start"/> returns, and
    /// stays false if setup threw (no interactive desktop — Session 0, some RDP configs) instead of
    /// reaching the message loop. Callers that only want to strip other UI (e.g. hide the console) once
    /// the tray is confirmed as its replacement should gate on this rather than assume <see cref="Start"/>
    /// succeeding.
    /// </summary>
    public bool IsActive { get; private set; }

    private Thread? _thread;
    private NotifyIcon? _icon;
    private StatusForm? _form;
    private ContextMenuStrip? _menu;
    private System.Windows.Forms.Timer? _timer;
    private TrayIconFactory? _icons;
    private ToolStripItem? _pluginsAnchor;
    private PluginsForm? _pluginsDialog;
    private long _lastPollTimestamp;

    // The launch/stop entries currently spliced in below "Plugins…", tracked so they can be removed
    // before each rebuild without disturbing the fixed items around them.
    private readonly List<ToolStripItem> _pluginItems = [];

    // Written from the metrics timer thread, read on the UI thread. A reference assignment is atomic
    // and the tray only ever wants the most recent sample, so no lock is needed.
    private volatile MetricsSnapshot? _latestMetrics;

    public TrayApplication(
        ConsoleSink sink,
        EngineStatus status,
        bool metricsEnabled,
        TimeSpan pollInterval,
        TrayControls? controls = null)
    {
        _sink = sink;
        _status = status;
        _metricsEnabled = metricsEnabled;
        _pollInterval = pollInterval;
        _controls = controls;
    }

    /// <summary>Starts the tray thread and blocks until the icon is live, so the caller can wire metrics.</summary>
    public void Start()
    {
        _thread = new Thread(RunUiLoop) { IsBackground = true, Name = "GameCapture tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Latest process-health sample from <see cref="MetricsReporter"/>. Called off the UI thread.</summary>
    public void OnMetrics(MetricsSnapshot snapshot) => _latestMetrics = snapshot;

    private void RunUiLoop()
    {
        try
        {
            Application.EnableVisualStyles();
            _icons = new TrayIconFactory();
            _form = new StatusForm();
            // Force the window handle onto this STA thread now so Dispose()'s BeginInvoke always has a
            // valid target. The handle is otherwise created lazily on the first Show(), and a run where
            // the popup is never opened (no debugger attached, so "Status…" is never in the menu) would
            // leave BeginInvoke to throw — ExitThread would never fire, the join would time out, and the
            // NotifyIcon would linger as a ghost in the tray.
            _ = _form.Handle;

            _menu = new ContextMenuStrip();
            // Debug-only convenience: never shown outside a Visual Studio debug session, and only ever
            // reached from this menu entry — left-clicking the icon does not pop it up.
            if (Debugger.IsAttached)
                _menu.Items.Add("Status…", null, (_, _) => ShowPopup());
            BuildControlMenu(_menu);
            // The installed set changes while the engine runs, but the menu is built once — so the
            // launch/stop entries are rebuilt each time the menu opens rather than pinned here.
            _menu.Opening += (_, _) => RebuildPluginItems();

            _icon = new NotifyIcon
            {
                Visible = true,
                Icon = _icons.For(TrayIconState.Idle),
                Text = "GameCapture engine",
                ContextMenuStrip = _menu,
            };

            _lastPollTimestamp = Stopwatch.GetTimestamp();
            Refresh();

            _timer = new System.Windows.Forms.Timer { Interval = (int)_pollInterval.TotalMilliseconds };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();

            IsActive = true;
            _ready.Set();
            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            // No interactive desktop (Windows service, Session 0, some RDP configs): disable the tray,
            // never take the capture engine down with an unhandled exception on this STA thread.
            _sink.WriteLine($"[tray] disabled: {ex.Message}");
        }
        finally
        {
            _ready.Set(); // idempotent; guarantees Start() unblocks even if init threw before the Set above
            _timer?.Dispose();
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            _menu?.Dispose();
            _form?.Dispose();
            _icons?.Dispose();
        }
    }

    private void ShowPopup()
    {
        Refresh();
        _form!.ShowNear(Cursor.Position);
    }

    // Adds the control actions when the host wired them. Selecting a monitor or saving
    // settings hands off to the host callback, which persists the change and restarts the engine — the
    // captured monitor, OCR pack, output dir and scan cadence are all bound at startup.
    private void BuildControlMenu(ContextMenuStrip menu)
    {
        if (_controls is not { } controls)
            return;

        var monitors = new ToolStripMenuItem("Capture monitor");
        for (var i = 0; i < controls.MonitorLabels.Count; i++)
        {
            var index = i; // capture the loop value, not the variable, for the click handler
            var item = new ToolStripMenuItem(controls.MonitorLabels[i])
            {
                Checked = index == controls.CurrentMonitorIndex,
                CheckOnClick = false,
            };
            item.Click += (_, _) =>
            {
                if (index != controls.CurrentMonitorIndex)
                    controls.OnSelectMonitor(index);
            };
            monitors.DropDownItems.Add(item);
        }
        if (controls.Plugins is not null)
            _pluginsAnchor = menu.Items.Add("Plugins…", null, (_, _) => OpenPlugins(controls.Plugins));

        if (monitors.HasDropDownItems)
            menu.Items.Add(monitors);

        menu.Items.Add("Settings…", null, (_, _) => OpenSettings(controls));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => controls.OnExit());
    }

    // Replaces the per-plugin launch/stop entries that sit directly under "Plugins…". They are
    // top-level rather than a submenu because starting a plugin is the action a user repeats; the
    // dialog behind "Plugins…" is the occasional one.
    private void RebuildPluginItems()
    {
        if (_controls?.Plugins is not { } plugins || _menu is null || _pluginsAnchor is null)
            return;

        foreach (var item in _pluginItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _pluginItems.Clear();

        var running = plugins.Launcher.RunningIds;
        var index = _menu.Items.IndexOf(_pluginsAnchor) + 1;
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
                "GameCapture",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenPlugins(PluginServices plugins)
    {
        using var dialog = new PluginsForm(plugins);
        // Tracked so shutdown can close it. ShowDialog runs a nested message loop, so an engine
        // shutdown while the manager is open would otherwise never reach Application.ExitThread: the
        // join would time out, the host would dispose the installer and launcher under the still-open
        // dialog, and the tray icon would linger until the user closed it by hand.
        _pluginsDialog = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            _pluginsDialog = null;
        }
    }

    private void OpenSettings(TrayControls controls)
    {
        using var dialog = new SettingsForm(controls.Settings, controls.AvailableOcrLanguages);
        if (dialog.ShowDialog() == DialogResult.OK && dialog.Result != controls.Settings)
            controls.OnSaveSettings(dialog.Result);
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
            // The popup behind this can only ever be reached via the debug-gated "Status…" menu item,
            // so formatting its contents when no debugger is attached would be pure wasted work on
            // every poll tick for the life of the process.
            if (Debugger.IsAttached)
                _form!.Update(view);
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
        // dispose the icon so it never lingers in the tray after shutdown.
        var form = _form;
        if (form is { IsDisposed: false })
        {
            try
            {
                form.BeginInvoke((Action)(() =>
                {
                    // Close the plugin manager first: its nested modal loop owns the UI thread, and
                    // ExitThread cannot end the outer loop while that one is running.
                    _pluginsDialog?.Close();
                    Application.ExitThread();
                }));
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
