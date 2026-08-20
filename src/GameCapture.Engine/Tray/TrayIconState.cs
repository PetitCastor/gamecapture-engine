namespace GameCapture.Engine.Tray;

/// <summary>
/// The coarse state the tray icon reflects at a glance, independent of the exact metrics.
/// <see cref="Error"/> is reserved for the alerts phase (plugin disconnect, GPU counters lost);
/// the read-only MVP builder never emits it, but <see cref="TrayIconFactory"/> already maps it so
/// that phase only has to flip the state, not touch the icon rendering.
/// </summary>
public enum TrayIconState
{
    /// <summary>Live capture, no plugin subscribed — the engine is up but nothing is consuming ticks.</summary>
    Idle,

    /// <summary>Live capture with at least one connected plugin.</summary>
    Capturing,

    /// <summary>Frames come from a PNG corpus or video, not a live screen.</summary>
    Replay,

    /// <summary>A fault the operator should notice. Not produced by the MVP builder.</summary>
    Error,
}
