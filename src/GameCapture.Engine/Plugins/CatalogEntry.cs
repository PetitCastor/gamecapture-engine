namespace GameCapture.Engine.Plugins;

/// <summary>
/// One entry of the plugins repository's <c>plugins.json</c> catalog, exactly as published — the
/// engine adds nothing to it. The catalog carries no version or checksum field on purpose: the
/// download URL is the version-less <c>releases/latest/download/…</c> redirect, and protocol
/// compatibility is settled by the connect-time handshake rather than by catalog metadata.
/// </summary>
/// <param name="Id">Stable kebab-case slug; also the per-plugin install folder name.</param>
/// <param name="Name">Display name, matching the plugin's assembly name.</param>
/// <param name="Description">One-line summary shown in the manager dialog.</param>
/// <param name="DownloadUrl">Release-asset URL. Never trusted on the strength of being in the
/// catalog — <see cref="PluginCatalog.IsTrustedAssetUrl"/> gates every use of it.</param>
public sealed record CatalogEntry(string Id, string Name, string Description, string DownloadUrl);
