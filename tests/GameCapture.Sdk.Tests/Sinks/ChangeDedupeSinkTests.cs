using Xunit;

namespace GameCapture.Sdk.Tests.Sinks;

public class ChangeDedupeSinkTests
{
    [Fact]
    public async Task EmitAsync_RepeatedRawTextObservations_ForwardOnlyTheFirst()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);

        for (var i = 0; i < 5; i++)
            await dedupe.EmitAsync(new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "same"), CancellationToken.None);

        Assert.Single(inner.Received);
    }

    [Fact]
    public async Task EmitAsync_AChangedValue_ForwardsAgain()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);

        await dedupe.EmitAsync(new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "one"), CancellationToken.None);
        await dedupe.EmitAsync(new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "two"), CancellationToken.None);

        Assert.Equal(2, inner.Received.Count);
    }

    [Fact]
    public async Task EmitAsync_FieldsCompareOrderIndependently()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);

        var first = new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "raw")
        {
            Fields = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
        };
        var second = new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "raw")
        {
            Fields = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
        };

        await dedupe.EmitAsync(first, CancellationToken.None);
        await dedupe.EmitAsync(second, CancellationToken.None);

        Assert.Single(inner.Received);
    }

    [Fact]
    public async Task EmitAsync_ClearedRecord_AlwaysForwardsAndResetsState()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);
        var observation = new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "same");

        await dedupe.EmitAsync(observation, CancellationToken.None);
        await dedupe.EmitAsync(new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "") { Kind = RecordKind.Cleared }, CancellationToken.None);
        await dedupe.EmitAsync(observation, CancellationToken.None);

        Assert.Equal(3, inner.Received.Count);
        Assert.Equal(RecordKind.Cleared, inner.Received[1].Kind);
    }

    [Fact]
    public async Task EmitAsync_EmptyFieldsAndEmptyRawTextAreDistinctKeySpaces()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);

        var emptyFields = new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "")
        {
            Fields = new Dictionary<string, string>(),
        };
        var nullFieldsEmptyRawText = new CaptureRecord(DateTime.Now, "ore", TriggerKind.Auto, "");

        await dedupe.EmitAsync(emptyFields, CancellationToken.None);
        await dedupe.EmitAsync(nullFieldsEmptyRawText, CancellationToken.None);

        Assert.Equal(2, inner.Received.Count);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheInnerSink()
    {
        var inner = new FakeRecordSink();
        var dedupe = new ChangeDedupeSink(inner);

        await dedupe.DisposeAsync();

        Assert.True(inner.Disposed);
    }
}
