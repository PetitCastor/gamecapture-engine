using System.Text.Json;

namespace GameCapture.Engine.Plugins;

/// <summary>Per-user plugin-manager preferences that take effect without restarting the engine.</summary>
public sealed class PluginManagerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Auto-start is on by default, so what is persisted is the exceptions: an id absent from this set
    // starts with the engine. Storing the opt-outs rather than a per-id flag means a freshly installed
    // plugin needs no write at all to get the default, and a plugin removed and reinstalled does not
    // silently inherit a decision the user made about an earlier install of the same id.
    private readonly HashSet<string> _autoStartDisabled;

    // Guards the set only. IncludePreviews stays a plain property (a single bool torn from no writer),
    // but the control API can toggle auto-start for one id while a row build enumerates the set.
    private readonly Lock _gate = new();

    private PluginManagerSettings(string path, IEnumerable<string>? autoStartDisabled = null)
    {
        FilePath = path;
        _autoStartDisabled = new HashSet<string>(autoStartDisabled ?? [], StringComparer.Ordinal);
    }

    /// <summary>Whether the manager may fetch and offer public preview plugins.</summary>
    public bool IncludePreviews { get; set; }

    /// <summary>Document that persists this setting.</summary>
    public string FilePath { get; }

    /// <summary>Ids the user has opted out of auto-start for; every other installed plugin starts
    /// with the engine.</summary>
    public IReadOnlyCollection<string> AutoStartDisabledIds
    {
        get
        {
            lock (_gate)
                return _autoStartDisabled.ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Loads the settings, defaulting safely to stable-only on any read failure.</summary>
    public static PluginManagerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<PluginManagerSettingsDocument>(File.ReadAllText(path), JsonOptions);
                return new PluginManagerSettings(path, parsed?.AutoStartDisabled)
                {
                    IncludePreviews = parsed?.IncludePreviews ?? false,
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preview access is opt-in, so a missing or unreadable preference must never enable it.
            // Auto-start defaults the other way, and an unreadable file is not evidence that the user
            // opted a plugin out — a plugin the engine starts that the user did not want is one visible
            // Stop click away, whereas silently not starting a plugin looks like the plugin is broken.
        }

        return new PluginManagerSettings(path);
    }

    /// <summary>Whether <paramref name="id"/> is launched when the engine starts. True unless the
    /// user turned it off.</summary>
    public bool IsAutoStartEnabled(string id)
    {
        lock (_gate)
            return !_autoStartDisabled.Contains(id);
    }

    /// <summary>
    /// Records and persists the auto-start choice for one plugin, restoring the previous value if the
    /// write fails.
    /// </summary>
    /// <remarks>
    /// One call rather than the mutate-then-<see cref="Save"/> pair <see cref="IncludePreviews"/> uses,
    /// because this is the per-row setting: every plugin has its own checkbox, so two of them (or the
    /// same one, double-clicked) can be in flight at once. Holding the write inside the same lock as
    /// the mutation is what stops the slower call persisting a snapshot taken before the faster one —
    /// a lost update that would leave disk disagreeing with what both callers were told.
    /// </remarks>
    public void SetAutoStart(string id, bool enabled)
    {
        lock (_gate)
        {
            var wasDisabled = _autoStartDisabled.Contains(id);
            if (enabled)
                _autoStartDisabled.Remove(id);
            else
                _autoStartDisabled.Add(id);

            try
            {
                WriteLocked();
            }
            catch
            {
                // The caller is about to be told the write failed, so memory must not keep a preference
                // the next engine start (which reads the file) would not honour.
                if (wasDisabled)
                    _autoStartDisabled.Add(id);
                else
                    _autoStartDisabled.Remove(id);
                throw;
            }
        }
    }

    /// <summary>Writes the current preferences, creating the plugin root if required.</summary>
    public void Save()
    {
        lock (_gate)
            WriteLocked();
    }

    // Called with _gate held: the snapshot and the write are one step, so a concurrent writer cannot
    // land between them.
    private void WriteLocked()
    {
        var document = new PluginManagerSettingsDocument(
            IncludePreviews,
            [.. _autoStartDisabled.Order(StringComparer.Ordinal)]);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, JsonOptions));
    }
}
