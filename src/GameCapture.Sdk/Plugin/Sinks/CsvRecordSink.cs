using System.Text;

namespace GameCapture.Sdk;

/// <summary>Appends CSV rows — a fixed prefix plus a caller-supplied, stable column order for
/// <see cref="CaptureRecord.Fields"/>. No-ops under replay mode.</summary>
public sealed class CsvRecordSink : IRecordSink
{
    private readonly bool _replayMode;
    private readonly bool _recordClears;
    private readonly IReadOnlyList<string> _fieldColumns;
    private readonly StreamWriter? _writer;

    public CsvRecordSink(string path, bool replayMode, IReadOnlyList<string> fieldColumns, bool recordClears = false)
    {
        _replayMode = replayMode;
        _recordClears = recordClears;
        _fieldColumns = fieldColumns;
        if (_replayMode)
        {
            Console.Error.WriteLine($"CsvRecordSink: replay mode, '{path}' will not be written.");
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Encoding.UTF8);
        if (isNew)
        {
            var header = new List<string> { "timestamp", "plugin", "trigger", "kind", "rawText" };
            header.AddRange(_fieldColumns);
            _writer.WriteLine(string.Join(',', header.Select(Escape)));
            _writer.Flush();
        }
    }

    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        if (_replayMode) return;
        if (record.Kind == RecordKind.Cleared && !_recordClears) return;

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
            await _writer!.WriteLineAsync(string.Join(',', row.Select(Escape)).AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"CsvRecordSink: write failed: {ex.Message}");
        }
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
