namespace GameCapture.Engine.Plugins;

/// <summary>
/// One captured line of plugin output.
/// </summary>
/// <remarks>
/// A struct because the ring holds these in a flat array: an entry then costs one array slot plus the
/// string it points at, rather than a slot plus a separately allocated object per line.
/// </remarks>
/// <param name="Sequence">Monotonic per-buffer number, never reset and never reused. This is the
/// cursor a reader pages with; it keeps meaning after eviction, which an index into the ring would
/// not.</param>
/// <param name="Timestamp">When the engine received the line, not when the plugin wrote it — the SDK
/// stamps nothing, so this is the closest honest reading available.</param>
/// <param name="Stream">Where the line came from.</param>
/// <param name="Text">The line itself, already split on newlines and truncated to the buffer's
/// per-line cap.</param>
public readonly record struct PluginLogLine(
    long Sequence,
    DateTimeOffset Timestamp,
    PluginLogStream Stream,
    string Text);
