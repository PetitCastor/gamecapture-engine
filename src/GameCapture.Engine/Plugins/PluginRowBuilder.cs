namespace GameCapture.Engine.Plugins;

/// <summary>
/// Pure composition of the catalog, the install state, the running-process set and whatever version
/// probes came back into the rows the plugin manager renders. No UI, no I/O — every decision about
/// what a row says or offers is made here and pinned by tests, mirroring how
/// <see cref="Tray.TrayViewBuilder"/> owns the tray's display decisions.
/// </summary>
public static class PluginRowBuilder
{
    /// <param name="catalog">Entries as published, in catalog order.</param>
    /// <param name="installed">Install state, keyed by catalog id.</param>
    /// <param name="runningIds">Ids this engine currently has a live child process for.</param>
    /// <param name="latestVersions">Release tags resolved per id; an id missing here simply has no
    /// update information, which never reads as an update being available.</param>
    public static IReadOnlyList<PluginRow> Build(
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlyDictionary<string, InstalledPlugin> installed,
        IReadOnlyCollection<string> runningIds,
        IReadOnlyDictionary<string, string> latestVersions)
    {
        var rows = new List<PluginRow>(catalog.Count);

        foreach (var entry in catalog)
        {
            var trusted = PluginCatalog.IsValidId(entry.Id) && PluginCatalog.IsTrustedAssetUrl(entry.DownloadUrl);
            var isInstalled = installed.TryGetValue(entry.Id, out var record);
            latestVersions.TryGetValue(entry.Id, out var latest);
            latest ??= "";

            // A blocked entry stays visible and keeps whatever it reports about an existing install:
            // hiding it would make a plugin that silently vanished from the list indistinguishable
            // from one that was never there, and the row is the only place the reason is stated.
            var state = !trusted
                ? PluginRowState.Blocked
                : !isInstalled
                    ? PluginRowState.NotInstalled
                    : latest.Length > 0 && !string.Equals(latest, record!.Version, StringComparison.OrdinalIgnoreCase)
                        ? PluginRowState.UpdateAvailable
                        : PluginRowState.Installed;

            rows.Add(new PluginRow(
                entry,
                state,
                isInstalled ? record!.Version : "",
                latest,
                runningIds.Contains(entry.Id)));
        }

        return rows;
    }
}
