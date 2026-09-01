using GameCapture.Engine.Plugins;

namespace GameCapture.Engine.Tray;

/// <summary>
/// Everything the tray needs to offer control actions, handed in by the host so the tray stays free
/// of engine internals. The three callbacks run on the tray's UI thread; the host is responsible for
/// their side effects — persisting to <c>engine-config.json</c> and, for a monitor/settings change,
/// restarting the process so the new value takes effect (every field below is bound at startup).
/// Plugin management is the exception to that last part: installing or launching a plugin changes
/// nothing the engine bound at startup, so it applies immediately and never restarts anything.
/// </summary>
/// <remarks>
/// Not scoped to the tray: <see cref="EngineDesktopLifetime.Start"/> builds this (and
/// <see cref="Plugins"/>) for every interactive run regardless of <c>trayEnabled</c> (TASK-UI-04) —
/// the main window and the loopback control API are both consumers now, and <c>trayEnabled</c> only
/// decides whether the <c>NotifyIcon</c> itself exists.
/// </remarks>
/// <param name="MonitorLabels">Display strings for each monitor, in the same order as the engine's
/// monitor list, so the selected index maps straight to <see cref="EngineConfig.MonitorIndex"/>.</param>
/// <param name="CurrentMonitorIndex">Index of the monitor currently being captured.</param>
/// <param name="ReadSettings">Returns the current editable settings. This is a callback rather than
/// a startup snapshot because theme-only saves apply without restarting the process.</param>
/// <param name="AvailableOcrLanguages">Installed OCR language tags offered in the settings screen.</param>
/// <param name="OnSelectMonitor">Invoked with the chosen monitor index.</param>
/// <param name="OnUpdateSettings">Atomically transforms and saves the current settings; returns what
/// was actually persisted (a bad value may have been corrected) and whether the engine needs to
/// restart. The transformation runs under the host's settings lock so concurrent partial API patches
/// cannot overwrite one another.</param>
/// <param name="OnExit">Invoked to shut the engine down.</param>
/// <param name="Plugins">Catalog/install/launch services behind the plugin manager, or <c>null</c>
/// to leave the plugin entries out of the menu entirely.</param>
public sealed record TrayControls(
    IReadOnlyList<string> MonitorLabels,
    int CurrentMonitorIndex,
    Func<EngineSettings> ReadSettings,
    IReadOnlyList<string> AvailableOcrLanguages,
    Action<int> OnSelectMonitor,
    Func<Func<EngineSettings, EngineSettings>, SettingsSaveResult> OnUpdateSettings,
    Action OnExit,
    PluginServices? Plugins = null)
{
    /// <summary>Current settings for whichever interactive surface is reading them.</summary>
    public EngineSettings Settings => ReadSettings();

    /// <summary>Saves a complete settings snapshot from the WinForms settings dialog.</summary>
    public SettingsSaveResult SaveSettings(EngineSettings settings)
        => OnUpdateSettings(_ => settings);

    /// <summary>Applies a partial transformation atomically to the latest settings snapshot.</summary>
    public SettingsSaveResult UpdateSettings(Func<EngineSettings, EngineSettings> update)
        => OnUpdateSettings(update);
}
