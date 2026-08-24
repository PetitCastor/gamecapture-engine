using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The seeder's contract is a promise about other people's files: add what they have never been
/// offered, change nothing they already chose, and only ever offer a given default once.
/// </summary>
public class ConfigSeedTests : IDisposable
{
    private const string V1 = "Seed.v1.json";
    private const string V2 = "Seed.v2.json";
    private const string Unversioned = "Seed.unversioned.json";

    private static readonly Assembly Here = typeof(ConfigSeedTests).Assembly;

    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sc-seed-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_(string name = "config.json") => Path.Combine(_dir, name);

    private static string[] OutputTypes(string path)
    {
        var outputs = JsonNode.Parse(File.ReadAllText(path))!["outputs"] as JsonArray
            ?? new JsonArray();
        return [.. outputs.Select(node => node!["type"]!.GetValue<string>())];
    }

    private static int Version(string path)
        => JsonNode.Parse(File.ReadAllText(path))!["configVersion"]!.GetValue<int>();

    [Fact]
    public void FirstRun_WritesTheEmbeddedDefaultVerbatim()
    {
        var path = Path_();

        ConfigSeed.Ensure(Here, V2, path);

        using var stream = Here.GetManifestResourceStream(V2)!;
        Assert.Equal(new StreamReader(stream).ReadToEnd(), File.ReadAllText(path));
    }

    [Fact]
    public void FirstRun_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "config.json");

        ConfigSeed.Ensure(Here, V1, path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Ensure_ReturnsThePathItWasGiven()
    {
        var path = Path_();

        Assert.Equal(path, ConfigSeed.Ensure(Here, V1, path));
    }

    [Fact]
    public void MissingResource_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigSeed.Ensure(Here, "Seed.does-not-exist.json", Path_()));

        Assert.Contains("Seed.does-not-exist.json", ex.Message);
    }

    /// <summary>The whole point: the overlay output that a v1-seeded user never saw.</summary>
    [Fact]
    public void VersionBump_AddsTheOutputTheUserNeverHad()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        Assert.Equal(["json"], OutputTypes(path));

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json", "overlay"], OutputTypes(path));
        Assert.Equal(2, Version(path));
    }

    [Fact]
    public void VersionBump_PreservesValuesTheUserEdited()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        var edited = JsonNode.Parse(File.ReadAllText(path))!;
        edited["pipeName"] = "MyOwnPipe";
        edited["saveDebugFrames"] = true;
        edited["outputs"]![0]!["dedupeOnChange"] = false;
        File.WriteAllText(path, edited.ToJsonString());

        ConfigSeed.Ensure(Here, V2, path);

        var merged = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("MyOwnPipe", merged["pipeName"]!.GetValue<string>());
        Assert.True(merged["saveDebugFrames"]!.GetValue<bool>());
        Assert.False(merged["outputs"]![0]!["dedupeOnChange"]!.GetValue<bool>());
        Assert.Equal(["json", "overlay"], OutputTypes(path));
    }

    /// <summary>
    /// The property that makes the whole design safe: refusing a default has to be possible, so a
    /// deletion after the offer must survive every later run at the same version.
    /// </summary>
    [Fact]
    public void DeletingAnOfferedDefault_StaysDeleted()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        ConfigSeed.Ensure(Here, V2, path);

        var trimmed = JsonNode.Parse(File.ReadAllText(path))!;
        (trimmed["outputs"] as JsonArray)!.RemoveAt(1);
        File.WriteAllText(path, trimmed.ToJsonString());

        ConfigSeed.Ensure(Here, V2, path);
        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json"], OutputTypes(path));
    }

    [Fact]
    public void SameVersion_LeavesTheFileByteIdentical()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V2, path);
        var before = File.ReadAllText(path);

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>A plugin that never opted in keeps the old first-run-only behaviour exactly.</summary>
    [Fact]
    public void UnversionedDefault_NeverTouchesAnExistingFile()
    {
        var path = Path_();
        File.WriteAllText(path, """{"pipeName":"Mine","outputs":[]}""");

        ConfigSeed.Ensure(Here, Unversioned, path);

        Assert.Equal("""{"pipeName":"Mine","outputs":[]}""", File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableJson_IsLeftForTheLoaderToReport()
    {
        var path = Path_();
        File.WriteAllText(path, "{ this is not json");

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal("{ this is not json", File.ReadAllText(path));
    }

    /// <summary>
    /// A repointed sink is a choice, not an absence. Matching on <c>type</c> alone is what stops a
    /// second json sink appearing beside the user's and quietly double-writing every record.
    /// </summary>
    [Fact]
    public void RepointedSink_DoesNotGetTheStockOneAddedBackBesideIt()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        var edited = JsonNode.Parse(File.ReadAllText(path))!;
        edited["outputs"]![0]!["path"] = "somewhere/else.jsonl";
        File.WriteAllText(path, edited.ToJsonString());

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json", "overlay"], OutputTypes(path));
        Assert.Equal("somewhere/else.jsonl",
            JsonNode.Parse(File.ReadAllText(path))!["outputs"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void MissingOutputsList_TakesTheEmbeddedOneWholesale()
    {
        var path = Path_();
        File.WriteAllText(path, """{"configVersion":1,"pipeName":"Mine"}""");

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json", "overlay"], OutputTypes(path));
        Assert.Equal("Mine", JsonNode.Parse(File.ReadAllText(path))!["pipeName"]!.GetValue<string>());
    }

    /// <summary>
    /// PluginConfig reads case-insensitively, so a file using different casing is valid — and must
    /// not come back with two keys differing only in case.
    /// </summary>
    [Fact]
    public void DifferentlyCasedKeys_AreUpdatedInPlaceNotDuplicated()
    {
        var path = Path_();
        File.WriteAllText(path, """{"ConfigVersion":1,"Outputs":[{"type":"json"}]}""");

        ConfigSeed.Ensure(Here, V2, path);

        var keys = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)!.Select(pair => pair.Key);
        Assert.Single(keys, key => key.Equals("outputs", StringComparison.OrdinalIgnoreCase));
        Assert.Single(keys, key => key.Equals("configVersion", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["json", "overlay"], OutputTypes_CaseInsensitive(path));
    }

    private static string[] OutputTypes_CaseInsensitive(string path)
    {
        var root = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)!;
        var key = root.First(pair => pair.Key.Equals("outputs", StringComparison.OrdinalIgnoreCase)).Key;
        return [.. (root[key] as JsonArray)!.Select(node => node!["type"]!.GetValue<string>())];
    }

    /// <summary>The seeded file must still load, which is the only reason it exists.</summary>
    [Fact]
    public void MergedFile_StillLoadsThroughPluginConfig()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        ConfigSeed.Ensure(Here, V2, path);

        var config = PluginConfig.Load<SeedTestConfig>(path);

        Assert.Equal(2, config.ConfigVersion);
        Assert.Equal(["json", "overlay"], config.Outputs.Select(output => output.Type).ToArray());
    }

    [Fact]
    public void EnsureInLocalAppData_LandsUnderTheConventionalPath()
    {
        var folder = $"SeedTestPlugin-{Guid.NewGuid():N}";
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameCapture", folder, "config.json");

        try
        {
            var actual = ConfigSeed.EnsureInLocalAppData(Here, V1, folder);

            Assert.Equal(expected, actual);
            Assert.True(File.Exists(expected));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(expected)!, recursive: true);
        }
    }

    private sealed class SeedTestConfig : PluginConfig;
}
