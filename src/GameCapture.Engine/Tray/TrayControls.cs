namespace GameCapture.Engine.Tray;

/// <summary>
/// Everything the tray needs to offer control actions, handed in by the host so the tray stays free
/// of engine internals. The three callbacks run on the tray's UI thread; the host is responsible for
/// their side effects — persisting to <c>engine-config.json</c> and, for a monitor/settings change,
/// restarting the process so the new value takes effect (every field below is bound at startup).
/// </summary>
/// <param name="MonitorLabels">Display strings for each monitor, in the same order as the engine's
/// monitor list, so the selected index maps straight to <see cref="EngineConfig.MonitorIndex"/>.</param>
/// <param name="CurrentMonitorIndex">Index of the monitor currently being captured.</param>
/// <param name="Settings">Current editable settings, used to seed the settings screen.</param>
/// <param name="AvailableOcrLanguages">Installed OCR language tags offered in the settings screen.</param>
/// <param name="OnSelectMonitor">Invoked with the chosen monitor index.</param>
/// <param name="OnSaveSettings">Invoked with the edited settings when the screen is confirmed.</param>
/// <param name="OnExit">Invoked to shut the engine down.</param>
public sealed record TrayControls(
    IReadOnlyList<string> MonitorLabels,
    int CurrentMonitorIndex,
    EngineSettings Settings,
    IReadOnlyList<string> AvailableOcrLanguages,
    Action<int> OnSelectMonitor,
    Action<EngineSettings> OnSaveSettings,
    Action OnExit);
