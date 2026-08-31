using GameCapture.Engine.Tray;

namespace GameCapture.Engine;

/// <summary>
/// Partial JSON settings body accepted by the control API. Nullable members distinguish an omitted
/// property from a supplied value, so a theme-only request cannot reset unrelated startup settings.
/// </summary>
internal sealed record EngineSettingsPatch(
    string? OutputDir = null,
    string? OcrLanguage = null,
    int? ScanIntervalMs = null,
    string? Hotkey = null,
    string? PipeName = null,
    bool? MetricsEnabled = null,
    int? MetricsIntervalMs = null,
    bool? TrayEnabled = null,
    int? MonitorIndex = null,
    EngineTheme? Theme = null)
{
    public EngineSettings ApplyTo(EngineSettings current)
        => current with
        {
            OutputDir = OutputDir ?? current.OutputDir,
            OcrLanguage = OcrLanguage ?? current.OcrLanguage,
            ScanIntervalMs = ScanIntervalMs ?? current.ScanIntervalMs,
            Hotkey = Hotkey ?? current.Hotkey,
            PipeName = PipeName ?? current.PipeName,
            MetricsEnabled = MetricsEnabled ?? current.MetricsEnabled,
            MetricsIntervalMs = MetricsIntervalMs ?? current.MetricsIntervalMs,
            TrayEnabled = TrayEnabled ?? current.TrayEnabled,
            MonitorIndex = MonitorIndex ?? current.MonitorIndex,
            Theme = Theme ?? current.Theme,
        };
}
