namespace GameCapture.Engine.Tray;

/// <summary>
/// Every field of <see cref="EngineConfig"/> the tray settings screen edits, except
/// <see cref="EngineConfig.MonitorIndex"/> — that one has its own menu entry with friendly monitor
/// labels. A plain snapshot: the screen is seeded from one and hands back an edited one, which the
/// host persists and applies by restarting. Kept UI-agnostic so the dialog stays a pure layout of
/// these fields.
/// </summary>
/// <param name="OutputDir">Absolute directory frame dumps land in.</param>
/// <param name="OcrLanguage">BCP-47 recognizer tag, or empty for "first available pack".</param>
/// <param name="ScanIntervalMs">Capture scan cadence in milliseconds.</param>
/// <param name="Hotkey">Global hotkey that triggers a manual scan, e.g. "Ctrl+Shift+F12".</param>
/// <param name="PipeName">Named pipe the gRPC server listens on; plugins must use the same name.</param>
/// <param name="MetricsEnabled">Whether the live CPU/memory/GPU status bar is shown.</param>
/// <param name="MetricsIntervalMs">Status bar refresh cadence in milliseconds.</param>
/// <param name="TrayEnabled">Whether the Windows tray icon is shown.</param>
public sealed record EngineSettings(
    string OutputDir,
    string OcrLanguage,
    int ScanIntervalMs,
    string Hotkey,
    string PipeName,
    bool MetricsEnabled,
    int MetricsIntervalMs,
    bool TrayEnabled);
