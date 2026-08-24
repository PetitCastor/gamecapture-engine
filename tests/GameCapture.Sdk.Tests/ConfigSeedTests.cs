using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The seeder's contract is a promise about other people's files: add what they have never been
/// offered, change nothing they already chose, and never reconsider a default once it has been put
/// in front of them.
/// </summary>
public class ConfigSeedTests : IDisposable
{
    private const string V1 = "Seed.v1.json";
    private const string V2 = "Seed.v2.json";
    private const string V3 = "Seed.v3.json";
    private const string Untagged = "Seed.untagged.json";
    private const string Malformed = "Seed.malformed.json";
    private const string Unversioned = "Seed.unversioned.json";

    private static readonly Assembly Here = typeof(ConfigSeedTests).Assembly;

    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sc-seed-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_(string name = "config.json") => Path.Combine(_dir, name);

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static string[] OutputTypes(string path)
    {
        var outputs = Read(path)["outputs"] as JsonArray ?? new JsonArray();
        return [.. outputs.Select(node => node!["type"]!.GetValue<string>())];
    }

    private static int Version(string path) => Read(path)["configVersion"]!.GetValue<int>();

    /// <summary>Rewrites the file as the user would: edit in place, leave everything else alone.</summary>
    private static void Edit(string path, Action<JsonObject> edit)
    {
        var config = Read(path);
        edit(config);
        File.WriteAllText(path, config.ToJsonString());
    }

    // ---- first run -------------------------------------------------------------------------

    [Fact]
    public void FirstRun_WritesTheDefaultWithoutTheBookkeeping()
    {
        var path = Path_();

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json", "overlay"], OutputTypes(path));
        Assert.Equal(2, Version(path));
        Assert.DoesNotContain("addedIn", File.ReadAllText(path));
    }

    [Fact]
    public void FirstRun_KeepsEveryValueTheDefaultShipped()
    {
        var path = Path_();

        ConfigSeed.Ensure(Here, V2, path);

        var json = (Read(path)["outputs"] as JsonArray)![0]!;
        Assert.Equal("captures/records.jsonl", json["path"]!.GetValue<string>());
        Assert.True(json["dedupeOnChange"]!.GetValue<bool>());
    }

    [Fact]
    public void FirstRun_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "config.json");

        ConfigSeed.Ensure(Here, V1, path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FirstRun_LeavesNoTemporaryFileBehind()
    {
        var path = Path_();

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
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

    // ---- offering a new default ------------------------------------------------------------

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
    public void VersionBump_AddsTheNewOutputWithoutItsBookkeeping()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);

        ConfigSeed.Ensure(Here, V2, path);

        Assert.DoesNotContain("addedIn", File.ReadAllText(path));
    }

    [Fact]
    public void VersionBump_AddsAKeyTheUserDoesNotHave()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        Assert.Null(Read(path)["ledgerPath"]);

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal("ledger.csv", Read(path)["ledgerPath"]!.GetValue<string>());
    }

    [Fact]
    public void VersionBump_PreservesValuesTheUserEdited()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        Edit(path, config =>
        {
            config["pipeName"] = "MyOwnPipe";
            config["saveDebugFrames"] = true;
            config["outputs"]![0]!["dedupeOnChange"] = false;
        });

        ConfigSeed.Ensure(Here, V2, path);

        var merged = Read(path);
        Assert.Equal("MyOwnPipe", merged["pipeName"]!.GetValue<string>());
        Assert.True(merged["saveDebugFrames"]!.GetValue<bool>());
        Assert.False(merged["outputs"]![0]!["dedupeOnChange"]!.GetValue<bool>());
        Assert.Equal(["json", "overlay"], OutputTypes(path));
    }

    // ---- offered exactly once --------------------------------------------------------------

    /// <summary>
    /// The property the whole design turns on, and the one a single-step test cannot see: the
    /// deletion has to survive a bump that is not the bump which introduced the entry. v3 still
    /// lists overlay, as any real shipped default would.
    /// </summary>
    [Fact]
    public void DeletedDefault_IsNotReofferedByALaterBump()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        ConfigSeed.Ensure(Here, V2, path);
        Edit(path, config => (config["outputs"] as JsonArray)!.RemoveAt(1));

        ConfigSeed.Ensure(Here, V3, path);

        Assert.Equal(["json", "http"], OutputTypes(path));
        Assert.Equal(3, Version(path));
    }

    [Fact]
    public void DeletedDefault_StaysDeletedAcrossRepeatedRunsAtTheSameVersion()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        ConfigSeed.Ensure(Here, V2, path);
        Edit(path, config => (config["outputs"] as JsonArray)!.RemoveAt(1));

        ConfigSeed.Ensure(Here, V2, path);
        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["json"], OutputTypes(path));
    }

    /// <summary>
    /// Emptying <c>outputs</c> is an ordinary way to turn everything off, and must not read as
    /// "has never been offered anything".
    /// </summary>
    [Fact]
    public void ClearedOutputs_OnlyGetsWhatIsGenuinelyNew()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        Edit(path, config => config["outputs"] = new JsonArray());

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["overlay"], OutputTypes(path));
    }

    [Fact]
    public void SkippingAVersion_StillOffersEverythingSinceTheStamp()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);

        ConfigSeed.Ensure(Here, V3, path);

        Assert.Equal(["json", "overlay", "http"], OutputTypes(path));
        Assert.Equal(3, Version(path));
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

    [Fact]
    public void OlderDefaultThanTheUsersFile_ChangesNothing()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V3, path);
        var before = File.ReadAllText(path);

        ConfigSeed.Ensure(Here, V1, path);

        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>An entry with no <c>addedIn</c> ships to new users but is never merged into old ones.</summary>
    [Fact]
    public void UntaggedOutput_IsNeverOfferedToAnExistingFile()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);

        ConfigSeed.Ensure(Here, Untagged, path);

        Assert.Equal(["json"], OutputTypes(path));
        Assert.Equal(2, Version(path));
    }

    // ---- files it must refuse to touch ------------------------------------------------------

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
    /// Duplicate keys parse without complaint and throw on first access, so this is the difference
    /// between declining the file and throwing on every launch from now on.
    /// </summary>
    [Fact]
    public void DuplicateKeys_AreDeclinedRatherThanThrown()
    {
        var path = Path_();
        File.WriteAllText(path, """{"configVersion":1,"pipeName":"a","pipeName":"b"}""");

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal("""{"configVersion":1,"pipeName":"a","pipeName":"b"}""", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("")]
    public void ARootThatIsNotAnObject_IsLeftAlone(string content)
    {
        var path = Path_();
        File.WriteAllText(path, content);

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void OutputsThatIsNotAList_IsLeftAlone()
    {
        var path = Path_();
        File.WriteAllText(path, """{"configVersion":1,"outputs":"nope"}""");

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal("""{"configVersion":1,"outputs":"nope"}""", File.ReadAllText(path));
    }

    /// <summary>
    /// A packaging typo in the plugin's own default must not take down users whose config is fine.
    /// </summary>
    [Fact]
    public void MalformedEmbeddedDefault_LeavesAnExistingFileAlone()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        var before = File.ReadAllText(path);

        ConfigSeed.Ensure(Here, Malformed, path);

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void MalformedEmbeddedDefault_StillSeedsSoTheAuthorSeesIt()
    {
        var path = Path_();

        ConfigSeed.Ensure(Here, Malformed, path);

        using var stream = Here.GetManifestResourceStream(Malformed)!;
        Assert.Equal(new StreamReader(stream).ReadToEnd(), File.ReadAllText(path));
    }

    // ---- shape of the file it writes --------------------------------------------------------

    [Fact]
    public void MissingOutputsList_TakesOnlyWhatIsNewSinceTheStamp()
    {
        var path = Path_();
        File.WriteAllText(path, """{"configVersion":1,"pipeName":"Mine"}""");

        ConfigSeed.Ensure(Here, V2, path);

        Assert.Equal(["overlay"], OutputTypes(path));
        Assert.Equal("Mine", Read(path)["pipeName"]!.GetValue<string>());
    }

    /// <summary>
    /// A sink the user added themselves under a type a later default also uses must not end up
    /// doubled — two sinks writing the same records to different places.
    /// </summary>
    [Fact]
    public void UserAddedSinkOfTheSameType_DoesNotGetTheDefaultAddedBesideIt()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        Edit(path, config => (config["outputs"] as JsonArray)!.Add(new JsonObject
        {
            ["type"] = "http",
            ["url"] = "https://mine.invalid/records",
        }));

        ConfigSeed.Ensure(Here, V3, path);

        Assert.Equal(["json", "http", "overlay"], OutputTypes(path));
        Assert.Equal("https://mine.invalid/records",
            (Read(path)["outputs"] as JsonArray)![1]!["url"]!.GetValue<string>());
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

        var keys = Read(path).Select(pair => pair.Key).ToArray();
        Assert.Single(keys, key => key.Equals("outputs", StringComparison.OrdinalIgnoreCase));
        Assert.Single(keys, key => key.Equals("configVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergedFile_StillLoadsThroughPluginConfig()
    {
        var path = Path_();
        ConfigSeed.Ensure(Here, V1, path);
        ConfigSeed.Ensure(Here, V3, path);

        var config = PluginConfig.Load<SeedTestConfig>(path);

        Assert.Equal(3, config.ConfigVersion);
        Assert.Equal(["json", "overlay", "http"], config.Outputs.Select(output => output.Type).ToArray());
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
