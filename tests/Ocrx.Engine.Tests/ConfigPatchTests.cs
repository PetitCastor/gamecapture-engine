using System.Text.Json;
using System.Text.Json.Nodes;
using Ocrx.Engine;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// Pins <see cref="ConfigPatch.Apply"/>: the tray persists one setting by patching the config JSON in
/// place, so untouched properties — above all a relative <c>outputDir</c> — must survive verbatim, and
/// changed ones must take the new value.
/// </summary>
public class ConfigPatchTests
{
    private const string Sample = """
        {
          "hotkey": "Ctrl+Shift+F12",
          "monitorIndex": 0,
          "outputDir": "captures",
          "ocrLanguage": "",
          "scanIntervalMs": 500,
          "trayEnabled": true
        }
        """;

    [Fact]
    public void Monitor_only_change_preserves_relative_output_dir_and_other_fields()
    {
        var result = ConfigPatch.Apply(Sample, new Dictionary<string, object> { ["monitorIndex"] = 2 });
        var node = JsonNode.Parse(result)!.AsObject();

        Assert.Equal(2, (int)node["monitorIndex"]!);
        Assert.Equal("captures", (string)node["outputDir"]!); // still relative, not resolved to absolute
        Assert.Equal("Ctrl+Shift+F12", (string)node["hotkey"]!);
        Assert.True((bool)node["trayEnabled"]!);
    }

    [Fact]
    public void Applies_string_and_int_changes_together()
    {
        var result = ConfigPatch.Apply(Sample, new Dictionary<string, object>
        {
            ["outputDir"] = @"D:\dumps",
            ["ocrLanguage"] = "fr-FR",
            ["scanIntervalMs"] = 750,
        });
        var node = JsonNode.Parse(result)!.AsObject();

        Assert.Equal(@"D:\dumps", (string)node["outputDir"]!);
        Assert.Equal("fr-FR", (string)node["ocrLanguage"]!);
        Assert.Equal(750, (int)node["scanIntervalMs"]!);
    }

    [Fact]
    public void Adds_a_key_absent_from_the_source()
    {
        var result = ConfigPatch.Apply("{}", new Dictionary<string, object> { ["monitorIndex"] = 1 });
        var node = JsonNode.Parse(result)!.AsObject();
        Assert.Equal(1, (int)node["monitorIndex"]!);
    }

    [Fact]
    public void Applies_a_bool_change()
    {
        var result = ConfigPatch.Apply(Sample, new Dictionary<string, object> { ["trayEnabled"] = false });
        var node = JsonNode.Parse(result)!.AsObject();

        Assert.False((bool)node["trayEnabled"]!);
        Assert.Equal(JsonValueKind.False, node["trayEnabled"]!.GetValueKind()); // a real JSON literal, not "false"
    }

    [Fact]
    public void Rejects_an_unsupported_value_type()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigPatch.Apply(Sample, new Dictionary<string, object> { ["scanIntervalMs"] = 1.5 }));
    }

    [Fact]
    public void Rejects_a_valid_but_non_object_root()
    {
        // A corrupted/truncated write can leave a valid JSON array/scalar; that must be a clear
        // ArgumentException, not the raw InvalidOperationException AsObject() would throw.
        Assert.Throws<ArgumentException>(() =>
            ConfigPatch.Apply("[1, 2, 3]", new Dictionary<string, object> { ["monitorIndex"] = 0 }));
    }

    [Fact]
    public void Null_root_starts_from_an_empty_object()
    {
        var result = ConfigPatch.Apply("null", new Dictionary<string, object> { ["monitorIndex"] = 3 });
        var node = JsonNode.Parse(result)!.AsObject();
        Assert.Equal(3, (int)node["monitorIndex"]!);
    }
}
