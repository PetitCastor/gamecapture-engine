using System.Text.Json;
using System.Text.Json.Serialization;
using GameCapture.Contracts;
using GameCapture.Engine.Tray;

namespace GameCapture.Engine;

/// <summary>
/// Engine-side settings only. Everything a plugin decides for itself — which trackers run,
/// where the ledger lives — stays out: the engine is game-agnostic and must not grow knobs
/// that describe what is being tracked.
/// </summary>
public sealed class EngineConfig
{
    private const string ConfigFileName = "engine-config.json";
    private const string ConfigDirectoryName = "GameCapture";

    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Index into the monitor list printed at startup (primary monitor is always index 0).</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Where frame dumps land. Relative paths resolve against this config file's directory.</summary>
    public string OutputDir { get; set; } = "captures";

    /// <summary>
    /// BCP-47 tag of the OCR recognizer, e.g. "en-US". Empty means "first Windows display
    /// language that has an OCR pack". Windows OCR has no image-based language detection, so
    /// set this when the game's UI language differs from the Windows display language.
    /// </summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>Named pipe the gRPC server listens on; plugins must use the same name.</summary>
    public string PipeName { get; set; } = PipeContract.DefaultPipeName;

    /// <summary>Scan cadence for the capture loop; values below 100 are clamped up at use.</summary>
    public int ScanIntervalMs { get; set; } = 500;

    /// <summary>Live CPU/memory/GPU status bar at the bottom of the console.</summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>Status bar refresh cadence; values below 250 are clamped up at use.</summary>
    public int MetricsIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Show the Windows tray icon (live capture only). Reflects engine state, metrics and connected
    /// plugins; the debug-only status popup depends on <see cref="MetricsEnabled"/> for its
    /// process-health line.
    /// </summary>
    public bool TrayEnabled { get; set; } = true;

    /// <summary>
    /// UI theme for the WebView2 main window (TASK-UI-05 applies it live). Serialized as
    /// <c>"system"</c>/<c>"light"</c>/<c>"dark"</c>, case-insensitively; an absent or unrecognized
    /// value reads as <see cref="EngineTheme.System"/> rather than failing config load.
    /// </summary>
    [JsonConverter(typeof(EngineThemeJsonConverter))]
    public EngineTheme Theme { get; set; } = EngineTheme.System;

    /// <summary>
    /// Whether the "GameCapture is still capturing…" balloon (TASK-UI-04) has ever been shown for
    /// this install. Set once, the first time the main window is closed to tray, and never again —
    /// there is no UI to un-set it, by design.
    /// </summary>
    public bool CloseToTrayNoticeShown { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Returns the per-user path used by the engine's configuration.</summary>
    public static string GetDefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ConfigDirectoryName,
            ConfigFileName);

    /// <summary>
    /// Loads the config, writing a defaults file on first run so the settings are discoverable
    /// without documentation. Same contract as the monolith's ProbeConfig.Load. On every run it also
    /// offers keys added since the file was last stamped (see <see cref="EngineConfigSeed"/>), so a
    /// config predating e.g. <see cref="Theme"/> still gets it written where a user can find it.
    /// </summary>
    public static EngineConfig Load(string path)
    {
        EngineConfig config;
        if (!File.Exists(path))
        {
            config = new EngineConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var seeded = EngineConfigSeed.ApplyNewDefaults(JsonSerializer.Serialize(config, JsonOptions));
            WriteAtomic(path, seeded);
        }
        else
        {
            var text = File.ReadAllText(path);
            var seeded = EngineConfigSeed.ApplyNewDefaults(text);
            if (seeded != text)
            {
                try
                {
                    WriteAtomic(path, seeded);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort: the new default still applies for this run from `seeded` below,
                    // and the next successful load offers it again. A read-only file must not stop
                    // the engine from starting with the config it already successfully read.
                }
            }

            // A read racing another instance's non-atomic write used to be the failure mode here;
            // WriteAtomic above closes that window, but a hand-edited file can still be malformed,
            // and JsonException on its own does not say which file — wrap it the way the other
            // startup-fatal errors in this project name their own cause.
            try
            {
                config = JsonSerializer.Deserialize<EngineConfig>(seeded, JsonOptions)
                         ?? new EngineConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Config file '{path}' is not valid JSON: {ex.Message}", ex);
            }
        }

        if (!Path.IsPathRooted(config.OutputDir))
            config.OutputDir = Path.GetFullPath(config.OutputDir, Path.GetDirectoryName(path)!);

        return config;
    }

    /// <summary>
    /// Writes through a temporary file in the same directory, then replaces. A plain
    /// <c>WriteAllText</c> truncates the target before writing; a crash, kill, or full disk mid-write
    /// would leave an existing user config empty or half-written instead of merely stale. This is a
    /// deliberate parallel of the SDK's <c>ConfigSeed.Write</c> — the engine does not reference
    /// <c>GameCapture.Sdk</c> (see this project's csproj), so the two cannot share the method.
    /// </summary>
    private static void WriteAtomic(string path, string content)
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
}
