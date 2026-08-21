using System.Reflection;
using Xunit;

namespace GameCapture.Sdk.Tests.Sinks;

public class CsvRecordSinkTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"gc-csv-{Guid.NewGuid():N}.csv");

    [Fact]
    public async Task EmitAsync_WritesHeaderThenRowsInFieldColumnOrder()
    {
        var path = TempPath();
        try
        {
            var record = new CaptureRecord(new DateTime(2026, 1, 1), "refinery", TriggerKind.Auto, "raw")
            {
                Fields = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
            };
            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: ["a", "b"]))
                await sink.EmitAsync(record, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("timestamp,plugin,trigger,kind,rawText,a,b", lines[0]);
            Assert.EndsWith(",1,2", lines[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_QuotesFieldsContainingCommaOrQuoteOrNewline()
    {
        var path = TempPath();
        try
        {
            var record = new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "has,comma and \"quote\"");
            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: []))
                await sink.EmitAsync(record, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Contains("\"has,comma and \"\"quote\"\"\"", lines[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_AppendingToAnExistingFile_DoesNotRewriteTheHeader()
    {
        var path = TempPath();
        try
        {
            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: []))
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: []))
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "two"), CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal("timestamp,plugin,trigger,kind,rawText", lines[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_UnderReplayMode_NeverCreatesTheFile()
    {
        var path = TempPath();
        await using var sink = new CsvRecordSink(path, replayMode: true, fieldColumns: []);
        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EmitAsync_AppendingToAPreCreatedEmptyFile_StillWritesTheHeader()
    {
        var path = TempPath();
        try
        {
            await File.Create(path).DisposeAsync();

            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: []))
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("timestamp,plugin,trigger,kind,rawText", lines[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_MissingFieldForAConfiguredColumn_WritesAnEmptyCell()
    {
        var path = TempPath();
        try
        {
            var record = new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "raw")
            {
                Fields = new Dictionary<string, string> { ["a"] = "1" },
            };
            await using (var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: ["a", "b"]))
                await sink.EmitAsync(record, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.EndsWith(",1,", lines[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_OnAWriteFailure_DoesNotThrow()
    {
        var path = TempPath();
        try
        {
            var sink = new CsvRecordSink(path, replayMode: false, fieldColumns: []);
            var writerField = typeof(CsvRecordSink).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((StreamWriter)writerField.GetValue(sink)!).Dispose();

            await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
