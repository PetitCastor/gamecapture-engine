namespace GameCapture.Engine.Plugins;

/// <summary>
/// One render-ready line of the plugin manager, plus the four "may I?" answers its buttons and the
/// tray's launch items read. Built by <see cref="PluginRowBuilder"/>; the UI only paints it.
/// </summary>
/// <param name="Entry">Catalog entry this row came from.</param>
/// <param name="State">Install status.</param>
/// <param name="InstalledVersion">Release tag on disk, or empty when not installed.</param>
/// <param name="LatestVersion">Latest release tag, or empty when the probe did not resolve one.</param>
/// <param name="IsRunning">Whether this engine launched the plugin and its process is still alive.</param>
/// <param name="UpdatesPaused">Whether a retained preview is intentionally excluded from preview updates.</param>
public sealed record PluginRow(
    CatalogEntry Entry,
    PluginRowState State,
    string InstalledVersion,
    string LatestVersion,
    bool IsRunning,
    bool UpdatesPaused)
{
    /// <summary>Catalog id, the key everything else is addressed by.</summary>
    public string Id => Entry.Id;

    /// <summary>Display name.</summary>
    public string Name => Entry.Name;

    /// <summary>The list's second column.</summary>
    public string StateText => State switch
    {
        PluginRowState.Blocked => "Blocked (untrusted source)",
        _ when UpdatesPaused && IsRunning => "Running preview (updates paused)",
        _ when UpdatesPaused => "Preview installed (updates paused)",
        PluginRowState.NotInstalled when Entry.Channel == ReleaseChannel.Preview => "Preview (not installed)",
        PluginRowState.UpdateAvailable when Entry.Channel == ReleaseChannel.Preview => $"Preview update available ({InstalledVersion} → {LatestVersion})",
        PluginRowState.Installed when Entry.Channel == ReleaseChannel.Preview && IsRunning => $"Running preview ({InstalledVersion})",
        PluginRowState.Installed when Entry.Channel == ReleaseChannel.Preview => $"Preview installed ({InstalledVersion})",
        PluginRowState.NotInstalled => "Not installed",
        PluginRowState.UpdateAvailable => $"Update available ({InstalledVersion} → {LatestVersion})",
        _ when IsRunning => $"Running ({InstalledVersion})",
        _ => $"Installed ({InstalledVersion})",
    };

    /// <summary>Label of the install button for this row, which doubles as the update action.</summary>
    public string InstallActionText => State == PluginRowState.UpdateAvailable ? "Update" : "Install";

    /// <summary>Installing over a running plugin means replacing files it has open: stop it first.</summary>
    public bool CanInstall => State is PluginRowState.NotInstalled or PluginRowState.UpdateAvailable && !IsRunning;

    /// <summary>
    /// Reinstalling the same version is allowed — it is the repair path for a damaged folder — but not
    /// while updates are intentionally paused, since a reinstall would just refetch the same build.
    /// </summary>
    public bool CanReinstall => State is PluginRowState.Installed && !IsRunning && !UpdatesPaused;

    /// <summary>Removing deletes the folder, so the same running-process rule applies.</summary>
    public bool CanRemove => State is PluginRowState.Installed or PluginRowState.UpdateAvailable && !IsRunning;

    /// <summary>Launchable once installed, regardless of whether an update is waiting.</summary>
    public bool CanLaunch => State is PluginRowState.Installed or PluginRowState.UpdateAvailable && !IsRunning;

    /// <summary>Only this engine's own child processes can be stopped from here.</summary>
    public bool CanStop => IsRunning;
}
