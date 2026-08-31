using GameCapture.Engine.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

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
        SubscriptionRegistry registry, ScanLoop scanLoop,
        ControlApiToken? controlApiToken, ControlApiState? controlApiState)
    {
        _app = app;
        _source = source;
        Status = status;
        Registry = registry;
        ScanLoop = scanLoop;
        ControlApiToken = controlApiToken;
        ControlApi = controlApiState;
    }

    public EngineStatus Status { get; }
    public SubscriptionRegistry Registry { get; }
    public ScanLoop ScanLoop { get; }

    /// <summary>Bearer token for the loopback control API, or <c>null</c> for a non-interactive
    /// (headless replay/video) run where no such listener exists. Held only for trusted in-process
    /// consumers (TASK-UI-04's WebView2 window) — never logged, persisted, or placed in a URL.</summary>
    internal ControlApiToken? ControlApiToken { get; }

    /// <summary>Late-bound handoff <see cref="EngineDesktopLifetime.Start"/> populates with the tray's
    /// callback bundle and metrics feed once they exist. Null for the same non-interactive case as
    /// <see cref="ControlApiToken"/>.</summary>
    internal ControlApiState? ControlApi { get; }

    /// <summary>Port the loopback control API bound to, resolved after <see cref="StartAsync"/>
    /// completes. Null until then, and forever for a non-interactive run.</summary>
    public int? ControlApiPort { get; private set; }

    /// <summary>
    /// Takes ownership of <paramref name="source"/>: it is disposed with the host, together with
    /// the retained frame the scan loop holds.
    /// </summary>
    /// <param name="sourceSelection">Monitor list/index for the control API's <c>/api/monitors</c>.
    /// Optional — a caller that never needs the control API (existing tests, a non-interactive
    /// source) can omit it.</param>
    public static EngineHost Create(
        string pipeName, EngineConfig config, OcrPipeline ocr, IFrameSource source,
        ConsoleSink sink, bool verbose, FrameSourceSelection? sourceSelection = null)
    {
        var status = new EngineStatus(ocr.LanguageTag, source.Mode.UsesReplayFlow());
        var registry = new SubscriptionRegistry(status);
        var scanLoop = new ScanLoop(source, ocr, registry, status, sink, config, verbose);

        // A headless replay/video run must never open a loopback socket — there is no UI to serve it
        // to, and the source is the one thing already known at this point that says so.
        var enableControlApi = source.Mode.IsInteractive();
        var controlApiToken = enableControlApi ? new ControlApiToken() : null;
        var controlApiState = enableControlApi ? new ControlApiState() : null;

        var app = GrpcHost.BuildGrpcHost(
            pipeName, status, registry, scanLoop, ocr, config, sink,
            sourceSelection, controlApiToken, controlApiState);

        return new EngineHost(app, source, status, registry, scanLoop, controlApiToken, controlApiState);
    }

    /// <summary>Starts serving the pipe (and, when interactive, the loopback control API). Plugins can
    /// connect and subscribe before the loop runs.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _app.StartAsync(ct);

        if (ControlApiToken is null)
            return;

        // Port 0 means the OS picked one; IServerAddressesFeature is where Kestrel reports back what
        // it actually bound once the server is listening.
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var loopback = addresses?.Addresses.FirstOrDefault(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        if (loopback is not null && Uri.TryCreate(loopback, UriKind.Absolute, out var uri))
            ControlApiPort = uri.Port;
    }

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
