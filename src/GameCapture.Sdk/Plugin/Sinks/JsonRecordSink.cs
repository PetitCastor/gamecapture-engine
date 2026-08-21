using System.Text;
using System.Text.Json;

namespace GameCapture.Sdk;

/// <summary>Appends one JSON object per line (JSONL) to a file. No-ops under replay mode.</summary>
public sealed class JsonRecordSink : IRecordSink
{
    private readonly bool _replayMode;
    private readonly bool _recordClears;
    private readonly StreamWriter? _writer;

    public JsonRecordSink(string path, bool replayMode, bool recordClears = false)
    {
        _replayMode = replayMode;
        _recordClears = recordClears;
        if (_replayMode)
        {
            Console.Error.WriteLine($"JsonRecordSink: replay mode, '{path}' will not be written.");
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Encoding.UTF8);
    }

    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        if (_replayMode) return;
        if (record.Kind == RecordKind.Cleared && !_recordClears) return;

        var obj = new Dictionary<string, object?>
        {
            ["timestamp"] = record.Timestamp,
            ["plugin"] = record.Plugin,
            ["trigger"] = record.Trigger.ToString(),
            ["kind"] = record.Kind.ToString(),
            ["rawText"] = record.RawText,
        };
        if (record.Fields is not null)
            foreach (var (k, v) in record.Fields) obj[k] = v;

        try
        {
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(obj).AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"JsonRecordSink: write failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is null) return;
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }
}
