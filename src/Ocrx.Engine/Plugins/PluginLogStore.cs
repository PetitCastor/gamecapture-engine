using System.Collections.Concurrent;

namespace Ocrx.Engine.Plugins;

/// <summary>
/// Every plugin's captured output, keyed by catalog id. In memory only — nothing is written to disk.
/// </summary>
/// <remarks>
/// A buffer opens when a plugin starts and outlives the process, because the run worth reading is
/// usually the one that ended: a plugin that dies on startup leaves its stderr and its exit code here
/// for a row that now says "stopped". Buffers end only when the plugin is uninstalled or the engine
/// exits. The absence of a <c>Changed</c> event is deliberate — see <see cref="PluginRow.HasLogs"/>,
/// which flips at exactly the moment <see cref="PluginLauncher.Changed"/> already fires.
/// </remarks>
public sealed class PluginLogStore
{
    /// <summary>Lines retained per plugin.</summary>
    public const int DefaultMaxLines = 2000;

    /// <summary>Characters retained per line; longer lines are truncated with an ellipsis.</summary>
    public const int DefaultMaxLineLength = 2000;

    /// <summary>
    /// Characters retained per plugin, across all its lines. The line cap alone would let one buffer
    /// hold 4 MB of text; a plugin logging a serialized payload per tick is exactly that case.
    /// </summary>
    public const int DefaultMaxTotalChars = 1_000_000;

    private readonly ConcurrentDictionary<string, PluginLogBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxLines;
    private readonly int _maxLineLength;
    private readonly int _maxTotalChars;

    public PluginLogStore(
        TimeProvider? time = null,
        int maxLines = DefaultMaxLines,
        int maxLineLength = DefaultMaxLineLength,
        int maxTotalChars = DefaultMaxTotalChars)
    {
        _time = time ?? TimeProvider.System;
        _maxLines = maxLines;
        _maxLineLength = maxLineLength;
        _maxTotalChars = maxTotalChars;
    }

    /// <summary>Whether this plugin has been started at least once since the engine came up.</summary>
    public bool Has(string id) => _buffers.ContainsKey(id);

    /// <summary>Forgets a plugin's output. Called when it is uninstalled, not when it stops.</summary>
    public void Drop(string id) => _buffers.TryRemove(id, out _);

    /// <summary>
    /// The buffer for a plugin about to start, created on first use. A relaunch reuses the existing
    /// buffer on purpose: the engine's "started" notice separates the runs, so "crashed, restarted,
    /// crashed again" reads as one history instead of silently erasing the interesting part.
    /// </summary>
    internal PluginLogBuffer Open(string id)
        => _buffers.GetOrAdd(id, _ => new PluginLogBuffer(_time, _maxLines, _maxLineLength, _maxTotalChars));

    /// <summary>Appends to an existing buffer. No-op for a plugin that has never been started.</summary>
    internal void Append(string id, PluginLogStream stream, string? text)
    {
        if (_buffers.TryGetValue(id, out var buffer))
            buffer.Append(stream, text);
    }

    /// <summary>
    /// A page of one plugin's output. An id that never started is not an error — it reads as an empty
    /// page reporting <see cref="PluginLogPage.HasBuffer"/> false.
    /// </summary>
    internal PluginLogPage Read(string id, long after, int limit)
        => _buffers.TryGetValue(id, out var buffer)
            ? buffer.Read(after, limit)
            : new PluginLogPage(
                HasBuffer: false,
                [],
                NextSequence: Math.Max(after, -1),
                OldestSequence: 0,
                DroppedLines: 0,
                Truncated: false);
}
