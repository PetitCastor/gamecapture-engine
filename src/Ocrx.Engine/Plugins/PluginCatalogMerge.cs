namespace Ocrx.Engine.Plugins;

/// <summary>
/// Combines a stable and a preview catalog into the one list the manager renders. Pure: no I/O, no
/// clock, mirroring how <see cref="PluginRowBuilder"/> keeps its own decisions testable in isolation.
/// </summary>
public static class PluginCatalogMerge
{
    /// <summary>
    /// Appends <paramref name="previews"/> to <paramref name="stable"/>, keeping the id namespace a
    /// strict partition. An id published in both catalogs is dropped from the preview side only —
    /// not the whole preview fetch — since one repeated id must not hide every other preview.
    /// </summary>
    /// <param name="droppedPreviewIds">Preview ids skipped because a stable entry already claims them.</param>
    public static IReadOnlyList<CatalogEntry> Combine(
        IReadOnlyList<CatalogEntry> stable,
        IReadOnlyList<CatalogEntry> previews,
        out IReadOnlyList<string> droppedPreviewIds)
    {
        var stableIds = stable.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var combined = new List<CatalogEntry>(stable);
        var dropped = new List<string>();

        foreach (var preview in previews)
        {
            if (stableIds.Contains(preview.Id))
            {
                dropped.Add(preview.Id);
                continue;
            }

            combined.Add(preview);
        }

        droppedPreviewIds = dropped;
        return combined;
    }
}
