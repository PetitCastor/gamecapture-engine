using System.Threading.Channels;
using GameCapture.Contracts;

namespace GameCapture.Sdk;

/// <summary>
/// Composes a run's record sinks and delivers queued records to them in order, off the tick thread.
/// </summary>
internal sealed class PluginOutputPipeline
{
    private readonly IPluginOutput _output;
    private readonly IRecordSink _sink;
    private readonly Channel<CaptureRecord> _outbox =
        Channel.CreateUnbounded<CaptureRecord>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _drain;

    public PluginOutputPipeline(IRecordSink sink, IPluginOutput output)
    {
        _sink = sink;
        _output = output;
    }

    /// <summary>
    /// Builds the configured sinks in declaration order. If a later spec is invalid, every sink
    /// built before it is disposed before the configuration error is returned to the host.
    /// </summary>
    public static async Task<PluginOutputPipeline> CreateAsync(PluginHostOptions options,
        PluginConfig config, Func<bool> isReplay, IPluginOutput output)
    {
        List<IRecordSink> sinks = [];
        try
        {
            // Explicit options win over config: tests and embedding hosts must not accidentally
            // construct a config-driven sink that writes to a real path.
            if (options.Sinks is { } explicitSinks)
                sinks.AddRange(explicitSinks);
            else
                foreach (var spec in config.Outputs)
                    sinks.Add(SinkFactory.Build(spec, isReplay, output, options.OverlayFactory));
        }
        catch (ArgumentException)
        {
            // A spec past the bad one never got built, but everything before it may own resources.
            foreach (var built in sinks)
                await built.DisposeAsync();
            throw;
        }

        // Keep the legacy callback as one more ordered sink in the same asynchronous pipeline.
        if (options.RecordSink is { } legacy)
            sinks.Add(new DelegateRecordSink(legacy));

        return new PluginOutputPipeline(new CompositeRecordSink(sinks), output);
    }

    public void Enqueue(CaptureRecord record) => _outbox.Writer.TryWrite(record);

    /// <summary>Starts the run's single background sink-drain task.</summary>
    public void Start(CancellationToken ct) => _drain = Task.Run(() => DrainAsync(ct));

    /// <summary>Flushes every queued record before disposing the composed sink.</summary>
    public async Task CompleteAndDrainAsync()
    {
        _outbox.Writer.TryComplete();
        if (_drain is not null)
            await _drain;
        await _sink.DisposeAsync();
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        // CancellationToken.None here, deliberately: a cancelled run still drains what is already
        // queued. The ct passed to EmitAsync is what lets a sink abort a slow write.
        await foreach (var record in _outbox.Reader.ReadAllAsync(CancellationToken.None))
        {
            try { await _sink.EmitAsync(record, ct); }
            catch (Exception ex) { _output.WriteLine($"sink error: {ex.Message}"); }
        }
    }
}
