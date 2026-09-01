namespace GameCapture.Engine.Plugins;

/// <summary>
/// Which stream a captured line came from.
/// </summary>
/// <remarks>
/// <see cref="Engine"/> is not a stream the plugin writes to — it marks the engine's own notices
/// (started, exited with a code, stopped) that are woven into the same buffer. Keeping them in the
/// buffer rather than beside it is what makes a crash readable as one story: the plugin's last stderr
/// line and the exit code that followed it sit next to each other, in order.
/// </remarks>
public enum PluginLogStream
{
    /// <summary>The child's standard output.</summary>
    Stdout,

    /// <summary>The child's standard error — where the SDK reports usage and invalid-config failures.</summary>
    Stderr,

    /// <summary>An engine notice about the process itself, not output from the plugin.</summary>
    Engine,
}
