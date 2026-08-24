namespace GameCapture.Sdk;

/// <summary>Decorator that forwards a record only when its dedupe key differs from the last
/// forwarded record. Turns a per-tick continuous stream back into one row per real change.
/// <see cref="RecordKind.Cleared"/> records always forward through and reset the "last" key so
/// the next observation is treated as new.</summary>
public sealed class ChangeDedupeSink(IRecordSink inner) : IRecordSink
{
    private const char FieldSep = '';
    private string? _last;

    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        if (record.Kind == RecordKind.Cleared)
        {
            _last = null;
            await inner.EmitAsync(record, ct);
            return;
        }

        var key = KeyOf(record);
        if (key == _last) return;
        _last = key;
        await inner.EmitAsync(record, ct);
    }

    private static string KeyOf(CaptureRecord record)
    {
        // R/F tag keeps the Fields key-space and the RawText key-space from colliding, e.g. an
        // empty Fields dict (key "F") vs. a null-Fields record with empty RawText (key "R").
        if (record.Fields is null) return $"R{FieldSep}{record.RawText}";
        return string.Join(FieldSep, record.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}{FieldSep}{kv.Value}").Prepend("F"));
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
