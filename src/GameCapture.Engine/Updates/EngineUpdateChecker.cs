using Velopack;
using Velopack.Sources;

namespace GameCapture.Engine.Updates;

/// <summary>
/// Fire-and-forget update check run once at startup, reporting through <see cref="ConsoleSink"/>
/// alongside the rest of the startup banner. Checking only — it does not download or apply
/// anything; the user (or a future auto-update path) decides what to do with the result.
/// </summary>
/// <remarks>
/// Unpackaged runs (dotnet run, IDE debug, the raw publish zip) are not installed by Velopack's
/// Setup.exe, so <see cref="UpdateManager.IsInstalled"/> is false and the check is skipped —
/// mirroring <c>VelopackApp.Build().Run()</c>'s no-op behavior for the same case in Program.cs.
/// Any failure (offline, GitHub rate limit, DNS) is swallowed: an update check is a courtesy, never
/// a startup dependency.
/// </remarks>
internal static class EngineUpdateChecker
{
    // Same repo release.yml publishes to (releases.win.json + the Setup.exe/-full.nupkg it feeds
    // from) — packId GameCaptureEngine is pinned there as the permanent update-feed identity.
    private const string RepoUrl = "https://github.com/PetitCastor/gamecapture-engine";

    public static async Task CheckAsync(ConsoleSink sink)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled)
                return;

            var update = await manager.CheckForUpdatesAsync();
            if (update is not null)
                sink.WriteLine($"Update available: v{update.TargetFullRelease.Version} (installed v{manager.CurrentVersion}).");
        }
        catch (Exception ex)
        {
            sink.WriteLine($"Update check failed: {ex.Message}");
        }
    }
}
