using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

/// <summary>
/// Pins <see cref="PluginRowBuilder"/> — the single place that decides what a plugin row says and
/// which actions it offers, so the dialog and the tray's launch entries cannot disagree about it.
/// </summary>
public class PluginRowBuilderTests
{
    private const string TrustedUrl =
        "https://github.com/PetitCastor/gamecapture-plugins/releases/latest/download/MissionPlugin-win-x64.zip";

    private static CatalogEntry Entry(string id = "mission-plugin", string url = TrustedUrl)
        => new(id, "MissionPlugin", "Watches the mission board.", url);

    private static InstalledPlugin Installed(string id = "mission-plugin", string version = "v1.0.4")
        => new(id, "MissionPlugin", version, $@"C:\plugins\{id}\{id}.exe", DateTimeOffset.UnixEpoch);

    private static PluginRow Build(
        CatalogEntry entry,
        InstalledPlugin? installed = null,
        bool running = false,
        string? latest = null)
    {
        var state = installed is null
            ? new Dictionary<string, InstalledPlugin>()
            : new Dictionary<string, InstalledPlugin> { [installed.Id] = installed };
        var versions = latest is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [entry.Id] = latest };

        return Assert.Single(PluginRowBuilder.Build([entry], state, running ? [entry.Id] : [], versions));
    }

    [Fact]
    public void NotOnDisk_OffersInstallOnly()
    {
        var row = Build(Entry());

        Assert.Equal(PluginRowState.NotInstalled, row.State);
        Assert.Equal("Not installed", row.StateText);
        Assert.Equal("Install", row.InstallActionText);
        Assert.True(row.CanInstall);
        Assert.False(row.CanLaunch);
        Assert.False(row.CanRemove);
        Assert.False(row.CanStop);
    }

    [Fact]
    public void InstalledAtTheLatestTag_OffersLaunchAndRemove()
    {
        var row = Build(Entry(), Installed(version: "v1.0.4"), latest: "v1.0.4");

        Assert.Equal(PluginRowState.Installed, row.State);
        Assert.Equal("Installed (v1.0.4)", row.StateText);
        Assert.True(row.CanLaunch);
        Assert.True(row.CanRemove);
        Assert.True(row.CanReinstall);
        Assert.False(row.CanInstall);
    }

    [Fact]
    public void NewerTagPublished_ReadsAsAnUpdate()
    {
        var row = Build(Entry(), Installed(version: "v1.0.4"), latest: "v1.1.0");

        Assert.Equal(PluginRowState.UpdateAvailable, row.State);
        Assert.Equal("Update available (v1.0.4 → v1.1.0)", row.StateText);
        Assert.Equal("Update", row.InstallActionText);
        Assert.True(row.CanInstall);
        Assert.True(row.CanLaunch);
    }

    [Fact]
    public void UnresolvedVersion_NeverInventsAnUpdate()
    {
        // The HEAD probe failed, so nothing is known about the latest release. Installed is the only
        // honest answer; claiming an update would send the user to download a release that may be
        // the one they already have.
        var row = Build(Entry(), Installed(version: "v1.0.4"));

        Assert.Equal(PluginRowState.Installed, row.State);
        Assert.Equal("", row.LatestVersion);
    }

    [Fact]
    public void RunningPlugin_OffersStopAndNothingThatTouchesItsFiles()
    {
        var row = Build(Entry(), Installed(), running: true, latest: "v1.1.0");

        Assert.True(row.IsRunning);
        Assert.True(row.CanStop);
        Assert.False(row.CanInstall);
        Assert.False(row.CanRemove);
        Assert.False(row.CanLaunch);
    }

    [Fact]
    public void RunningAtTheCurrentVersion_SaysSoInsteadOfInstalled()
    {
        var row = Build(Entry(), Installed(version: "v1.0.4"), running: true, latest: "v1.0.4");

        Assert.Equal("Running (v1.0.4)", row.StateText);
    }

    [Fact]
    public void OffRepositoryDownloadUrl_IsBlocked()
    {
        var row = Build(Entry(url: "https://attacker.example/evil.zip"));

        Assert.Equal(PluginRowState.Blocked, row.State);
        Assert.Equal("Blocked (untrusted source)", row.StateText);
        Assert.False(row.CanInstall);
        Assert.False(row.CanReinstall);
    }

    [Fact]
    public void UnusableId_IsBlockedEvenWithATrustedUrl()
    {
        var row = Build(Entry(id: "../escape"));

        Assert.Equal(PluginRowState.Blocked, row.State);
    }

    [Fact]
    public void RowsFollowCatalogOrder()
    {
        var rows = PluginRowBuilder.Build(
            [Entry("refinery-plugin"), Entry("mission-plugin")],
            new Dictionary<string, InstalledPlugin>(),
            [],
            new Dictionary<string, string>());

        Assert.Equal(["refinery-plugin", "mission-plugin"], rows.Select(r => r.Id));
    }
}
