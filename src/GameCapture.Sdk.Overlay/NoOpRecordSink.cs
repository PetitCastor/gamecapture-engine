using GameCapture.Sdk;

namespace GameCapture.Sdk.Overlay;

internal sealed class NoOpRecordSink : IRecordSink
{
    public static readonly NoOpRecordSink Instance = new();

    public ValueTask EmitAsync(CaptureRecord record, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
