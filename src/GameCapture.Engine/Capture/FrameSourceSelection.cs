namespace GameCapture.Engine;

/// <summary>
/// The selected frame source together with the startup details consumed by the desktop lifetime.
/// Ownership of <see cref="Source"/> passes to <see cref="EngineHost"/>.
/// </summary>
internal sealed record FrameSourceSelection(
    IFrameSource Source,
    string Description,
    IReadOnlyList<string> MonitorLabels,
    int CurrentMonitorIndex,
    MonitorInfo? CaptureMonitor = null);
