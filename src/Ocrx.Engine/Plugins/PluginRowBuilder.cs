namespace Ocrx.Engine.Plugins;

/// <summary>
/// Pure composition of the catalog, the install state, the running-process set and whatever version
/// probes came back into the rows the plugin manager renders. No UI, no I/O — every decision about
/// what a row says or offers is made here and pinned by tests, mirroring how
/// <see cref="Tray.TrayViewBuilder"/> owns the tray's display decisions.
/// </summary>
internal static class PluginRowBuilder
{
    /// <param name="catalog">Entries as published, in catalog order.</param>
    /// <param name="installed">Install state, keyed by catalog id.</param>
    /// <param name="runningIds">Ids this engine currently has a live child process for.</param>
    /// <param name="latestVersions">Release tags resolved per id; an id missing here simply has no
    /// update information, which never reads as an update being available.</param>
    /// <param name="readAutoStart">Whether the engine launches this entry at startup; absent means
    /// the default, on.</param>
    public static IReadOnlyList<PluginRow> Build(
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlyDictionary<string, InstalledPlugin> installed,
        IReadOnlyCollection<string> runningIds,
        IReadOnlyDictionary<string, string> latestVersions,
        IReadOnlyCollection<string>? updatesPausedIds = null,
        Func<CatalogEntry, RoiOverlayState>? readRoiOverlayState = null,
        Func<CatalogEntry, bool>? readHasLogs = null,
        Func<CatalogEntry, bool>? readAutoStart = null)
    {
        var rows = new List<PluginRow>(catalog.Count);

        foreach (var entry in catalog)
        {
            var trusted = PluginCatalog.IsValidId(entry.Id) && PluginCatalog.IsTrustedAssetUrl(entry.DownloadUrl);
            var isInstalled = installed.TryGetValue(entry.Id, out var record);
            latestVersions.TryGetValue(entry.Id, out var latest);
            latest ??= "";
            var updatesPaused = entry.Channel == ReleaseChannel.Preview
                                && isInstalled
                                && record!.Channel == ReleaseChannel.Preview
                                && updatesPausedIds?.Contains(entry.Id) == true;

            // A blocked entry stays visible and keeps whatever it reports about an existing install:
            // hiding it would make a plugin that silently vanished from the list indistinguishable
            // from one that was never there, and the row is the only place the reason is stated.
            var state = !trusted
                ? PluginRowState.Blocked
                : !isInstalled
                    ? PluginRowState.NotInstalled
                    : !updatesPaused && latest.Length > 0 && !string.Equals(latest, record!.Version, StringComparison.OrdinalIgnoreCase)
                        ? PluginRowState.UpdateAvailable
                        : PluginRowState.Installed;

            var roiOverlay = readRoiOverlayState?.Invoke(entry) ?? default;
            rows.Add(new PluginRow(
                entry,
                state,
                isInstalled ? record!.Version : "",
                latest,
                runningIds.Contains(entry.Id),
                updatesPaused,
                roiOverlay.CanShow,
                roiOverlay.IsVisible,
                readHasLogs?.Invoke(entry) ?? false,
                readAutoStart?.Invoke(entry) ?? true));
        }

        return rows;
    }
}
