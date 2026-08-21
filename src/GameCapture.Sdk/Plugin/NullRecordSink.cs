namespace GameCapture.Sdk;

/// <summary>The no-op sink — used when a run has no sinks configured.</summary>
internal sealed class NullRecordSink : IRecordSink
{
    public static readonly NullRecordSink Instance = new();

    public ValueTask EmitAsync(CaptureRecord record, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
