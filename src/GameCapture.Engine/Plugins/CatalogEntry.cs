namespace GameCapture.Engine.Plugins;

/// <summary>
/// One entry of a published plugin catalog, exactly as published — the engine adds nothing to it.
/// Protocol compatibility is settled by the connect-time handshake rather than by catalog metadata.
/// </summary>
/// <param name="Id">Stable kebab-case slug; also the per-plugin install folder name.</param>
/// <param name="Name">Display name, matching the plugin's assembly name.</param>
/// <param name="Description">One-line summary shown in the manager dialog.</param>
/// <param name="DownloadUrl">Release-asset URL. A stable entry's URL is GitHub's mutable "latest"
/// pointer; a preview entry's is pinned to one immutable release, since previews have no "latest"
/// alias. Never trusted on the strength of being in the catalog —
/// <see cref="PluginCatalog.IsTrustedAssetUrl"/> gates every use of it.</param>
/// <param name="Channel">Stable unless the catalog explicitly marks this as a preview.</param>
public sealed record CatalogEntry(
    string Id,
    string Name,
    string Description,
    string DownloadUrl,
    ReleaseChannel Channel = ReleaseChannel.Stable);
