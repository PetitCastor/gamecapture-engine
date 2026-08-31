namespace GameCapture.Engine.Tray;

/// <summary>
/// UI theme for the WebView2-hosted main window (TASK-UI-03 onward). <see cref="System"/> follows
/// the OS setting; the tray/settings screens only ever pass this value through, they do not render
/// it themselves.
/// </summary>
public enum EngineTheme
{
    System,
    Light,
    Dark,
}
