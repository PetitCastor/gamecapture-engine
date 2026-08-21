namespace GameCapture.Engine.Tray;

/// <summary>
/// The subset of <see cref="EngineConfig"/> the tray settings screen edits. A plain snapshot: the
/// screen is seeded from one and hands back an edited one, which the host persists and applies by
/// restarting. Kept UI-agnostic so the dialog stays a pure layout of these fields.
/// </summary>
/// <param name="OutputDir">Absolute directory frame dumps land in.</param>
/// <param name="OcrLanguage">BCP-47 recognizer tag, or empty for "first available pack".</param>
/// <param name="ScanIntervalMs">Capture scan cadence in milliseconds.</param>
public sealed record EngineSettings(string OutputDir, string OcrLanguage, int ScanIntervalMs);
