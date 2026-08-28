namespace GameCapture.Engine.Plugins;

/// <summary>
/// Where installed plugins live. Sits beside <c>engine-config.json</c> under
/// <c>%LOCALAPPDATA%\GameCapture</c> rather than inside the engine's own Velopack install directory
/// (<c>%LOCALAPPDATA%\GameCaptureEngine</c>), so updating or uninstalling the engine leaves a user's
/// plugins alone — the same separation the config already relies on.
/// </summary>
public static class PluginPaths
{
    private const string ConfigDirectoryName = "GameCapture";
    private const string PluginsDirectoryName = "plugins";

    /// <summary>Install-state document, one per user.</summary>
    public const string StateFileName = "installed.json";

    /// <summary>Staging area for downloads. On the same volume as the install folders so the
    /// swap-into-place at the end of an install is a move, not a copy.</summary>
    public const string StagingDirectoryName = ".staging";

    /// <summary>Returns <c>%LOCALAPPDATA%\GameCapture\plugins</c>.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ConfigDirectoryName,
            PluginsDirectoryName);

    /// <summary>Path of the install-state document under <paramref name="root"/>.</summary>
    public static string StateFile(string root) => Path.Combine(root, StateFileName);

    /// <summary>
    /// Install directory for one catalog id. Rejects an id that has not passed
    /// <see cref="PluginCatalog.IsValidId"/>, so a catalog value can never be combined into a path
    /// that escapes <paramref name="root"/>.
    /// </summary>
    public static string PluginDirectory(string root, string id)
    {
        if (!PluginCatalog.IsValidId(id))
            throw new ArgumentException($"Not a usable plugin id: '{id}'.", nameof(id));

        return Path.Combine(root, id);
    }
}
