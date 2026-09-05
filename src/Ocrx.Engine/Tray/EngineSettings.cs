namespace Ocrx.Engine.Tray;

/// <summary>
/// Every field of <see cref="EngineConfig"/> the tray settings screen edits. A plain snapshot: the
/// screen is seeded from one and hands back an edited one, which the host persists and applies by
/// restarting. Kept UI-agnostic so the dialog stays a pure layout of these fields.
/// </summary>
/// <param name="OutputDir">Absolute directory frame dumps land in.</param>
/// <param name="OcrLanguage">BCP-47 recognizer tag, or empty for "first available pack".</param>
/// <param name="ScanIntervalMs">Capture scan cadence in milliseconds.</param>
/// <param name="Hotkey">Global hotkey that triggers a manual scan, e.g. "Ctrl+Shift+F12".</param>
/// <param name="PipeName">Named pipe the gRPC server listens on; plugins must use the same name.</param>
/// <param name="MetricsEnabled">Whether the live CPU/memory/GPU status bar is shown.</param>
/// <param name="MetricsIntervalMs">Status bar refresh cadence in milliseconds.</param>
/// <param name="TrayEnabled">Whether the Windows tray icon is shown.</param>
/// <param name="MonitorIndex">Index into the monitor list; used to have its own tray submenu, now
/// edited from the same settings pane as everything else.</param>
/// <param name="Theme">UI theme for the WebView2 main window; applied live, never restarts.</param>
public sealed record EngineSettings(
    string OutputDir,
    string OcrLanguage,
    int ScanIntervalMs,
    string Hotkey,
    string PipeName,
    bool MetricsEnabled,
    int MetricsIntervalMs,
    bool TrayEnabled,
    int MonitorIndex,
    EngineTheme Theme);
