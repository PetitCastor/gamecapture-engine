using System.Diagnostics;
using System.Windows.Forms;
using GameCapture.Engine.Metrics;

namespace GameCapture.Engine.Tray;

/// <summary>
/// Hosts the Windows tray icon inside the engine process. Runs its own STA thread with a WinForms
/// message loop; a UI timer polls <see cref="EngineStatus"/> and the latest metrics sample, composes
/// a <see cref="TrayView"/>, and repaints the icon, tooltip and popup. Read-only in this phase — no
/// control actions.
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
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly FrameRateTracker _fps = new();

    private Thread? _thread;
    private NotifyIcon? _icon;
    private StatusForm? _form;
    private ContextMenuStrip? _menu;
    private System.Windows.Forms.Timer? _timer;
    private TrayIconFactory? _icons;
    private long _lastPollTimestamp;

    // Written from the metrics timer thread, read on the UI thread. A reference assignment is atomic
    // and the tray only ever wants the most recent sample, so no lock is needed.
    private volatile MetricsSnapshot? _latestMetrics;

    public TrayApplication(ConsoleSink sink, EngineStatus status, bool metricsEnabled, TimeSpan pollInterval)
    {
        _sink = sink;
        _status = status;
        _metricsEnabled = metricsEnabled;
        _pollInterval = pollInterval;
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
            // the popup is never opened would leave BeginInvoke to throw — ExitThread would never fire,
            // the join would time out, and the NotifyIcon would linger as a ghost in the tray.
            _ = _form.Handle;

            _menu = new ContextMenuStrip();
            _menu.Items.Add("Status…", null, (_, _) => ShowPopup());

            _icon = new NotifyIcon
            {
                Visible = true,
                Icon = _icons.For(TrayIconState.Idle),
                Text = "GameCapture engine",
                ContextMenuStrip = _menu,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowPopup();
            };

            _lastPollTimestamp = Stopwatch.GetTimestamp();
            Refresh();

            _timer = new System.Windows.Forms.Timer { Interval = (int)_pollInterval.TotalMilliseconds };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();

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
                form.BeginInvoke((Action)Application.ExitThread);
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
