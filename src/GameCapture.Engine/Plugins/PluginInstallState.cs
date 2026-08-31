using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameCapture.Engine.Plugins;

/// <summary>
/// The set of plugins installed for this user, persisted as <c>installed.json</c> in the plugins
/// root. Deliberately a separate document from <c>engine-config.json</c>: the engine config is
/// engine-side settings only and must not grow knobs describing what is being tracked, and a plugin
/// install is also not a change worth restarting the engine to apply.
/// </summary>
public sealed class PluginInstallState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, InstalledPlugin> _entries;

    private PluginInstallState(string path, Dictionary<string, InstalledPlugin> entries)
    {
        FilePath = path;
        _entries = entries;
    }

    /// <summary>Document this state reads from and writes to.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Installed plugins, keyed by catalog id. A fresh snapshot on every access rather than a live
    /// view: the control API can now call <see cref="Set"/>/<see cref="Remove"/> for one plugin id
    /// concurrently with a caller enumerating this for another, and a live dictionary view being
    /// enumerated while mutated throws.
    /// </summary>
    public IReadOnlyDictionary<string, InstalledPlugin> Entries
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, InstalledPlugin>(_entries, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Loads the document, treating a missing or unreadable one as "nothing installed". A corrupt
    /// file must not keep the manager dialog from opening: the folders on disk are the real install,
    /// and reinstalling a plugin rewrites its entry.
    /// </summary>
    public static PluginInstallState Load(string path)
    {
        var entries = new Dictionary<string, InstalledPlugin>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<List<InstalledPlugin>>(File.ReadAllText(path), JsonOptions);
                foreach (var entry in parsed ?? [])
                {
                    if (!string.IsNullOrEmpty(entry.Id))
                        entries[entry.Id] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            entries.Clear();
        }

        return new PluginInstallState(path, entries);
    }

    /// <summary>Records an install or a reinstall, replacing any earlier entry for the same id.</summary>
    public void Set(InstalledPlugin plugin)
    {
        lock (_gate)
            _entries[plugin.Id] = plugin;
    }

    /// <summary>Drops an entry. Returns whether there was one.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
            return _entries.Remove(id);
    }

    /// <summary>Looks up an installed plugin by catalog id.</summary>
    public bool TryGet(string id, out InstalledPlugin plugin)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out plugin!);
    }

    /// <summary>
    /// Writes the document, creating the plugins root if this is the first install. Serialized under
    /// the same lock as <see cref="Set"/>/<see cref="Remove"/> — since it always writes the full
    /// current table rather than a delta, two overlapping install/uninstall calls for different ids
    /// still converge on a correct file, just with a redundant extra write, instead of interleaving
    /// two partial writes into a corrupt one. Goes through a temp file plus rename, the same reason
    /// <c>EngineConfig.WriteAtomic</c> does: a crash or kill mid-write must never leave
    /// <c>installed.json</c> truncated.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var ordered = _entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var content = JsonSerializer.Serialize(ordered, JsonOptions);

            var temp = FilePath + ".tmp";
            try
            {
                File.WriteAllText(temp, content);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a leftover .tmp costs disk space, not correctness.
                }
                throw;
            }
        }
    }
}
