using Ocrx.Engine.Metrics;
using Ocrx.Engine.Tray;

namespace Ocrx.Engine;

/// <summary>
/// Late-bound handoff from <see cref="EngineDesktopLifetime"/> to the control API's Kestrel routes.
/// <see cref="Grpc.GrpcHost.BuildGrpcHost"/> maps those routes while the app is being built, before
/// <see cref="EngineDesktopLifetime.Start"/> constructs <see cref="TrayControls"/> and starts the
/// metrics reporter — so the endpoints close over this box and read whatever it holds at request
/// time instead of a value captured at map time. Reference assignment is atomic in .NET, so a route
/// handler always sees either the old value or the new one, never a partial write; the backing
/// fields are <c>volatile</c> so that value is also promptly visible, since a request thread never
/// takes a lock with the tray/metrics thread that writes them.
/// </summary>
internal sealed class ControlApiState
{
    // volatile, not just reference-assignment atomicity: a request thread that never took a lock
    // with the writer (SetControls/SetMetrics run on the tray/metrics thread) needs a memory barrier
    // to see a fresh value promptly rather than a stale cached read.
    private volatile TrayControls? _controls;
    private volatile MetricsSnapshot? _latestMetrics;

    /// <summary>
    /// The tray/window's callback bundle, once <see cref="EngineDesktopLifetime.Start"/> has built it.
    /// <c>null</c> until then, and forever for a non-interactive run (replay/video) — the endpoints
    /// that need it degrade to 503 rather than throwing. Built for every interactive run regardless of
    /// <c>TrayEnabled</c>, which only gates whether a <c>NotifyIcon</c> exists (TASK-UI-04): the window
    /// is the primary surface now, so settings/plugin management must work with the tray icon off.
    /// </summary>
    public TrayControls? Controls => _controls;

    /// <summary>Latest process-health sample, or <c>null</c> before the first one arrives (or when
    /// metrics are disabled) — mirrors what <see cref="Tray.TrayApplication"/> shows.</summary>
    public MetricsSnapshot? LatestMetrics => _latestMetrics;

    public event Action<TrayControls?>? ControlsChanged;

    public void SetControls(TrayControls controls)
    {
        _controls = controls;
        ControlsChanged?.Invoke(controls);
    }

    /// <summary>Wired to <see cref="Metrics.MetricsReporter.Sampled"/> alongside the tray's own
    /// subscription, so the API's <c>/api/status</c> shows the same numbers as the tray does.</summary>
    public void SetMetrics(MetricsSnapshot snapshot) => _latestMetrics = snapshot;
}
