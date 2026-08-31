using System.Text.Json.Nodes;
using GameCapture.Engine;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Pins <see cref="EngineConfigSeed"/> in isolation from the filesystem — the per-entry
/// <c>addedIn</c> discipline it borrows from <see cref="GameCapture.Sdk.ConfigSeed"/>, adapted to
/// <c>engine-config.json</c>'s flat scalar shape.
/// </summary>
public sealed class EngineConfigSeedTests
{
    [Fact]
    public void MissingThemeKey_IsAddedAndStamped()
    {
        var result = EngineConfigSeed.ApplyNewDefaults("""{ "hotkey": "Ctrl+Shift+F12" }""");

        var root = (JsonObject)JsonNode.Parse(result)!;
        Assert.Equal("system", root["theme"]!.GetValue<string>());
        Assert.True(root["configVersion"]!.GetValue<int>() >= 1);
        Assert.Equal("Ctrl+Shift+F12", root["hotkey"]!.GetValue<string>());
    }

    [Fact]
    public void ExistingThemeKey_IsNeverOverwritten()
    {
        var result = EngineConfigSeed.ApplyNewDefaults("""{ "theme": "dark" }""");

        var root = (JsonObject)JsonNode.Parse(result)!;
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
    }

    [Fact]
    public void AlreadyStampedFile_DoesNotResurrectARemovedTheme()
    {
        // Stamped at the version that introduced "theme" but the key itself is gone: it was either
        // deliberately removed or never applicable, and re-adding it here would be the exact bug
        // addedIn tagging is meant to prevent (see docs/PLUGIN-AUTHORING.md for the plugin analogue).
        var result = EngineConfigSeed.ApplyNewDefaults("""{ "configVersion": 1 }""");

        var root = (JsonObject)JsonNode.Parse(result)!;
        Assert.False(root.ContainsKey("theme"));
    }

    [Fact]
    public void CaseInsensitiveVersionStamp_IsHonoured()
    {
        var result = EngineConfigSeed.ApplyNewDefaults("""{ "ConfigVersion": 1 }""");

        var root = (JsonObject)JsonNode.Parse(result)!;
        Assert.False(root.ContainsKey("theme"));
    }

    [Fact]
    public void MalformedJson_IsReturnedUnchanged()
    {
        const string malformed = "{ not json";

        Assert.Equal(malformed, EngineConfigSeed.ApplyNewDefaults(malformed));
    }

    [Fact]
    public void NonObjectRoot_IsReturnedUnchanged()
    {
        const string array = "[1, 2, 3]";

        Assert.Equal(array, EngineConfigSeed.ApplyNewDefaults(array));
    }
}
