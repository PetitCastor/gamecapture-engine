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
///
/// That last guarantee is why a catalog URL and a redirect target are checked by two different
/// methods. GitHub serves release bytes from content hosts whose paths are opaque signed blobs
/// carrying no repository identity, so there is nothing on them to check — accepting one straight
/// out of the catalog would accept any file on any repository. They are therefore reachable only as
/// the target of a redirect from a URL that already passed the strict, path-checked rule.
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

    private const string ReleaseHost = "github.com";

    /// <summary>Release assets live under this path on <c>github.com</c>.</summary>
    private const string ReleasePathPrefix = "/PetitCastor/gamecapture-plugins/releases/";

    /// <summary>
    /// Content hosts a validated release URL is allowed to redirect to. Their paths are signed blob
    /// references with no repository identity in them, so they are accepted as a redirect target and
    /// never as a starting point.
    /// </summary>
    private static readonly string[] RedirectOnlyHosts =
    [
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
           && IsOn(uri, CatalogHost)
           && uri.AbsolutePath.StartsWith(CatalogPathPrefix, StringComparison.Ordinal);

    /// <inheritdoc cref="IsCatalogUrl(string)"/>
    public static bool IsCatalogUri(Uri uri) => IsCatalogUrl(uri.AbsoluteUri);

    /// <summary>
    /// Whether a download may <em>start</em> at <paramref name="url"/>. This is the rule applied to a
    /// catalog entry's <c>downloadUrl</c>: the plugins repository's own releases on <c>github.com</c>
    /// and nothing else.
    /// </summary>
    public static bool IsTrustedAssetUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsTrustedAssetUri(uri);

    /// <inheritdoc cref="IsTrustedAssetUrl(string)"/>
    public static bool IsTrustedAssetUri(Uri uri)
        => IsOn(uri, ReleaseHost) && uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a download already under way may follow a redirect to <paramref name="uri"/>: either
    /// another release URL, or one of the content hosts GitHub serves release bytes from. Only ever
    /// reached from a URL that passed <see cref="IsTrustedAssetUri"/> first.
    /// </summary>
    public static bool IsTrustedRedirectTarget(Uri uri)
        => IsTrustedAssetUri(uri) || RedirectOnlyHosts.Any(host => IsOn(uri, host));

    // Host comparison is exact and case-insensitive against Uri.Host, which is already punycode for
    // an IDN and excludes any userinfo, so "github.com@evil.example" and a unicode look-alike both
    // fail here. The default-port requirement keeps a non-standard listener off the allowlist.
    private static bool IsOn(Uri uri, string host)
        => uri.Scheme == Uri.UriSchemeHttps
           && uri.IsDefaultPort
           && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

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
