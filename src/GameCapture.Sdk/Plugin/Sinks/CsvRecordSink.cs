using System.Text;

namespace GameCapture.Sdk;

/// <summary>Appends CSV rows — a fixed prefix plus a caller-supplied, stable column order for
/// <see cref="CaptureRecord.Fields"/>. Replay mode is checked on every emit rather than once at
/// construction, so the file (and its header) is opened lazily on the first write that is actually
/// allowed through — a run that never leaves replay mode never touches the
/// filesystem.</summary>
public sealed class CsvRecordSink : IRecordSink
{
    private readonly string _path;
    private readonly IReadOnlyList<string> _fieldColumns;
    private readonly Func<bool> _isReplay;
    private readonly bool _recordClears;
    private StreamWriter? _writer;
    private bool _loggedReplaySkip;

    public CsvRecordSink(string path, IReadOnlyList<string> fieldColumns, Func<bool> isReplay,
        bool recordClears = false)
    {
        _path = path;
        _fieldColumns = fieldColumns;
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
                Console.Error.WriteLine($"CsvRecordSink: replay mode, '{_path}' will not be written.");
            }
            return;
        }
        if (record.Kind == RecordKind.Cleared && !_recordClears) return;

        _writer ??= OpenWriter();

        var row = new List<string>
        {
            record.Timestamp.ToString("O"),
            record.Plugin,
            record.Trigger.ToString(),
            record.Kind.ToString(),
            record.RawText,
        };
        foreach (var col in _fieldColumns)
            row.Add(record.Fields is not null && record.Fields.TryGetValue(col, out var v) ? v : string.Empty);

        try
        {
            await _writer.WriteLineAsync(string.Join(',', row.Select(Escape)).AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"CsvRecordSink: write failed: {ex.Message}");
        }
    }

    private StreamWriter OpenWriter()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var isNew = !File.Exists(_path) || new FileInfo(_path).Length == 0;
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, Encoding.UTF8);
        if (isNew)
        {
            var header = new List<string> { "timestamp", "plugin", "trigger", "kind", "rawText" };
            header.AddRange(_fieldColumns);
            writer.WriteLine(string.Join(',', header.Select(Escape)));
            writer.Flush();
        }
        return writer;
    }

    private static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0) return field;
        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is null) return;
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }
}
