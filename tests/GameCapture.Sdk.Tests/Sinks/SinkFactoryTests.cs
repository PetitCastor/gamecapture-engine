using Xunit;

namespace GameCapture.Sdk.Tests.Sinks;

/// <summary>
/// <see cref="SinkFactory.Build"/> — the one place a <see cref="SinkSpec"/> becomes an
/// <see cref="IRecordSink"/>. What matters here is the mapping and validation, not any one sink's
/// own I/O behaviour, which the per-sink test files already cover.
/// </summary>
public class SinkFactoryTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"gc-sf-{Guid.NewGuid():N}.jsonl");
    private static readonly RecordingOutput Log = new();

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("http")]
    public async Task Build_KnownTypes_ProducesAWorkingSink(string type)
    {
        var spec = type switch
        {
            "json" => new SinkSpec { Type = "json", Path = TempPath() },
            "csv" => new SinkSpec { Type = "csv", Path = TempPath() },
            "http" => new SinkSpec { Type = "http", Url = "http://example.test/records" },
            _ => throw new InvalidOperationException(),
        };

        await using var sink = SinkFactory.Build(spec, () => false, Log, overlayFactory: null);

        Assert.IsType<ChangeDedupeSink>(sink);
    }

    [Fact]
    public async Task Build_DedupeOnChangeFalse_SkipsTheDecorator()
    {
        var spec = new SinkSpec { Type = "json", Path = TempPath(), DedupeOnChange = false };

        await using var sink = SinkFactory.Build(spec, () => false, Log, overlayFactory: null);

        Assert.IsType<JsonRecordSink>(sink);
    }

    [Fact]
    public async Task Build_UnknownType_Throws()
    {
        var spec = new SinkSpec { Type = "carrier-pigeon" };

        var ex = Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
        Assert.Contains("carrier-pigeon", ex.Message);
    }

    [Fact]
    public void Build_JsonWithoutPath_Throws()
    {
        var spec = new SinkSpec { Type = "json" };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_CsvWithoutPath_Throws()
    {
        var spec = new SinkSpec { Type = "csv" };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_HttpWithoutUrl_Throws()
    {
        var spec = new SinkSpec { Type = "http" };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_WhitespaceOnlyPath_Throws()
    {
        var spec = new SinkSpec { Type = "json", Path = "   " };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_WhitespaceOnlyUrl_Throws()
    {
        var spec = new SinkSpec { Type = "http", Url = "   " };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_HttpWithANonPositiveTimeout_Throws()
    {
        var spec = new SinkSpec { Type = "http", Url = "http://example.test/records", TimeoutSeconds = 0 };

        // ArgumentOutOfRangeException : ArgumentException, so the host's malformed-spec catch still
        // takes it — ThrowsAny checks the supertype, where Throws<T> requires an exact type match.
        Assert.ThrowsAny<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public void Build_HttpWithAnInvalidUrl_Throws()
    {
        var spec = new SinkSpec { Type = "http", Url = "not a url" };

        Assert.Throws<ArgumentException>(
            () => SinkFactory.Build(spec, () => false, Log, overlayFactory: null));
    }

    [Fact]
    public async Task Build_Overlay_WithNoFactoryRegistered_ProducesANoOpSink()
    {
        var spec = new SinkSpec { Type = "overlay" };

        await using var sink = SinkFactory.Build(spec, () => false, Log, overlayFactory: null);

        // Never thrown for a plugin that simply does not reference the overlay package.
        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "text"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Build_Overlay_IsNeverDedupeWrapped_EvenWhenSpecAsksForIt()
    {
        var factory = new FakeOverlaySinkFactory();
        var spec = new SinkSpec { Type = "overlay", DedupeOnChange = true };

        var sink = SinkFactory.Build(spec, () => false, Log, factory);

        // Overlay wants every observation and clear — ChangeDedupeSink would collapse repeats.
        Assert.Same(factory.LastBuilt, sink);
        await sink.DisposeAsync();
    }

    private sealed class FakeOverlaySinkFactory : IOverlaySinkFactory
    {
        public IRecordSink? LastBuilt { get; private set; }

        public IRecordSink Create(OverlaySpec spec, IPluginOutput log)
            => LastBuilt = new FakeRecordSink();
    }
}
