using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests;

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
        Assert.Equal(1, page.NextSequence);
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
        Assert.Equal(4, page.NextSequence);
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
        Assert.Equal(2, page.NextSequence);
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
    /// A limit-capped page must return the last delivered line as the exclusive cursor. Returning the
    /// next unread line would silently lose it on the next request.
    /// </summary>
    [Fact]
    public void Read_RespectsTheLimit_AndPointsTheCursorAtTheRemainder()
    {
        var buffer = Buffer();
        foreach (var i in Enumerable.Range(0, 10))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: -1, limit: 4);

        Assert.Equal(4, page.Lines.Count);
        Assert.Equal(3, page.NextSequence);

        var rest = buffer.Read(page.NextSequence, limit: 100);
        Assert.Equal("line 4", rest.Lines[0].Text);
    }

    /// <summary>
    /// A page that took nothing must leave the exclusive cursor unchanged; advancing it would skip
    /// lines the caller has not yet read.
    /// </summary>
    [Fact]
    public void Read_WithAZeroLimit_LeavesTheCursorWhereItWas()
    {
        var buffer = Buffer();
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: -1, limit: 0);

        Assert.Empty(page.Lines);
        Assert.Equal(-1, page.NextSequence);

        // The cursor it handed back still reaches everything.
        var rest = buffer.Read(page.NextSequence, limit: 100);
        Assert.Equal(5, rest.Lines.Count);
    }

    /// <summary>
    /// After an eviction the cursor has to resume at the oldest line actually delivered. Returning the
    /// next retained line would exclude it from the next request.
    /// </summary>
    [Fact]
    public void Read_WithAStaleCursor_ResumesAfterTheLinesItActuallyReturned()
    {
        var buffer = Buffer(maxLines: 2);
        foreach (var i in Enumerable.Range(0, 5))
            buffer.Append(PluginLogStream.Stdout, $"line {i}");

        var page = buffer.Read(after: 0, limit: 1);

        Assert.Equal(["line 3"], page.Lines.Select(line => line.Text));
        Assert.Equal(3, page.NextSequence);

        var rest = buffer.Read(page.NextSequence, limit: 100);
        Assert.Equal(["line 4"], rest.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Read_OnAnEmptyBuffer_ReturnsAnEmptyPageThatStillHasABuffer()
    {
        var page = ReadAll(Buffer());

        Assert.True(page.HasBuffer);
        Assert.Empty(page.Lines);
        Assert.Equal(-1, page.NextSequence);
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
