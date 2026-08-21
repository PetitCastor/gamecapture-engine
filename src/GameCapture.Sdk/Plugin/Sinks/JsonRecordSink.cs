using System.Text;
using System.Text.Json;

namespace GameCapture.Sdk;

/// <summary>Appends one JSON object per line (JSONL) to a file. Replay mode is checked on every emit
/// rather than once at construction, so the file is opened lazily on the first write that is
/// actually allowed through — a run that never leaves replay mode never touches the
/// filesystem.</summary>
public sealed class JsonRecordSink : IRecordSink
{
    private readonly string _path;
    private readonly Func<bool> _isReplay;
    private readonly bool _recordClears;
    private StreamWriter? _writer;
    private bool _loggedReplaySkip;

    public JsonRecordSink(string path, Func<bool> isReplay, bool recordClears = false)
    {
        _path = path;
        _isReplay = isReplay;
        _recordClears = recordClears;
    }

    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        if (_isReplay())
        {
            if (!_loggedReplaySkip)
            {
                _loggedReplaySkip = true;
                Console.Error.WriteLine($"JsonRecordSink: replay mode, '{_path}' will not be written.");
            }
            return;
        }
        if (record.Kind == RecordKind.Cleared && !_recordClears) return;

        _writer ??= OpenWriter();

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
            await _writer.WriteLineAsync(JsonSerializer.Serialize(obj).AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"JsonRecordSink: write failed: {ex.Message}");
        }
    }

    private StreamWriter OpenWriter()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, Encoding.UTF8);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is null) return;
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }
}
