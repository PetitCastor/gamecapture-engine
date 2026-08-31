using System.Windows.Forms;
using Velopack;
using Velopack.Sources;

namespace GameCapture.Engine.Updates;

/// <summary>
/// Update check run once at startup, before any capture engine component (pipe, tray, plugins)
/// exists, reporting through <see cref="ConsoleSink"/> alongside the rest of the startup banner.
/// On finding a newer release it asks the user (via a plain <see cref="MessageBox"/> — no tray icon
/// exists yet this early in startup) whether to install it; a "yes" downloads and applies the
/// update, which restarts the process on the new version and never returns.
/// </summary>
/// <remarks>
/// <para>
/// Awaited by <c>Program.cs</c> rather than fired-and-forgotten: once accepting restarts the
/// process, letting the network check and the prompt race against a live capture session — plugins
/// connected, pipe bound — would turn "yes" into an abrupt kill instead of a clean startup gate.
/// Blocking here only costs one network round trip plus however long the user takes to answer the
/// dialog, and that cost is paid once, before anything is listening.
/// </para>
/// <para>
/// Unpackaged runs (dotnet run, IDE debug, the raw publish zip) are not installed by Velopack's
/// Setup.exe, so <see cref="UpdateManager.IsInstalled"/> is false and the check is skipped —
/// mirroring <c>VelopackApp.Build().Run()</c>'s no-op behavior for the same case in Program.cs.
/// A failure in the check itself (offline, GitHub rate limit, DNS) is swallowed to the console: an
/// update check is a courtesy, never a startup dependency. A failure <em>after</em> the user has
/// explicitly consented (download/apply) instead surfaces a <see cref="MessageBox"/>, matching
/// <see cref="StartupDiagnostics"/>'s pattern for a launch with no visible console — silently
/// dropping a failure the user is actively waiting on would leave them stuck with no signal that
/// their "yes" didn't do anything.
/// </para>
/// </remarks>
internal static class EngineUpdateChecker
{
    // Same repo release.yml publishes to (releases.win.json + the Setup.exe/-full.nupkg it feeds
    // from) — packId GameCaptureEngine is pinned there as the permanent update-feed identity.
    private const string RepoUrl = "https://github.com/PetitCastor/gamecapture-engine";

    public static async Task CheckAsync(ConsoleSink sink)
    {
        UpdateManager manager;
        UpdateInfo update;
        try
        {
            manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled)
                return;

            var found = await manager.CheckForUpdatesAsync();
            if (found is null)
                return;
            update = found;

            sink.WriteLine($"Update available: v{update.TargetFullRelease.Version} (installed v{manager.CurrentVersion}).");
        }
        catch (Exception ex)
        {
            sink.WriteLine($"Update check failed: {ex.Message}");
            return;
        }

        var accepted = MessageBox.Show(
            $"GameCapture v{update.TargetFullRelease.Version} is available (installed v{manager.CurrentVersion}).\n\nInstall now? The engine will restart.",
            "GameCapture update available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        if (!accepted)
        {
            sink.WriteLine("Update declined; continuing on the current version.");
            return;
        }

        try
        {
            sink.WriteLine("Downloading update…");
            await manager.DownloadUpdatesAsync(update);

            sink.WriteLine("Update downloaded; restarting to apply.");
            manager.ApplyUpdatesAndRestart(update); // exits this process and relaunches on the new version
        }
        catch (Exception ex)
        {
            sink.WriteLine($"Update install failed: {ex.Message}");
            MessageBox.Show(
                $"Could not install the update:\n{ex.Message}\n\nGameCapture will continue on the current version.",
                "GameCapture update failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
