using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameCapture.Sdk;

/// <summary>
/// Seeds a user-editable config file from a plugin's embedded default, and offers defaults added
/// since that file was written — each one exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The three plugins each carried the same "write the embedded default if the file is missing"
/// block, and that block has a hole: it only ever runs on first run. A default added later — a new
/// <c>outputs</c> entry, say — never reaches anyone who has already run the plugin once, and
/// nothing says so. <see cref="SinkFactory"/> routes an unknown-to-the-user sink to a no-op without
/// a warning, so the feature is simply absent rather than broken, which is the hardest kind of
/// missing to notice.
/// </para>
/// <para>
/// Rewriting the file unconditionally would fix that and introduce a worse problem: a user who
/// deliberately deleted an output would get it back on every run, with no way to refuse it short of
/// editing the binary. So the merge is gated on <see cref="PluginConfig.ConfigVersion"/>: a bump in
/// the embedded default is what makes this class add anything, and the stamp is written back
/// whether or not anything was added. Each new default therefore gets offered once. Delete it
/// afterwards and it stays deleted, because the stamp already matches.
/// </para>
/// <para>
/// The merge is strictly additive — it never changes a value the user already has, and never
/// removes anything. A plugin that has not opted in (no <c>configVersion</c> in its embedded
/// default, i.e. version 0) keeps exactly today's first-run-only behaviour.
/// </para>
/// </remarks>
public static class ConfigSeed
{
    private const string VersionProperty = "configVersion";
    private const string OutputsProperty = "outputs";
    private const string AppDataFolder = "GameCapture";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Ensures <paramref name="path"/> exists, seeding it from the embedded
    /// <paramref name="resourceName"/> in <paramref name="assembly"/>, then applies any defaults
    /// added since the file was last stamped. Returns <paramref name="path"/> so it can be handed
    /// straight to <see cref="PluginConfig.Load{T}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedded resource is not in the assembly —
    /// a build error (a missing <c>EmbeddedResource</c> item), not a user-fixable condition.</exception>
    public static string Ensure(Assembly assembly, string resourceName, string path)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var defaults = ReadResource(assembly, resourceName);
        if (!File.Exists(full))
        {
            File.WriteAllText(full, defaults);
            return path;
        }

        MergeNewDefaults(full, defaults);
        return path;
    }

    /// <summary>
    /// <see cref="Ensure(Assembly, string, string)"/> against the conventional per-plugin location,
    /// <c>%LOCALAPPDATA%\GameCapture\{pluginFolder}\{fileName}</c> — the path all three plugins were
    /// composing by hand.
    /// </summary>
    public static string EnsureInLocalAppData(Assembly assembly, string resourceName,
        string pluginFolder, string fileName = "config.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolder, pluginFolder, fileName);
        return Ensure(assembly, resourceName, path);
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded config '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Adds anything the embedded default gained since the user's file was stamped, then restamps.
    /// Bails without touching the file whenever it cannot be sure what it would be overwriting.
    /// </summary>
    private static void MergeNewDefaults(string path, string defaults)
    {
        if (JsonNode.Parse(defaults) is not JsonObject embedded)
            return;

        // Version 0 means the plugin never opted in; leave its users on first-run-only behaviour.
        var target = ReadVersion(embedded);
        if (target == 0)
            return;

        JsonObject user;
        try
        {
            // A file the user has hand-edited into invalid JSON is not ours to rewrite — silently
            // replacing it is exactly how hand-edited configs disappear. PluginConfig.Load will
            // surface the parse error with the file intact.
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed)
                return;

            user = parsed;
        }
        catch (JsonException)
        {
            return;
        }

        if (ReadVersion(user) >= target)
            return;

        foreach (var (key, value) in embedded)
        {
            if (Matches(key, VersionProperty))
                continue;

            if (Matches(key, OutputsProperty))
                MergeOutputs(user, value as JsonArray);
            else if (FindKey(user, key) is null)
                user[key] = value?.DeepClone();
        }

        // Stamped even when nothing was added: the stamp records that this version's defaults were
        // offered, which is what makes a later deletion stick.
        Set(user, VersionProperty, target);
        File.WriteAllText(path, user.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// Adds embedded outputs whose <c>type</c> the user has none of, leaving every existing entry
    /// untouched.
    /// </summary>
    /// <remarks>
    /// Identity is the sink's <c>type</c> alone, not type+path/url. A user who repointed the stock
    /// json sink at their own path would otherwise look like someone missing it, and get a second
    /// one added — two sinks quietly writing the same records to different files. Coarse identity
    /// fails the other way instead: a default that adds a second sink of a type the user already
    /// has is skipped. That is the safe direction, and today's defaults are one sink per type.
    /// </remarks>
    private static void MergeOutputs(JsonObject user, JsonArray? embeddedOutputs)
    {
        if (embeddedOutputs is null)
            return;

        var key = FindKey(user, OutputsProperty);
        if (key is null || user[key] is not JsonArray userOutputs)
        {
            // No outputs at all, or something that isn't a list: the embedded list is the only
            // sensible answer, and there is nothing of the user's to lose.
            Set(user, OutputsProperty, embeddedOutputs.DeepClone());
            return;
        }

        var present = userOutputs.OfType<JsonObject>()
            .Select(TypeOf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in embeddedOutputs.OfType<JsonObject>())
            if (present.Add(TypeOf(candidate)))
                userOutputs.Add(candidate.DeepClone());
    }

    private static string TypeOf(JsonObject output)
        => FindKey(output, "type") is { } key && output[key] is JsonValue value
            && value.TryGetValue<string>(out var type)
                ? type
                : "";

    private static int ReadVersion(JsonObject config)
        => FindKey(config, VersionProperty) is { } key && config[key] is JsonValue value
            && value.TryGetValue<int>(out var version)
                ? version
                : 0;

    /// <summary>
    /// Writes under the key's existing casing when there is one. <see cref="PluginConfig"/> reads
    /// case-insensitively, so a file saying <c>"Outputs"</c> loads fine — and adding a second
    /// <c>"outputs"</c> beside it would produce a file that no longer round-trips.
    /// </summary>
    private static void Set(JsonObject config, string name, JsonNode? value)
        => config[FindKey(config, name) ?? name] = value;

    private static string? FindKey(JsonObject config, string name)
        => config.Select(pair => pair.Key).FirstOrDefault(key => Matches(key, name));

    private static bool Matches(string key, string name)
        => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
