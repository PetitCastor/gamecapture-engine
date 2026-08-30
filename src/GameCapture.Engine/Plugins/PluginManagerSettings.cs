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

    private PluginManagerSettings(string path) => FilePath = path;

    /// <summary>Whether the manager may fetch and offer public preview plugins.</summary>
    public bool IncludePreviews { get; set; }

    /// <summary>Document that persists this setting.</summary>
    public string FilePath { get; }

    /// <summary>Loads the settings, defaulting safely to stable-only on any read failure.</summary>
    public static PluginManagerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<PluginManagerSettingsDocument>(File.ReadAllText(path), JsonOptions);
                return new PluginManagerSettings(path) { IncludePreviews = parsed?.IncludePreviews ?? false };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preview access is opt-in, so a missing or unreadable preference must never enable it.
        }

        return new PluginManagerSettings(path);
    }

    /// <summary>Writes the current preference, creating the plugin root if required.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new PluginManagerSettingsDocument(IncludePreviews), JsonOptions));
    }
}
