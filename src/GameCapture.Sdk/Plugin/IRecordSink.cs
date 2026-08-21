namespace GameCapture.Sdk;

/// <summary>A destination for emitted <see cref="CaptureRecord"/>s — file, HTTP, overlay, etc.</summary>
public interface IRecordSink : IAsyncDisposable
{
    /// <summary>Deliver one record. Called sequentially from a single drain task, never
    /// concurrently, so an implementation needs no internal locking. Must not throw for a
    /// transient failure — log/swallow; a sink that throws is caught by the drain and skipped.</summary>
    ValueTask EmitAsync(CaptureRecord record, CancellationToken ct);
}
