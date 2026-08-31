using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameCapture.Engine;

/// <summary>
/// Offers <c>engine-config.json</c> keys added after a user's file was written, the same way
/// <see cref="GameCapture.Sdk.ConfigSeed"/> offers new plugin config defaults — adapted to this
/// file's flat scalar shape (there is no <c>outputs</c>-style array to merge into).
/// </summary>
/// <remarks>
/// Each entry below is tagged with the version it first appeared in, and a merge only ever adds
/// entries newer than the version stamped on the user's file; it never looks at whether the key is
/// currently present. Comparing shipped defaults against what the user currently has would read
/// "deliberately removed" and "never offered" as the same state, and hand a declined default straight
/// back on the very next bump — see docs/PLUGIN-AUTHORING.md for the plugin-side write-up of that
/// exact bug. <c>configVersion</c> is bookkeeping only: <see cref="EngineConfig"/> does not declare a
/// matching property, so it round-trips as an ordinary (ignored) JSON property once stamped.
/// </remarks>
internal static class EngineConfigSeed
{
    private const string VersionProperty = "configVersion";

    private static readonly (string Key, int AddedIn, JsonNode Default)[] Entries =
    [
        ("theme", 1, JsonValue.Create("system")!),
    ];

    private static readonly int CurrentVersion = Entries.Length == 0 ? 0 : Entries.Max(entry => entry.AddedIn);

    /// <summary>
    /// Adds any key whose <c>addedIn</c> exceeds the version stamped on <paramref name="json"/> and
    /// that the file does not already have, then restamps to <see cref="CurrentVersion"/>. Returns
    /// <paramref name="json"/> unchanged when it cannot be parsed as a JSON object — an unreadable
    /// file is not this class's to rewrite; <see cref="EngineConfig.Load"/> surfaces the problem with
    /// the file intact.
    /// </summary>
    public static string ApplyNewDefaults(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (parsed is not JsonObject root)
            return json;

        var stamped = ReadVersion(root);
        if (stamped >= CurrentVersion)
            return json;

        foreach (var (key, addedIn, defaultValue) in Entries)
        {
            if (addedIn <= stamped || FindKey(root, key) is not null)
                continue;

            root[key] = defaultValue.DeepClone();
        }

        // Stamped even when nothing was added: the stamp records how far this file has been brought
        // forward, and every later merge reads it to know what it must not revisit.
        Set(root, VersionProperty, CurrentVersion);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static int ReadVersion(JsonObject root)
        => FindKey(root, VersionProperty) is { } key
            && root[key] is JsonValue value
            && value.TryGetValue(out int stamped)
                ? stamped
                : 0;

    private static string? FindKey(JsonObject obj, string name)
        => obj.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static void Set(JsonObject obj, string name, JsonNode? value)
        => obj[FindKey(obj, name) ?? name] = value;
}
