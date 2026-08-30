namespace GameCapture.Engine.Plugins;

/// <summary>
/// Fetches the plugin catalog and installs, updates or removes a plugin under the per-user plugins
/// root. The engine's only outbound network use: everything else it does is local capture, so the
/// single <see cref="HttpClient"/> lives here and nowhere else.
/// </summary>
/// <remarks>
/// Network/file edge, excluded from the coverage gate — the decisions worth pinning were pushed out
/// into <see cref="PluginCatalog"/>, <see cref="ReleaseVersionResolver"/> and
/// <see cref="PluginArchive"/>, which this class sequences. Redirects are followed by hand rather
/// than by the handler so every hop can be re-checked against the trust rules; an automatic
/// redirect would let a compromised release URL walk the download off the allowlist unseen.
/// </remarks>
public sealed class PluginInstaller : IDisposable
{
    private const int MaxRedirects = 5;
    private const string StagedArchiveName = "asset.zip";
    private const string StagedPayloadName = "payload";
    private const string ReplacedDirectoryName = "replaced";

    private readonly HttpClient _http;
    private readonly string _root;

    /// <param name="root">Plugins root, normally <see cref="PluginPaths.DefaultRoot"/>.</param>
    /// <param name="handler">Transport override for tests. Must not follow redirects on its own.</param>
    public PluginInstaller(string root, HttpMessageHandler? handler = null)
    {
        _root = root;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameCapture.Engine");
        State = PluginInstallState.Load(PluginPaths.StateFile(root));
    }

    /// <summary>What is installed for this user. Reloaded from disk only at construction.</summary>
    public PluginInstallState State { get; }

    /// <summary>Downloads and parses the stable <c>plugins.json</c> catalog.</summary>
    /// <exception cref="InvalidOperationException">The catalog could not be fetched or read.</exception>
    public async Task<IReadOnlyList<CatalogEntry>> FetchCatalogAsync(CancellationToken cancellationToken)
        => await FetchCatalogAsync(
            new Uri(PluginCatalog.StableCatalogUrl),
            PluginCatalog.IsStableCatalogUri,
            ReleaseChannel.Stable,
            cancellationToken);

    /// <summary>Downloads the opt-in preview catalog. Call only after the user enables previews.</summary>
    public async Task<IReadOnlyList<CatalogEntry>> FetchPreviewCatalogAsync(CancellationToken cancellationToken)
        => await FetchCatalogAsync(
            new Uri(PluginCatalog.PreviewCatalogUrl),
            PluginCatalog.IsPreviewCatalogUri,
            ReleaseChannel.Preview,
            cancellationToken);

    private async Task<IReadOnlyList<CatalogEntry>> FetchCatalogAsync(
        Uri catalogUri,
        Func<Uri, bool> isAllowedCatalogUri,
        ReleaseChannel expectedChannel,
        CancellationToken cancellationToken)
    {
        using var response = await GetFollowingRedirectsAsync(
            catalogUri, isAllowedCatalogUri, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!PluginCatalog.TryParse(json, expectedChannel, out var entries, out var error))
            throw new InvalidOperationException(error);

        return entries;
    }

    /// <summary>
    /// Resolves the release tag behind a catalog entry's version-less download URL. Returns an empty
    /// string when it cannot be determined — the caller treats that as "no update information", so a
    /// failed probe never invents an update.
    /// </summary>
    public async Task<string> ResolveLatestVersionAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!PluginCatalog.IsTrustedAssetUrl(entry.DownloadUrl))
            return "";

        var uri = new Uri(entry.DownloadUrl);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (ReleaseVersionResolver.TryExtractTag(uri, out var tag))
                return tag;

            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.Headers.Location is not { } location)
                return "";

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!PluginCatalog.IsTrustedRedirectTarget(uri))
                return "";
        }

        return "";
    }

    /// <summary>
    /// Installs or reinstalls a plugin: download to a staging folder, unpack, then swap into place.
    /// Any existing install is moved aside rather than deleted, and put back if the swap fails — so a
    /// plugin whose files are locked (it is still running, a scanner has the exe open) leaves the
    /// working copy in place instead of losing it between the delete and the move.
    /// </summary>
    /// <param name="entry">Catalog entry to install.</param>
    /// <param name="progress">Percent complete of the download, when the server reports a length.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public async Task<InstalledPlugin> InstallAsync(
        CatalogEntry entry,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        // Re-checked here rather than trusted from the row the user clicked: this is the last point
        // before bytes are fetched, and it is the only check an install can never route around.
        if (!PluginCatalog.IsValidId(entry.Id) || !PluginCatalog.IsTrustedAssetUrl(entry.DownloadUrl))
            throw new InvalidOperationException($"'{entry.Name}' is not published by the plugins repository and will not be downloaded.");

        var version = await ResolveLatestVersionAsync(entry, cancellationToken);
        var destination = PluginPaths.PluginDirectory(_root, entry.Id);
        var staging = Path.Combine(_root, PluginPaths.StagingDirectoryName, $"{entry.Id}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            var archivePath = Path.Combine(staging, StagedArchiveName);
            await DownloadAsync(new Uri(entry.DownloadUrl), archivePath, progress, cancellationToken);

            var payload = Path.Combine(staging, StagedPayloadName);
            string executable;
            await using (var archive = File.OpenRead(archivePath))
                executable = PluginArchive.Extract(archive, payload);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SwapIntoPlace(payload, destination, Path.Combine(staging, ReplacedDirectoryName));

            var installed = new InstalledPlugin(
                entry.Id,
                entry.Name,
                version.Length > 0 ? version : "unknown",
                Path.Combine(destination, Path.GetRelativePath(payload, executable)),
                DateTimeOffset.UtcNow,
                entry.DownloadUrl,
                entry.Channel);

            State.Set(installed);
            State.Save();
            return installed;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Deletes a plugin's folder and forgets it. The caller stops the process first; a running plugin
    /// holds its exe open and the delete would fail halfway.
    /// </summary>
    public void Uninstall(string id)
    {
        var directory = PluginPaths.PluginDirectory(_root, id);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        if (State.Remove(id))
            State.Save();
    }

    public void Dispose() => _http.Dispose();

    // Replaces an existing install without ever being in a state where neither copy is there: the old
    // folder moves aside first, and if putting the new one in its place fails, it moves back. Both
    // moves are same-volume renames, so the window where either could fail is as small as the
    // filesystem allows.
    private static void SwapIntoPlace(string payload, string destination, string replaced)
    {
        var hadExisting = Directory.Exists(destination);
        if (hadExisting)
            Directory.Move(destination, replaced);

        try
        {
            Directory.Move(payload, destination);
        }
        catch
        {
            if (hadExisting && !Directory.Exists(destination))
                Directory.Move(replaced, destination);
            throw;
        }
    }

    private async Task DownloadAsync(Uri uri, string path, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetFollowingRedirectsAsync(uri, PluginCatalog.IsTrustedRedirectTarget, cancellationToken);

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long copied = 0;
        var lastReported = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;

            if (total is not > 0)
                continue;

            var percent = (int)(copied * 100 / total.Value);
            if (percent == lastReported)
                continue;

            lastReported = percent;
            progress?.Report(percent);
        }
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        Uri uri,
        Func<Uri, bool> isAllowed,
        CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!isAllowed(next))
                    throw new InvalidOperationException($"The download was redirected to an untrusted host ({next.Host}) and was stopped.");

                uri = next;
                continue;
            }

            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                response.Dispose();
                throw;
            }

            return response;
        }

        throw new InvalidOperationException("The download was redirected too many times.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging leftovers are named with a fresh GUID per attempt, so one that cannot be
            // deleted now costs disk space rather than correctness; never fail an install over it.
        }
    }
}
