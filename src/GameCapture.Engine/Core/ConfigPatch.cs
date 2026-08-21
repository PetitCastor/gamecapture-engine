using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameCapture.Engine;

/// <summary>
/// Surgically updates named properties in the engine's config JSON, leaving every other property
/// exactly as written. The tray persists a single setting and then restarts; patching only the
/// changed keys — rather than reserializing a loaded <see cref="EngineConfig"/> — keeps untouched
/// fields verbatim, most importantly a relative <c>outputDir</c> that <see cref="EngineConfig.Load"/>
/// resolves to an absolute path in memory (reserializing would bake that absolute path to disk).
/// </summary>
public static class ConfigPatch
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Returns <paramref name="json"/> with each <paramref name="changes"/> entry applied by camelCase
    /// property name. Values must be <see cref="string"/> or <see cref="int"/>. A key not already present
    /// is added; every other property is preserved untouched.
    /// </summary>
    public static string Apply(string json, IReadOnlyDictionary<string, object> changes)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        foreach (var (key, value) in changes)
        {
            root[key] = value switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                _ => throw new ArgumentException($"Unsupported config value type for '{key}': {value.GetType()}"),
            };
        }
        return root.ToJsonString(WriteOptions);
    }
}
