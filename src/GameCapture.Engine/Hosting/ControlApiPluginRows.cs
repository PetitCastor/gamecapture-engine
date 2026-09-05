using GameCapture.Engine.Plugins;

namespace GameCapture.Engine;

internal static class ControlApiPluginRows
{
    public static IReadOnlyList<PluginRow> Build(
        IReadOnlyList<CatalogEntry> catalog,
        PluginServices plugins,
        IReadOnlyDictionary<string, string> latestVersions)
        => PluginRowBuilder.Build(
            catalog,
            plugins.Installer.State.Entries,
            plugins.Launcher.RunningIds,
            latestVersions,
            PausedPreviewIds(plugins),
            entry => plugins.RoiOverlays?.GetState(entry) ?? default,
            entry => plugins.Launcher.Logs?.Has(entry.Id) ?? false,
            entry => plugins.Settings.IsAutoStartEnabled(entry.Id));

    public static IReadOnlyList<CatalogEntry> MergeInstalled(IReadOnlyList<CatalogEntry> catalog, PluginServices plugins)
    {
        var catalogIds = catalog.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var merged = catalog.ToList();
        foreach (var installed in plugins.Installer.State.Entries.Values)
        {
            if (catalogIds.Contains(installed.Id))
                continue;

            merged.Add(new CatalogEntry(installed.Id, installed.Name, "", installed.DownloadUrl, installed.Channel, installed.ClientName));
        }

        return merged;
    }

    private static IReadOnlyCollection<string> PausedPreviewIds(PluginServices plugins)
        => !plugins.Settings.IncludePreviews
            ? plugins.Installer.State.Entries.Values
                .Where(installed => installed.Channel == ReleaseChannel.Preview)
                .Select(installed => installed.Id)
                .ToHashSet(StringComparer.Ordinal)
            : [];
}
