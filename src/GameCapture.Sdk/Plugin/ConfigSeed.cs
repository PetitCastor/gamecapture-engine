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
/// editing the binary. So each entry in the embedded <c>outputs</c> array carries an
/// <c>addedIn</c> version, and a merge only ever considers entries newer than the version stamped
/// on the user's file (<see cref="PluginConfig.ConfigVersion"/>). Anything the user has already
/// been offered is never reconsidered, whatever they subsequently did with it.
/// </para>
/// <para>
/// Gating on the entry rather than on the file as a whole is what makes the promise survive more
/// than one bump. Comparing the embedded defaults against what the user currently has would read
/// "deleted" and "never offered" as the same state, and re-add a declined default on the next
/// version after the one that introduced it.
/// </para>
/// <para>
/// The merge is strictly additive — it never changes a value the user already has, and never
/// removes anything. A plugin that has not opted in (no <c>configVersion</c> in its embedded
/// default, i.e. version 0) keeps exactly today's first-run-only behaviour, and so does an
/// individual <c>outputs</c> entry with no <c>addedIn</c>.
/// </para>
/// </remarks>
public static class ConfigSeed
{
    private const string VersionProperty = "configVersion";
    private const string AddedInProperty = "addedIn";
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
            Write(full, Seedable(defaults));
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
        // A malformed embedded default is a packaging bug in the plugin, and one the author will
        // meet head-on through a first run. Bailing here rather than throwing keeps it from also
        // taking down every existing user, whose own config is perfectly good.
        if (TryParseObject(defaults) is not { } embedded)
            return;

        // Version 0 means the plugin never opted in; leave its users on first-run-only behaviour.
        var target = ReadInt(embedded, VersionProperty);
        if (target == 0)
            return;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return;
        }

        // A file the user has hand-edited into something unreadable is not ours to rewrite —
        // silently replacing it is exactly how hand-edited configs disappear. PluginConfig.Load
        // surfaces the problem with the file intact.
        if (TryParseObject(text) is not { } user)
            return;

        var stamped = ReadInt(user, VersionProperty);
        if (stamped >= target)
            return;

        // Outputs present but not a list is a shape we do not understand well enough to edit.
        var outputsKey = FindKey(user, OutputsProperty);
        if (outputsKey is not null && user[outputsKey] is not JsonArray)
            return;

        foreach (var (key, value) in embedded)
        {
            if (Matches(key, VersionProperty))
                continue;

            if (Matches(key, OutputsProperty))
                MergeOutputs(user, value as JsonArray, stamped);
            else if (FindKey(user, key) is null)
                user[key] = value?.DeepClone();
        }

        // Stamped even when nothing was added: the stamp is the record of how far this file has
        // been brought forward, and every later merge reads it to know what it must not revisit.
        Set(user, VersionProperty, target);
        Write(path, user.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// Adds embedded outputs introduced after <paramref name="stamped"/>, leaving every existing
    /// entry untouched.
    /// </summary>
    /// <remarks>
    /// Two independent guards, and both are load-bearing. <c>addedIn</c> is what makes an offer
    /// happen once: an entry at or below the user's stamp has already been put in front of them,
    /// so whether it is currently in their file says nothing this code is entitled to act on. The
    /// <c>type</c> check then stops a genuinely new default from landing beside one the user
    /// happens to have added themselves under the same type — two sinks quietly writing the same
    /// records to different places.
    /// </remarks>
    private static void MergeOutputs(JsonObject user, JsonArray? embeddedOutputs, int stamped)
    {
        if (embeddedOutputs is null)
            return;

        if (FindKey(user, OutputsProperty) is not { } key || user[key] is not JsonArray userOutputs)
        {
            userOutputs = [];
            Set(user, OutputsProperty, userOutputs);
        }

        var present = userOutputs.OfType<JsonObject>()
            .Select(TypeOf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in embeddedOutputs.OfType<JsonObject>())
        {
            if (ReadInt(candidate, AddedInProperty) <= stamped)
                continue;

            if (!present.Add(TypeOf(candidate)))
                continue;

            userOutputs.Add(WithoutBookkeeping(candidate));
        }
    }

    /// <summary>
    /// The shipped default as a first run should land it: <c>addedIn</c> tells this class which
    /// entries are new, and means nothing in a file the user is expected to open and edit. Falls
    /// back to the raw resource text when it cannot be parsed, so a packaging typo still reaches
    /// the author through <see cref="PluginConfig.Load{T}"/> rather than being swallowed here.
    /// </summary>
    private static string Seedable(string defaults)
    {
        if (TryParseObject(defaults) is not { } embedded)
            return defaults;

        if (FindKey(embedded, OutputsProperty) is not { } key || embedded[key] is not JsonArray outputs)
            return defaults;

        var cleaned = new JsonArray();
        foreach (var node in outputs)
            cleaned.Add(node is JsonObject output ? WithoutBookkeeping(output) : node?.DeepClone());

        embedded[key] = cleaned;
        return embedded.ToJsonString(WriteOptions);
    }

    /// <summary><c>addedIn</c> describes the shipped default, not the user's copy of it.</summary>
    private static JsonNode WithoutBookkeeping(JsonObject output)
    {
        var clone = (JsonObject)output.DeepClone();
        if (FindKey(clone, AddedInProperty) is { } key)
            clone.Remove(key);

        return clone;
    }

    /// <summary>
    /// Writes through a temporary file in the same directory, then replaces. <see cref="Ensure"/>
    /// is the only thing here that touches a file the user may have edited, and a plain
    /// <c>WriteAllText</c> truncates before it writes — a crash or a full disk mid-write would
    /// leave them with the empty config this class exists to protect them from.
    /// </summary>
    private static void Write(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Losing the temp file matters less than the exception on its way up.
            }

            throw;
        }
    }

    /// <summary>
    /// Parses to an object, or null for anything this class should decline to touch: malformed
    /// JSON, and a root that is not an object.
    /// </summary>
    /// <remarks>
    /// The key enumeration is not a formality. Duplicate keys parse without complaint and then
    /// throw <see cref="ArgumentException"/> on first access, so forcing that access here is what
    /// turns "the plugin throws on every launch from now on" into the same quiet decline as any
    /// other file it cannot read.
    /// </remarks>
    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
                return null;

            _ = parsed.Select(pair => pair.Key).ToArray();
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string TypeOf(JsonObject output) => ReadString(output, "type") ?? "";

    private static string? ReadString(JsonObject node, string name)
        => Read(node, name, out string? value) ? value : null;

    private static int ReadInt(JsonObject node, string name)
        => Read(node, name, out int value) ? value : 0;

    /// <summary>
    /// Reads a scalar under a case-insensitive key. A value of the wrong JSON type reads as absent
    /// rather than throwing — <c>"configVersion": "2"</c> is a user's typo, not a reason to refuse
    /// to start.
    /// </summary>
    private static bool Read<T>(JsonObject node, string name, out T? value)
    {
        value = default;
        return FindKey(node, name) is { } key
            && node[key] is JsonValue found
            && found.TryGetValue(out value);
    }

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
