using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GameCapture.Sdk.Tests.Sinks;

public class JsonRecordSinkTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"gc-json-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task EmitAsync_WritesOneJsonObjectPerLine()
    {
        var path = TempPath();
        try
        {
            await using (var sink = new JsonRecordSink(path, isReplay: () => false))
            {
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "two"), CancellationToken.None);
            }

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("one", doc.RootElement.GetProperty("rawText").GetString());
            Assert.Equal("refinery", doc.RootElement.GetProperty("plugin").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_SerializesFieldsAsTopLevelProps()
    {
        var path = TempPath();
        try
        {
            var record = new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "raw")
            {
                Fields = new Dictionary<string, string> { ["quantity"] = "42" },
            };
            await using (var sink = new JsonRecordSink(path, isReplay: () => false))
                await sink.EmitAsync(record, CancellationToken.None);

            var line = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("42", doc.RootElement.GetProperty("quantity").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_UnderReplayMode_NeverCreatesTheFile()
    {
        var path = TempPath();
        await using var sink = new JsonRecordSink(path, isReplay: () => true);
        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EmitAsync_ClearedRecord_IsIgnoredByDefault()
    {
        var path = TempPath();
        try
        {
            await using (var sink = new JsonRecordSink(path, isReplay: () => false))
            {
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "") { Kind = RecordKind.Cleared }, CancellationToken.None);
            }

            Assert.False(File.Exists(path) && (await File.ReadAllLinesAsync(path)).Length > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_ClearedRecord_IsWrittenWhenRecordClearsIsSet()
    {
        var path = TempPath();
        try
        {
            await using (var sink = new JsonRecordSink(path, isReplay: () => false, recordClears: true))
            {
                await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "") { Kind = RecordKind.Cleared }, CancellationToken.None);
            }

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Single(lines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_OnAWriteFailure_DoesNotThrow()
    {
        var path = TempPath();
        try
        {
            var sink = new JsonRecordSink(path, isReplay: () => false);
            await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

            var writerField = typeof(JsonRecordSink).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((StreamWriter)writerField.GetValue(sink)!).Dispose();

            await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "two"), CancellationToken.None);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task EmitAsync_OnOpenFailure_DoesNotThrow()
    {
        var parentFile = TempPath();
        try
        {
            await File.WriteAllTextAsync(parentFile, "parent");
            var path = Path.Combine(parentFile, "records.jsonl");

            await using var sink = new JsonRecordSink(path, isReplay: () => false);
            await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);
        }
        finally
        {
            if (File.Exists(parentFile)) File.Delete(parentFile);
        }
    }
}
