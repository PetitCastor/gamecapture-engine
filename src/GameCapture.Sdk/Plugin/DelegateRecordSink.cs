namespace GameCapture.Sdk;

/// <summary>Adapts the legacy <see cref="PluginHostOptions.RecordSink"/> callback into an <see cref="IRecordSink"/>.</summary>
internal sealed class DelegateRecordSink(Action<CaptureRecord> callback) : IRecordSink
{
    public ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        callback(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
