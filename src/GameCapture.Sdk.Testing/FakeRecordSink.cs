using GameCapture.Sdk;

namespace GameCapture.Sdk.Testing;

/// <summary>An in-memory <see cref="IRecordSink"/> for assertions over records delivered through
/// the host's sink pipeline.</summary>
public sealed class FakeRecordSink : IRecordSink
{
    /// <summary>Every record received through <see cref="EmitAsync"/>, in order.</summary>
    public List<CaptureRecord> Received { get; } = [];

    /// <inheritdoc />
    public ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        Received.Add(record);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
