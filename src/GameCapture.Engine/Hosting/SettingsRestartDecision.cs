namespace GameCapture.Engine;

/// <summary>
/// Decides whether a settings patch needs the engine to restart to take effect. Pulled out of
/// <see cref="EngineDesktopLifetime"/> as a pure function so the rule — the headline requirement of
/// the theme setting — has a surface that does not need a WinForms tray or a running
/// <see cref="EngineHost"/> to test.
/// </summary>
internal static class SettingsRestartDecision
{
    /// <summary>
    /// Theme is UI-only and applied live by the web UI (TASK-UI-05); a patch that touches nothing
    /// else must persist without restarting, or picking a theme would kill and relaunch the engine
    /// for a change the running process never needed to apply. Every other field is bound at
    /// startup, so any other change — alone or alongside theme — still needs the restart.
    /// </summary>
    public static bool IsRestartRequired(IReadOnlyDictionary<string, object> changes)
        => changes.Count != 0 && !(changes.Count == 1 && changes.ContainsKey("theme"));
}
