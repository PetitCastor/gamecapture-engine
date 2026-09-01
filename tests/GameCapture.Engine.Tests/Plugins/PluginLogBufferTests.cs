using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The ring behind every plugin's captured output. Everything the feature actually decides lives here
/// — how a write becomes lines, what a cursor still means after an eviction, when a reader is told it
/// missed something — because <see cref="PluginLauncher"/> itself is the process edge and cannot be
/// covered.
/// </summary>
public class PluginLogBufferTests
{
    private static PluginLogBuffer Buffer(int maxLines = 100, int maxLineLength = 1000, int maxTotalChars = 100_000)
        => new(TimeProvider.System, maxLines, maxLineLength, maxTotalChars);

    private static PluginLogPage ReadAll(PluginLogBuffer buffer) => buffer.Read(after: -1, limit: 1000);

    [Fact]
    public void Append_AssignsMonotonicSequencesFromZero()
    {
        var buffer = Buffer();
        buffer.Append(PluginLogStream.Stdout, "one");
        buffer.Append(PluginLogStream.Stderr, "two");

        var page = ReadAll(buffer);

        Assert.True(page.HasBuffer);
        Assert.Equal([0L, 1L], page.Lines.Select(line => line.Sequence));
        Assert.Equal(["one", "two"], page.Lines.Select(line => line.Text));
        Assert.Equal(PluginLogStream.Stderr, page.Lines[1].Stream);
        Assert.Equal(2, page.NextSequence);
    }

    /// <summary>
    /// The SDK writes whole blocks as a single Console.WriteLine — PluginServices.Emit joins its
    /// five-line capture banner with Environment.NewLine on purpose, to avoid flickering the status row
    /// five times. Stored as one entry, the line cap would silently count in blocks instead of lines.
    /// </summary>
    [Fact]
    public void AMultiLineWrite_BecomesOneEntryPerLine()
    {
        var buffer = Buffer();
        buffer.Append(PluginLogStream.Stdout, "first\r\nsecond\nthird");

        Assert.Equal(["first", "second", "third"], ReadAll(buffer).Lines.Select(line => line.Text));
    }

    [Fact]
    public void ATrailingNewline_DoesNotProduceAnEmptyLine_ButAnInteriorBlankIsKept()
    {
        var buffer = Buffer();
        buffer.Append(PluginLogStream.Stdout, "header\r\n\r\nbody\r\n");

        Assert.Equal(["header", "", "body"], ReadAll(buffer).Lines.Select(line => line.Text));
    }

    /// <summary>
    /// The end-of-stream sentinel Process.OutputDataReceived delivers once per stream. It carries
    /// nothing to show, and storing it would put a blank line in the panel at an arbitrary moment.
    /// </summary>
    [Fact]
    public void NullData_IsIgnored()
    {
        var buffer = Buffer();
        buffer.Append(PluginLogStream.Stdout, null);

        Assert.Empty(ReadAll(buffer).Lines);
    }

    [Fact]
    public void AnOverlongLine_IsTruncatedWithAnEllipsis()
    {
        var buffer = Buffer(maxLineLength: 10);
        buffer.Append(PluginLogStream.Stdout, new string('x', 25));

        var line = Assert.Single(ReadAll(buffer).Lines);
        Assert.Equal(new string('x', 10) + "…", line.Text);
    }

    [Fact]
    public void PastTheLineCap_TheOldestLinesAreEvictedAndCounted()
    {
        var buffer = Buffer(maxLines: 3);
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = ReadAll(buffer);

        Assert.Equal(["line 2", "line 3", "line 4"], page.Lines.Select(line => line.Text));
        Assert.Equal(2, page.DroppedLines);
        Assert.Equal(2, page.OldestSequence);
        Assert.Equal(5, page.NextSequence);
    }

    /// <summary>
    /// The line cap alone would let one buffer hold maxLines * maxLineLength characters, which is the
    /// plugin that logs a serialized payload every tick. The character cap is what bounds that.
    /// </summary>
    [Fact]
    public void TheCharacterCap_EvictsEvenWhileUnderTheLineCap()
    {
        var buffer = Buffer(maxLines: 100, maxLineLength: 100, maxTotalChars: 25);
        foreach (var i in Enumerable.Range(0, 4))
            buffer.Append(PluginLogStream.Stdout, new string((char)('a' + i), 10));

        var page = ReadAll(buffer);

        Assert.Equal(2, page.Lines.Count);
        Assert.Equal(["cccccccccc", "dddddddddd"], page.Lines.Select(line => line.Text));
        Assert.Equal(2, page.DroppedLines);
    }

    /// <summary>A single line over the whole-buffer cap is kept rather than evicting itself to nothing.</summary>
    [Fact]
    public void ALineLargerThanTheCharacterCap_IsStillRetained()
    {
        var buffer = Buffer(maxLines: 10, maxLineLength: 100, maxTotalChars: 5);
        buffer.Append(PluginLogStream.Stdout, new string('x', 40));

        Assert.Single(ReadAll(buffer).Lines);
    }

    [Fact]
    public void Read_AfterACursor_ReturnsOnlyNewerLines()
    {
        var buffer = Buffer();
        buffer.Append(PluginLogStream.Stdout, "one\ntwo\nthree");

        var page = buffer.Read(after: 0, limit: 100);

        Assert.Equal(["two", "three"], page.Lines.Select(line => line.Text));
        Assert.False(page.Truncated);
        Assert.Equal(3, page.NextSequence);
    }

    [Fact]
    public void Read_WithAStaleCursor_ReportsTruncated()
    {
        var buffer = Buffer(maxLines: 2);
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: 0, limit: 100);

        Assert.True(page.Truncated);
        Assert.Equal(["line 3", "line 4"], page.Lines.Select(line => line.Text));
    }

    /// <summary>
    /// The two "we lost lines" signals answer different questions and must not collapse into one:
    /// DroppedLines is the buffer's standing history, Truncated is about this reader's own cursor.
    /// A caller that is fully caught up has missed nothing, however much the buffer has shed.
    /// </summary>
    [Fact]
    public void Read_WithACurrentCursor_IsNotTruncated_EvenAfterEvictions()
    {
        var buffer = Buffer(maxLines: 2);
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: 3, limit: 100);

        Assert.False(page.Truncated);
        Assert.Equal(3, page.DroppedLines);
        Assert.Equal(["line 4"], page.Lines.Select(line => line.Text));
    }

    /// <summary>
    /// A reader asking for everything cannot have missed anything yet, so the very first page of a
    /// buffer that has already evicted is not flagged — the standing DroppedLines notice covers it.
    /// </summary>
    [Fact]
    public void Read_FromTheStart_IsNeverTruncated()
    {
        var buffer = Buffer(maxLines: 2);
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        Assert.False(ReadAll(buffer).Truncated);
    }

    /// <summary>
    /// A limit-capped page must leave the cursor on the remainder. Advancing it to the buffer's head
    /// would page a caller straight past everything a fast writer produced in between.
    /// </summary>
    [Fact]
    public void Read_RespectsTheLimit_AndPointsTheCursorAtTheRemainder()
    {
        var buffer = Buffer();
        foreach (var i in Enumerable.Range(0, 10))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: -1, limit: 4);

        Assert.Equal(4, page.Lines.Count);
        Assert.Equal(4, page.NextSequence);

        var rest = buffer.Read(page.NextSequence - 1, limit: 100);
        Assert.Equal("line 4", rest.Lines[0].Text);
    }

    [Fact]
    public void Read_OnAnEmptyBuffer_ReturnsAnEmptyPageThatStillHasABuffer()
    {
        var page = ReadAll(Buffer());

        Assert.True(page.HasBuffer);
        Assert.Empty(page.Lines);
        Assert.Equal(0, page.NextSequence);
        Assert.Equal(0, page.OldestSequence);
        Assert.False(page.Truncated);
    }

    /// <summary>
    /// Both of a plugin's streams append from thread-pool callbacks, so they genuinely race. Nothing
    /// may be lost and no sequence may be handed out twice.
    /// </summary>
    [Fact]
    public void ConcurrentAppends_LoseNoLinesAndKeepSequencesUnique()
    {
        var buffer = Buffer(maxLines: 1000, maxTotalChars: 1_000_000);

        Parallel.For(0, 200, i =>
        {
            buffer.Append(PluginLogStream.Stdout, $"out {i}");
            buffer.Append(PluginLogStream.Stderr, $"err {i}");
        });

        var page = buffer.Read(after: -1, limit: 1000);

        Assert.Equal(400, page.Lines.Count);
        Assert.Equal(400, page.Lines.Select(line => line.Sequence).Distinct().Count());
        Assert.Equal(0, page.DroppedLines);
    }
}
