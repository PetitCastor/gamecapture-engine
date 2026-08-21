using GameCapture.Contracts;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The sink fan-out added in SINK-02: <see cref="PluginServices.Emit"/> and
/// <see cref="PluginServices.EmitCleared"/> queue onto an unbounded channel rather than calling a
/// sink directly, so these tests exercise the drain loop end to end via
/// <see cref="PluginServices.StartDraining"/> / <see cref="PluginServices.CompleteAndDrainAsync"/>
/// rather than asserting on some synchronous side effect.
/// </summary>
public class PluginServicesSinkTests
{
    private static PluginServices New(out List<CaptureRecord> records, IRecordSink sink,
        Action<CaptureRecord>? recordSink = null)
    {
        records = [];
        return new PluginServices(records, new RecordingOutput(), verbose: false,
            dumpFrame: null, readRoi: null, recordSink: recordSink, sink: sink);
    }

    [Fact]
    public async Task Emit_DeliversToTheSinkInEmitOrder()
    {
        var sink = new FakeRecordSink();
        var services = New(out _, sink);
        services.StartDraining(CancellationToken.None);

        services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"));
        services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "two"));
        services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "three"));

        await services.CompleteAndDrainAsync();

        Assert.Equal(["one", "two", "three"], sink.Received.Select(r => r.RawText));
    }

    [Fact]
    public async Task EmitCleared_FansAClearedRecordToTheSinkOnly()
    {
        var sink = new FakeRecordSink();
        var services = New(out var records, sink);
        services.StartDraining(CancellationToken.None);

        services.EmitCleared(DateTime.Now, "refinery");

        await services.CompleteAndDrainAsync();

        Assert.Empty(records);
        var record = Assert.Single(sink.Received);
        Assert.Equal(RecordKind.Cleared, record.Kind);
        Assert.Equal("refinery", record.Plugin);
    }

    [Fact]
    public async Task CompleteAndDrainAsync_FlushesEverythingQueuedBeforeReturning()
    {
        var sink = new FakeRecordSink();
        var services = New(out _, sink);
        services.StartDraining(CancellationToken.None);

        for (var i = 0; i < 50; i++)
            services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, i.ToString()));

        await services.CompleteAndDrainAsync();

        Assert.Equal(50, sink.Received.Count);
        Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task AThrowingSink_IsIsolatedByTheComposite_AndDoesNotBlockOthers()
    {
        var throwing = new FakeRecordSink { ThrowOnEmit = true };
        var healthy = new FakeRecordSink();
        var composite = new CompositeRecordSink([throwing, healthy]);
        var services = New(out _, composite);
        services.StartDraining(CancellationToken.None);

        services.Emit(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "text"));

        await services.CompleteAndDrainAsync();

        Assert.Single(healthy.Received);
        Assert.True(throwing.Disposed);
        Assert.True(healthy.Disposed);
    }

    [Fact]
    public async Task LegacyRecordSink_StillFiresAlongsideTheNewSink()
    {
        var sink = new FakeRecordSink();
        var legacySeen = new List<CaptureRecord>();
        var services = New(out _, sink, recordSink: legacySeen.Add);
        services.StartDraining(CancellationToken.None);

        var record = new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "text");
        services.Emit(record);

        await services.CompleteAndDrainAsync();

        Assert.Same(record, Assert.Single(legacySeen));
        Assert.Single(sink.Received);
    }
}
