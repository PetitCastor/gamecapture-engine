using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

/// <summary>
/// Pins the install-state document: it round-trips, and a damaged one degrades to "nothing
/// installed" instead of throwing — a corrupt file must not be able to keep the plugin manager from
/// opening, since reinstalling is how a user would repair it.
/// </summary>
public class PluginInstallStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gc-state-" + Guid.NewGuid().ToString("N"));

    private string StatePath => PluginPaths.StateFile(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static InstalledPlugin Entry(string id, string version = "v1.0.4")
        => new(id, id, version, $@"C:\plugins\{id}\{id}.exe", DateTimeOffset.UnixEpoch);

    [Fact]
    public void SavedEntries_ComeBackOnLoad()
    {
        var state = PluginInstallState.Load(StatePath);
        state.Set(Entry("mission-plugin"));
        state.Set(Entry("refinery-plugin", "v2.0.0"));
        state.Save();

        var reloaded = PluginInstallState.Load(StatePath);

        Assert.Equal(2, reloaded.Entries.Count);
        Assert.True(reloaded.TryGet("refinery-plugin", out var refinery));
        Assert.Equal("v2.0.0", refinery.Version);
    }

    [Fact]
    public void SavedClientName_ComesBackOnLoad()
    {
        var state = PluginInstallState.Load(StatePath);
        state.Set(new InstalledPlugin("mission-plugin", "MissionPlugin", "v1.0.4", "MissionPlugin.exe",
            DateTimeOffset.UnixEpoch, ClientName: "missions"));
        state.Save();

        Assert.True(PluginInstallState.Load(StatePath).TryGet("mission-plugin", out var installed));
        Assert.Equal("missions", installed.ClientName);
    }

    [Fact]
    public void ReinstallingReplacesTheEntryRatherThanAddingOne()
    {
        var state = PluginInstallState.Load(StatePath);
        state.Set(Entry("mission-plugin", "v1.0.0"));
        state.Set(Entry("mission-plugin", "v1.1.0"));
        state.Save();

        var reloaded = PluginInstallState.Load(StatePath);

        Assert.Equal("v1.1.0", Assert.Single(reloaded.Entries).Value.Version);
    }

    [Fact]
    public void RemoveThenSave_ForgetsThePlugin()
    {
        var state = PluginInstallState.Load(StatePath);
        state.Set(Entry("mission-plugin"));
        state.Save();

        Assert.True(state.Remove("mission-plugin"));
        Assert.False(state.Remove("mission-plugin"));
        state.Save();

        Assert.Empty(PluginInstallState.Load(StatePath).Entries);
    }

    [Fact]
    public void MissingDocument_LoadsAsNothingInstalled()
        => Assert.Empty(PluginInstallState.Load(StatePath).Entries);

    [Fact]
    public void CorruptDocument_LoadsAsNothingInstalledInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, "{ this is not the document");

        Assert.Empty(PluginInstallState.Load(StatePath).Entries);
    }

    [Fact]
    public async Task ConcurrentSetAndSave_ForDifferentIds_NeverThrowsOrCorruptsTheDocument()
    {
        // Reproduces the HIGH finding from the PR #56 review: before PluginInstallState took its own
        // lock, two different-id Set/Save pairs running on separate threads (as the control API now
        // allows — one HTTP request per plugin id, no longer serialized by the WinForms tray being
        // single-threaded) could tear the Dictionary mid-enumeration or interleave two non-atomic
        // File.WriteAllText calls into a corrupt installed.json.
        var state = PluginInstallState.Load(StatePath);
        var ids = Enumerable.Range(0, 20).Select(i => $"plugin-{i}").ToArray();

        await Task.WhenAll(ids.Select(id => Task.Run(() =>
        {
            state.Set(Entry(id));
            state.Save();
        })));

        var reloaded = PluginInstallState.Load(StatePath);
        Assert.Equal(ids.Length, reloaded.Entries.Count);
        foreach (var id in ids)
            Assert.True(reloaded.TryGet(id, out _));
    }

    [Fact]
    public void LegacyDocument_DefaultsMissingChannelAndUrlToStable()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, """
            [{ "id": "mission-plugin", "name": "MissionPlugin", "version": "v1.0.4",
               "executablePath": "C:\\plugins\\mission-plugin\\MissionPlugin.exe", "installedUtc": "1970-01-01T00:00:00+00:00" }]
            """);

        Assert.True(PluginInstallState.Load(StatePath).TryGet("mission-plugin", out var installed));
        Assert.Equal(ReleaseChannel.Stable, installed.Channel);
        Assert.Equal("", installed.DownloadUrl);
        Assert.Equal("", installed.ClientName);
    }
}
