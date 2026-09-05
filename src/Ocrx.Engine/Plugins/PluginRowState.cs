namespace Ocrx.Engine.Plugins;

/// <summary>Install status of one catalog entry, as shown in the plugin manager's list.</summary>
public enum PluginRowState
{
    /// <summary>In the catalog, not on this machine.</summary>
    NotInstalled,

    /// <summary>Installed, and either current or of unknown currency (the version probe failed).</summary>
    Installed,

    /// <summary>Installed, and the catalog's latest release carries a different tag.</summary>
    UpdateAvailable,

    /// <summary>Fails a trust rule — an off-repository download URL or an unusable id — so the engine
    /// offers no way to install it.</summary>
    Blocked,
}
