using System.ComponentModel;

namespace Ocrx.Engine.Plugins;

/// <summary>
/// Launches the installed plugins the user has left on auto-start, once, as the engine finishes
/// coming up. Auto-start is on by default: a plugin that is installed is a plugin that is wanted, and
/// the pre-existing alternative was starting every one of them by hand after every engine launch.
/// </summary>
/// <remarks>
/// This does not make <see cref="PluginLauncher"/> a supervisor — it still never restarts anything on
/// its own. This is one startup pass with the same semantics as the user clicking Start on each row.
/// </remarks>
internal static class PluginAutoStarter
{
    /// <summary>
    /// Installed plugins eligible for auto-start, in a stable order so the log reads the same way
    /// twice. Split from <see cref="StartAll"/> so the choice can be tested without launching
    /// processes.
    /// </summary>
    public static IReadOnlyList<InstalledPlugin> Select(PluginInstallState state, PluginManagerSettings settings)
        => [.. state.Entries.Values
            .Where(plugin => settings.IsAutoStartEnabled(plugin.Id))
            .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Starts every eligible plugin, reporting each outcome through <paramref name="log"/>. One
    /// plugin that cannot start (its folder was deleted behind the engine's back, the exe is blocked)
    /// never stops the rest: the row it belongs to is still there to retry from, and the engine has no
    /// stake in any single plugin running.
    /// </summary>
    public static void StartAll(PluginServices plugins, Action<string>? log = null)
    {
        foreach (var plugin in Select(plugins.Installer.State, plugins.Settings))
        {
            try
            {
                plugins.Launcher.Start(plugin);
                log?.Invoke($"Plugin:    started {plugin.Name} {plugin.Version}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                log?.Invoke($"Plugin:    {plugin.Name} did not start ({ex.Message})");
            }
        }
    }
}
