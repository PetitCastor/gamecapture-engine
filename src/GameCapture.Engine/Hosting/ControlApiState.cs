using GameCapture.Engine.Metrics;
using GameCapture.Engine.Tray;

namespace GameCapture.Engine;

/// <summary>
/// Late-bound handoff from <see cref="EngineDesktopLifetime"/> to the control API's Kestrel routes.
/// <see cref="Grpc.GrpcHost.BuildGrpcHost"/> maps those routes while the app is being built, before
/// <see cref="EngineDesktopLifetime.Start"/> constructs <see cref="TrayControls"/> and starts the
/// metrics reporter — so the endpoints close over this box and read whatever it holds at request
/// time instead of a value captured at map time. Reference assignment is atomic in .NET, so a route
/// handler always sees either the old value or the new one, never a partial write.
/// </summary>
internal sealed class ControlApiState
{
    private TrayControls? _controls;
    private MetricsSnapshot? _latestMetrics;

    /// <summary>
    /// The tray's callback bundle, once <see cref="EngineDesktopLifetime.Start"/> has built it.
    /// <c>null</c> until then, and forever when the engine is interactive but the tray itself is
    /// disabled (<c>TrayEnabled: false</c>) — the endpoints that need it degrade to 503 rather than
    /// throwing.
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
