namespace Ocrx.Engine.Plugins;

/// <summary>
/// One page of captured output, read with a cursor. Doubles as the body of
/// <c>GET /api/plugins/{id}/logs</c>.
/// </summary>
/// <param name="HasBuffer">Whether this plugin has been started at least once this session. A plugin
/// that never ran is not an error — it is an empty page that says so.</param>
/// <param name="Lines">Lines newer than the cursor, oldest first, capped by the request's limit.</param>
/// <param name="NextSequence">The exclusive <c>after</c> cursor to send back next time: the final line
/// delivered by this page, or the caller's previous cursor when the page is empty.</param>
/// <param name="OldestSequence">Sequence of the oldest line still retained.</param>
/// <param name="DroppedLines">How many lines this buffer has evicted since it opened — a standing
/// "the history is trimmed" fact, not a per-request one.</param>
/// <param name="Truncated">Whether lines the caller had not yet seen were evicted before this read.
/// Computed here rather than by the client because only the buffer sees the cursor and the oldest
/// retained sequence in the same critical section.</param>
public sealed record PluginLogPage(
    bool HasBuffer,
    IReadOnlyList<PluginLogLine> Lines,
    long NextSequence,
    long OldestSequence,
    long DroppedLines,
    bool Truncated);
