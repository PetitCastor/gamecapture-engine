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
    /// property name. Values must be <see cref="string"/>, <see cref="int"/> or <see cref="bool"/>. A key
    /// not already present is added; every other property is preserved untouched.
    /// </summary>
    public static string Apply(string json, IReadOnlyDictionary<string, object> changes)
    {
        // Parse to null (literal "null" or empty) starts from an empty object; a valid-but-non-object
        // root (array/number/string — a corrupted or truncated write) is a clear, catchable error rather
        // than the raw InvalidOperationException AsObject() would throw.
        var parsed = JsonNode.Parse(json);
        if (parsed is not null and not JsonObject)
            throw new ArgumentException($"Config root must be a JSON object, was {parsed.GetValueKind()}.");
        var root = parsed?.AsObject() ?? new JsonObject();
        foreach (var (key, value) in changes)
        {
            root[key] = value switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                _ => throw new ArgumentException($"Unsupported config value type for '{key}': {value.GetType()}"),
            };
        }
        return root.ToJsonString(WriteOptions);
    }
}
