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
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2?token=abc")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")]
    public void IsTrustedAssetUrl_AcceptsThePluginsRepositoryAndItsContentHosts(string url)
        => Assert.True(PluginCatalog.IsTrustedAssetUrl(url));

    [Theory]
    // Right host, wrong repository.
    [InlineData("https://github.com/attacker/evil/releases/latest/download/A.zip")]
    // Right repository name appended to someone else's path.
    [InlineData("https://github.com/attacker/PetitCastor/gamecapture-plugins/releases/A.zip")]
    // Host that merely ends with an allowed one.
    [InlineData("https://evilgithub.com/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Plaintext transport.
    [InlineData("http://github.com/PetitCastor/gamecapture-plugins/releases/latest/download/A.zip")]
    // Non-release path on the right repository.
    [InlineData("https://github.com/PetitCastor/gamecapture-plugins/raw/master/A.zip")]
    [InlineData("not a url")]
    public void IsTrustedAssetUrl_RejectsEverythingElse(string url)
        => Assert.False(PluginCatalog.IsTrustedAssetUrl(url));

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
