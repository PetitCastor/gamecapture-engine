namespace GameCapture.Engine.Plugins;

/// <summary>
/// A bounded, thread-safe ring of one plugin's captured output.
/// </summary>
/// <remarks>
/// All of the feature's real decisions live here — how a write becomes lines, what a cursor means
/// across an eviction, when a reader is told it missed something — deliberately kept away from
/// <see cref="PluginLauncher"/>, which is the untestable process edge. Both of a plugin's streams
/// append to the same buffer from thread-pool callbacks, so every member takes the lock; the lock is
/// a leaf, and nothing it guards ever calls back out.
/// </remarks>
internal sealed class PluginLogBuffer
{
    private readonly Lock _gate = new();
    private readonly PluginLogLine[] _ring;
    private readonly TimeProvider _time;
    private readonly int _maxLineLength;
    private readonly int _maxTotalChars;

    private int _head;
    private int _count;
    private long _nextSequence;
    private long _droppedLines;
    private long _totalChars;

    internal PluginLogBuffer(TimeProvider time, int maxLines, int maxLineLength, int maxTotalChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalChars, 1);

        _time = time;
        _ring = new PluginLogLine[maxLines];
        _maxLineLength = maxLineLength;
        _maxTotalChars = maxTotalChars;
    }

    /// <summary>
    /// Records a write. A null <paramref name="text"/> is the end-of-stream sentinel
    /// <see cref="System.Diagnostics.Process.OutputDataReceived"/> delivers once per stream; it carries
    /// nothing worth showing, so it is dropped rather than stored as a blank line.
    /// </summary>
    internal void Append(PluginLogStream stream, string? text)
    {
        if (text is null)
            return;

        // Read once, outside the lock: every line of one write shares the instant it arrived, and the
        // clock is never read while holding the gate.
        var timestamp = _time.GetLocalNow();

        lock (_gate)
        {
            foreach (var line in SplitLines(text))
                Add(stream, line, timestamp);
        }
    }

    /// <summary>Lines newer than <paramref name="after"/>, oldest first, at most <paramref name="limit"/>.</summary>
    /// <param name="after">Exclusive cursor; -1 asks for everything still retained.</param>
    internal PluginLogPage Read(long after, int limit)
    {
        if (after < -1)
            after = -1;
        if (limit < 0)
            limit = 0;

        lock (_gate)
        {
            var oldest = _count == 0 ? _nextSequence : _ring[_head].Sequence;

            // Sequences are contiguous, so the cursor resolves to an offset without scanning: anything
            // the caller has already seen, plus anything evicted before it asked, is simply skipped.
            var skip = (int)Math.Clamp(after + 1 - oldest, 0, _count);
            var take = Math.Min(_count - skip, limit);

            var lines = new PluginLogLine[take];
            for (var i = 0; i < take; i++)
                lines[i] = _ring[(_head + skip + i) % _ring.Length];

            // `after` is exclusive, so the caller must send back the last line it actually received.
            // Returning the next unread sequence would make the next request exclude it and skip that
            // line. An empty page leaves the cursor unchanged for the same reason.
            var next = take > 0 ? lines[^1].Sequence : after;

            return new PluginLogPage(
                HasBuffer: true,
                lines,
                next,
                oldest,
                _droppedLines,
                // Not "has this buffer ever evicted" — that is DroppedLines. This is the narrower and
                // more useful claim: lines *this reader had not seen* went away between its polls.
                Truncated: after >= 0 && after + 1 < oldest);
        }
    }

    // The SDK writes a whole multi-line block as one Console.WriteLine — PluginServices.Emit joins its
    // five-line capture banner with Environment.NewLine deliberately, to avoid flickering the status
    // row five times. Storing that as one entry would make the line cap silently count in blocks.
    private static IEnumerable<string> SplitLines(string text)
    {
        var fragments = text.Split('\n');
        for (var i = 0; i < fragments.Length; i++)
        {
            var line = fragments[i].TrimEnd('\r');

            // A trailing newline is punctuation, not an empty line. Interior blanks are kept: a plugin
            // spacing its output out meant to.
            if (line.Length == 0 && i == fragments.Length - 1 && fragments.Length > 1)
                continue;

            yield return line;
        }
    }

    // Called under the lock.
    private void Add(PluginLogStream stream, string text, DateTimeOffset timestamp)
    {
        if (text.Length > _maxLineLength)
            text = string.Concat(text.AsSpan(0, _maxLineLength), "…");

        if (_count == _ring.Length)
            Evict();

        _ring[(_head + _count) % _ring.Length] = new PluginLogLine(_nextSequence++, timestamp, stream, text);
        _count++;
        _totalChars += text.Length;

        // The character cap is what bounds a plugin that logs a serialized payload per tick: the line
        // cap alone would let one buffer hold maxLines * maxLineLength characters.
        while (_totalChars > _maxTotalChars && _count > 1)
            Evict();
    }

    // Called under the lock.
    private void Evict()
    {
        _totalChars -= _ring[_head].Text.Length;
        _ring[_head] = default;
        _head = (_head + 1) % _ring.Length;
        _count--;
        _droppedLines++;
    }
}
