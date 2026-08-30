namespace GameCapture.Engine.Plugins;

/// <summary>
/// The pair the tray needs to offer plugin management, handed in by the host the same way
/// <see cref="Tray.TrayControls"/> hands in its callbacks. Grouped into one type so the tray's
/// control record stays readable and so a build with no plugin support is a single null.
/// </summary>
/// <param name="Installer">Catalog fetch, install, update and removal.</param>
/// <param name="Launcher">Start/stop of installed plugins as child processes.</param>
/// <param name="Settings">Per-user preview-channel preference.</param>
public sealed record PluginServices(
    PluginInstaller Installer,
    PluginLauncher Launcher,
    PluginManagerSettings Settings);
