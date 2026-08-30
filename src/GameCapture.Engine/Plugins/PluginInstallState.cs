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

    private readonly Dictionary<string, InstalledPlugin> _entries;

    private PluginInstallState(string path, Dictionary<string, InstalledPlugin> entries)
    {
        FilePath = path;
        _entries = entries;
    }

    /// <summary>Document this state reads from and writes to.</summary>
    public string FilePath { get; }

    /// <summary>Installed plugins, keyed by catalog id.</summary>
    public IReadOnlyDictionary<string, InstalledPlugin> Entries => _entries;

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
    public void Set(InstalledPlugin plugin) => _entries[plugin.Id] = plugin;

    /// <summary>Drops an entry. Returns whether there was one.</summary>
    public bool Remove(string id) => _entries.Remove(id);

    /// <summary>Looks up an installed plugin by catalog id.</summary>
    public bool TryGet(string id, out InstalledPlugin plugin) => _entries.TryGetValue(id, out plugin!);

    /// <summary>Writes the document, creating the plugins root if this is the first install.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var ordered = _entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(ordered, JsonOptions));
    }
}
