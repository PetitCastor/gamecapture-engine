using System.Text.Json;

namespace GameCapture.Engine.Plugins;

/// <summary>
/// Parses the plugins repository's <c>plugins.json</c> and decides what the engine is allowed to
/// download. Pure: no I/O, no clock — the fetch itself lives in <see cref="PluginInstaller"/>.
/// </summary>
/// <remarks>
/// The trust rules here are the whole security model of the plugin manager, so they are code rather
/// than documentation. The catalog is served from a URL this assembly hard-codes, and every asset
/// URL — including each redirect hop the download follows — must resolve to the plugins repository's
/// own releases. A catalog that has been tampered with can therefore rename or re-describe a plugin,
/// but it cannot point the engine at a binary hosted anywhere else.
/// </remarks>
public static class PluginCatalog
{
    /// <summary>
    /// The one catalog the engine reads. Deliberately not configurable: a settings knob pointing at
    /// an arbitrary catalog would hand an attacker the plugin list and, through it, the install
    /// prompt — and the host allowlist below would be the only thing left standing.
    /// </summary>
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/PetitCastor/gamecapture-plugins/master/plugins.json";

    private const string CatalogHost = "raw.githubusercontent.com";
    private const string CatalogPathPrefix = "/PetitCastor/gamecapture-plugins/";

    /// <summary>Release assets live under this path on <c>github.com</c>.</summary>
    private const string ReleasePathPrefix = "/PetitCastor/gamecapture-plugins/releases/";

    /// <summary>
    /// Hosts a release download may legitimately touch. <c>github.com</c> issues the redirect and is
    /// path-checked; the two content hosts serve the signed, opaque blob URLs that redirect lands on,
    /// whose paths carry no repository identity to check — reaching them at all requires having
    /// followed a validated <c>github.com</c> release URL first.
    /// </summary>
    private static readonly string[] AssetHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the catalog document. Entries missing a required field are dropped rather than failing
    /// the whole fetch, so one malformed row cannot hide the rest of the catalog; malformed JSON
    /// fails outright, because then nothing about the document can be believed.
    /// </summary>
    /// <param name="json">Raw catalog document.</param>
    /// <param name="entries">Parsed entries; empty when parsing failed.</param>
    /// <param name="error">Human-readable reason, shown verbatim in the dialog's status line.</param>
    public static bool TryParse(string json, out IReadOnlyList<CatalogEntry> entries, out string error)
    {
        entries = [];

        List<CatalogEntry>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"The plugin catalog could not be read: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "The plugin catalog was empty.";
            return false;
        }

        entries = parsed
            .Where(e => !string.IsNullOrWhiteSpace(e.Id)
                        && !string.IsNullOrWhiteSpace(e.Name)
                        && !string.IsNullOrWhiteSpace(e.DownloadUrl))
            .ToList();
        error = "";
        return true;
    }

    /// <summary>Whether <paramref name="url"/> is the catalog document itself.</summary>
    public static bool IsCatalogUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.Equals(uri.Host, CatalogHost, StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.StartsWith(CatalogPathPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a release asset may be downloaded from <paramref name="url"/>. Applied to the catalog's
    /// own <c>downloadUrl</c> and again to every redirect the download follows.
    /// </summary>
    public static bool IsTrustedAssetUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsTrustedAssetUri(uri);

    /// <inheritdoc cref="IsTrustedAssetUrl(string)"/>
    public static bool IsTrustedAssetUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!AssetHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return false;

        // Only github.com carries a checkable repository path. The content hosts are reachable solely
        // by following a redirect that already passed this check.
        return !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a catalog id is safe to use as a directory name. Restrictive on purpose: the id comes
    /// off the network and is combined into a path, so anything outside this alphabet — a separator,
    /// a drive letter, a dotted traversal, a device name — is rejected before it can escape the
    /// plugins root.
    /// </summary>
    public static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64)
            return false;

        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return id[0] != '-' && id[^1] != '-';
    }
}
