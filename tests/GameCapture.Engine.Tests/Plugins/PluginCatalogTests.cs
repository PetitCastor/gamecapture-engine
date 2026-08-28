using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

/// <summary>
/// Pins <see cref="PluginCatalog"/>: how a catalog document is read, and — the part that matters —
/// which URLs and ids the engine is willing to act on. These rules are the plugin manager's whole
/// security model, so they are asserted rather than left to the dialog.
/// </summary>
public class PluginCatalogTests
{
    private const string ValidCatalog = """
        [
          {
            "id": "mission-plugin",
            "name": "MissionPlugin",
            "description": "Watches the mission board.",
            "downloadUrl": "https://github.com/PetitCastor/gamecapture-plugins/releases/latest/download/MissionPlugin-win-x64.zip"
          }
        ]
        """;

    [Fact]
    public void TryParse_ReadsEveryField()
    {
        Assert.True(PluginCatalog.TryParse(ValidCatalog, out var entries, out var error));

        var entry = Assert.Single(entries);
        Assert.Equal("mission-plugin", entry.Id);
        Assert.Equal("MissionPlugin", entry.Name);
        Assert.Equal("Watches the mission board.", entry.Description);
        Assert.EndsWith("MissionPlugin-win-x64.zip", entry.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal("", error);
    }

    [Fact]
    public void TryParse_DropsIncompleteEntriesButKeepsTheRest()
    {
        const string json = """
            [
              { "id": "", "name": "Nameless", "downloadUrl": "https://github.com/x" },
              { "id": "refinery-plugin", "name": "RefineryPlugin", "description": "d", "downloadUrl": "https://github.com/y" }
            ]
            """;

        Assert.True(PluginCatalog.TryParse(json, out var entries, out _));

        Assert.Equal("refinery-plugin", Assert.Single(entries).Id);
    }

    [Fact]
    public void TryParse_MalformedJson_FailsWithAReason()
    {
        Assert.False(PluginCatalog.TryParse("{ not json", out var entries, out var error));

        Assert.Empty(entries);
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("https://github.com/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    [InlineData("https://github.com/PetitCastor/gamecapture-plugins/releases/download/v1.0.4/A.zip")]
    public void IsTrustedAssetUrl_AcceptsThePluginsRepositoryReleases(string url)
        => Assert.True(PluginCatalog.IsTrustedAssetUrl(url));

    [Theory]
    // Right host, wrong repository.
    [InlineData("https://github.com/attacker/evil/releases/latest/download/A.zip")]
    // Right repository name appended to someone else's path.
    [InlineData("https://github.com/attacker/PetitCastor/gamecapture-plugins/releases/A.zip")]
    // Host that merely ends with an allowed one.
    [InlineData("https://evilgithub.com/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Allowed host in the userinfo of someone else's.
    [InlineData("https://github.com@evil.example/PetitCastor/gamecapture-plugins/releases/A.zip")]
    // Trailing-dot spelling of the same host.
    [InlineData("https://github.com./PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Non-default port.
    [InlineData("https://github.com:8443/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Plaintext transport.
    [InlineData("http://github.com/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Non-release path on the right repository.
    [InlineData("https://github.com/PetitCastor/gamecapture-plugins/raw/master/A.zip")]
    [InlineData("not a url")]
    public void IsTrustedAssetUrl_RejectsEverythingElse(string url)
        => Assert.False(PluginCatalog.IsTrustedAssetUrl(url));

    [Fact]
    public void ContentHosts_AreRedirectTargetsOnlyAndNeverAStartingPoint()
    {
        // Their paths are signed blobs with no repository identity in them, so accepting one straight
        // out of the catalog would accept any file on any repository. Reaching them has to require
        // having followed a release URL that was path-checked first.
        const string blob = "https://objects.githubusercontent.com/github-production-release-asset/1/2?token=abc";

        Assert.False(PluginCatalog.IsTrustedAssetUrl(blob));
        Assert.True(PluginCatalog.IsTrustedRedirectTarget(new Uri(blob)));
        Assert.True(PluginCatalog.IsTrustedRedirectTarget(
            new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")));
    }

    [Theory]
    [InlineData("https://attacker.example/evil.zip")]
    [InlineData("http://objects.githubusercontent.com/1/2")]
    [InlineData("https://evil-objects.githubusercontent.com/1/2")]
    public void IsTrustedRedirectTarget_StillRefusesAnythingOffTheAllowlist(string url)
        => Assert.False(PluginCatalog.IsTrustedRedirectTarget(new Uri(url)));

    [Fact]
    public void ARedirectMayStillLandOnAnotherReleaseUrl()
        => Assert.True(PluginCatalog.IsTrustedRedirectTarget(
            new Uri("https://github.com/PetitCastor/gamecapture-plugins/releases/download/v1.0.4/A.zip")));

    [Fact]
    public void IsCatalogUrl_AcceptsOnlyThePluginsRepositoryRawPath()
    {
        Assert.True(PluginCatalog.IsCatalogUrl(PluginCatalog.CatalogUrl));
        Assert.False(PluginCatalog.IsCatalogUrl("https://raw.githubusercontent.com/attacker/evil/master/plugins.json"));
    }

    [Theory]
    [InlineData("mission-plugin")]
    [InlineData("a")]
    [InlineData("plugin9")]
    public void IsValidId_AcceptsKebabCaseSlugs(string id) => Assert.True(PluginCatalog.IsValidId(id));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("Mission-Plugin")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("plugin.name")]
    public void IsValidId_RejectsAnythingThatCouldLeaveThePluginsFolder(string id)
        => Assert.False(PluginCatalog.IsValidId(id));

    [Fact]
    public void IsValidId_RejectsAnOverlongId()
        => Assert.False(PluginCatalog.IsValidId(new string('a', 65)));
}
