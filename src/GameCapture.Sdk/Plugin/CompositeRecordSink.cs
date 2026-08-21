namespace GameCapture.Sdk;

/// <summary>Fans a record out to every child sink, in order, on the single drain task.</summary>
internal sealed class CompositeRecordSink(IReadOnlyList<IRecordSink> sinks) : IRecordSink
{
    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.EmitAsync(record, ct); }
            catch (Exception) { /* one failing sink must not block the others */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sink in sinks)
            await sink.DisposeAsync();
    }
}
