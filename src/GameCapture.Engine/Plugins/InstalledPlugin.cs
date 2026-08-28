namespace GameCapture.Engine.Plugins;

/// <summary>
/// A plugin the engine has installed for this user, as recorded in <c>installed.json</c>. The record
/// is the only thing that remembers which release a folder came from — the extracted exe carries no
/// marker the engine reads back — so it is what "update available" is decided against.
/// </summary>
/// <param name="Id">Catalog id; matches the install folder name.</param>
/// <param name="Name">Display name at install time, so an uninstall still reads well if the plugin
/// later leaves the catalog.</param>
/// <param name="Version">Release tag the asset was downloaded from, e.g. <c>v1.0.4</c>.</param>
/// <param name="ExecutablePath">Absolute path of the extracted exe.</param>
/// <param name="InstalledUtc">When the install completed.</param>
public sealed record InstalledPlugin(
    string Id,
    string Name,
    string Version,
    string ExecutablePath,
    DateTimeOffset InstalledUtc);
