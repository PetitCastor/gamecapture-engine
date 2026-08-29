namespace GameCapture.Engine;

/// <summary>
/// Prepares the argument list for the self-restart the tray triggers after it persists a settings
/// change. The tray writes its choice to <c>engine-config.json</c>; on relaunch the config must win,
/// so any CLI flag that would re-override the very fields the tray just changed is dropped.
/// </summary>
public static class EngineRelaunch
{
    // Flags that carry a value and shadow a tray-editable config field. Each is a "<flag> <value>"
    // pair, so a match removes two tokens. Every other flag (--verbose, --save-frames, the
    // --video/--replay batch knobs) is preserved verbatim — a relaunch is the same run, minus only
    // the overrides the operator has now superseded from the tray.
    private static readonly string[] ValuedOverrides = ["--monitor", "--ocr-lang", "--pipe"];

    /// <summary>
    /// Whether <paramref name="processPath"/> (typically <see cref="Environment.ProcessPath"/>) is the
    /// engine's own apphost — the only case where relaunching it actually restarts the engine. Under
    /// <c>dotnet run</c> the host is the shared <c>dotnet</c> muxer, and an empty/unknown path is not
    /// launchable either; in both cases the caller must fall back to asking the operator to restart.
    /// </summary>
    public static bool IsSelfRelaunchable(string? processPath)
        => !string.IsNullOrEmpty(processPath)
           && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="args"/> with the <c>--monitor</c> / <c>--ocr-lang</c> / <c>--pipe</c>
    /// override pairs stripped, so a freshly-persisted config value is not immediately re-overridden
    /// on restart.
    /// </summary>
    public static string[] StripPersistedOverrides(IReadOnlyList<string> args)
    {
        var kept = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var isOverride = ValuedOverrides.Any(o => args[i].Equals(o, StringComparison.OrdinalIgnoreCase));
            if (isOverride)
            {
                i++; // also skip the value token that follows the flag
                continue;
            }
            kept.Add(args[i]);
        }
        return kept.ToArray();
    }
}
