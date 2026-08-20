namespace GameCapture.Engine.Tray;

/// <summary>
/// A fully-formatted, render-ready snapshot for the tray — every field is a display string so the
/// WinForms layer places labels without any formatting logic of its own, and so the whole
/// composition is unit-testable without a UI. Built by <see cref="TrayViewBuilder"/>.
/// </summary>
public sealed record TrayView(
    TrayIconState IconState,
    string Tooltip,
    string Mode,
    string EngineVersion,
    string Frame,
    string OcrLanguage,
    string Fps,
    string Metrics,
    IReadOnlyList<string> Plugins);
