using GameCapture.Engine.Grpc;
using Microsoft.AspNetCore.Builder;

namespace GameCapture.Engine;

/// <summary>
/// Composes the engine: a frame source, the scan loop that drains it, the subscription registry
/// they share, and the gRPC host that exposes all of it on a named pipe. Program and the
/// integration tests both go through this facade, so a test always runs the wiring the real
/// engine runs — the failure mode a split like this is most prone to.
/// </summary>
internal sealed class EngineHost : IAsyncDisposable
{
    /// <summary>How long to give registered clients to observe their completed channel, drain any
    /// buffered ticks and return from <c>Track</c> before the pipe is torn down. Without this,
    /// <see cref="StopAsync"/> can tear the host down the instant <see cref="RunScanAsync"/>
    /// returns — which is as soon as <c>ScanLoop</c> marks every channel complete, not once a
    /// client's <c>Track</c> call has actually had a turn to notice, flush its last tick(s) and
    /// return — and the client then sees its stream cut instead of a clean end.</summary>
    private static readonly TimeSpan ClientDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>Poll interval while waiting out <see cref="ClientDrainGrace"/>.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly WebApplication _app;
    private readonly IFrameSource _source;
    private bool _stopped;

    private EngineHost(WebApplication app, IFrameSource source, EngineStatus status,
        SubscriptionRegistry registry, ScanLoop scanLoop)
    {
        _app = app;
        _source = source;
        Status = status;
        Registry = registry;
        ScanLoop = scanLoop;
    }

    public EngineStatus Status { get; }
    public SubscriptionRegistry Registry { get; }
    public ScanLoop ScanLoop { get; }

    /// <summary>
    /// Takes ownership of <paramref name="source"/>: it is disposed with the host, together with
    /// the retained frame the scan loop holds.
    /// </summary>
    public static EngineHost Create(
        string pipeName, EngineConfig config, OcrPipeline ocr, IFrameSource source,
        ConsoleSink sink, bool verbose)
    {
        var status = new EngineStatus(ocr.LanguageTag, source.Mode.UsesReplayFlow());
        var registry = new SubscriptionRegistry(status);
        var scanLoop = new ScanLoop(source, ocr, registry, status, sink, config, verbose);
        var app = GrpcHost.BuildGrpcHost(pipeName, status, registry, scanLoop, ocr, config);

        return new EngineHost(app, source, status, registry, scanLoop);
    }

    /// <summary>Starts serving the pipe. Plugins can connect and subscribe before the loop runs.</summary>
    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    /// <summary>
    /// Runs the scan loop until cancellation (live) or corpus exhaustion (replay). Returns only
    /// after every client's Track stream has been completed.
    /// </summary>
    public Task RunScanAsync(CancellationToken ct) => ScanLoop.RunAsync(ct);

    public async Task StopAsync()
    {
        if (_stopped)
            return;

        _stopped = true;

        // Best-effort: a client that stopped reading its stream (or never will) must not hang
        // shutdown forever, so this gives up and stops the host anyway once the grace period
        // elapses rather than waiting on Registry.Snapshot() to reach zero unconditionally.
        using var drainCts = new CancellationTokenSource(ClientDrainGrace);
        try
        {
            while (Registry.Snapshot().Count > 0)
                await Task.Delay(DrainPollInterval, drainCts.Token);
        }
        catch (OperationCanceledException) { /* grace period elapsed; stop anyway */ }

        await _app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _app.DisposeAsync();
        ScanLoop.Dispose();
        _source.Dispose();
    }
}
