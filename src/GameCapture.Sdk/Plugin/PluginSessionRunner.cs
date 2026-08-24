using GameCapture.Contracts;
using Grpc.Core;

namespace GameCapture.Sdk;

/// <summary>
/// Owns the host's connect / subscribe / consume loop, including reconnect and shutdown rules.
/// </summary>
internal sealed class PluginSessionRunner
{
    private readonly CaptureClient _client;
    private readonly TickDispatcher _dispatcher;
    private readonly PluginServices _services;
    private readonly IPluginOutput _output;
    private readonly PluginHostOptions _options;
    private readonly IGameCapturePlugin _plugin;
    private readonly string _pipeName;
    private readonly IReadOnlyList<RoiSubscription> _rois;

    public PluginSessionRunner(IGameCapturePlugin plugin, CaptureClient client,
        PluginServices services, IPluginOutput output, PluginHostOptions options, string pipeName)
    {
        _plugin = plugin;
        _client = client;
        _services = services;
        _output = output;
        _options = options;
        _pipeName = pipeName;
        _rois = plugin.Rois;
        _dispatcher = new TickDispatcher(plugin, services, output);
    }

    public async Task<(StreamEndReason Reason, int ExitCode)> RunAsync(CancellationToken ct)
    {
        // Announced once per disconnected stretch rather than per retry: a plugin started before the
        // engine would otherwise scroll the same line every few seconds.
        var announcedWait = false;
        var reconnectAttempt = 0;
        var replayMode = false;

        while (true)
        {
            if (!announcedWait)
            {
                _output.WriteLine($"waiting for engine on pipe '{_pipeName}'...");
                announcedWait = true;
            }

            try
            {
                // Inner try purely to re-type transport failures: every arm below is written against
                // the SDK's own exceptions, never RpcException. gRPC status codes are a detail of the
                // current boundary, and a host that switched on them would have to be rewritten if
                // the boundary ever changed.
                try
                {
                    var engine = await _client.WaitForEngineAsync(_options.EngineWait, ct);
                    announcedWait = false;
                    replayMode = engine.ReplayMode;

                    await using var session = await _client.TrackAsync(_plugin.Name, _rois, ct);

                    _services.Engine = engine.WithSession(session);
                    _dispatcher.OnConnected();
                    reconnectAttempt = 0;
                    _plugin.OnSessionEvent(new SessionEvent.Connected(_services.Engine));

                    WriteConnectedBanner(_services.Engine);

                    await foreach (var tick in session.Ticks(ct))
                        await _dispatcher.DispatchAsync(tick, ct);
                }
                catch (RpcException e)
                {
                    // Deliberately unfiltered by cancellation: a call this host cancelled surfaces as
                    // an OperationCanceledException (the channel sets
                    // ThrowOperationCanceledOnCancellation), so what reaches here during shutdown is
                    // only the write that was already in flight on the request stream. Translate maps
                    // its CANCELLED to SessionFaultedException, and the cancellation-filtered arm
                    // below takes it — the same "not an engine failure" the RpcException arm used to
                    // express.
                    throw ProtocolNegotiation.Translate(e, ProtocolVersion.Current);
                }
            }
            catch (ProtocolMismatchException ex)
            {
                // The one failure the loop must NOT retry. Both sides' versions are fixed for the
                // life of their processes, so dialling again can only reproduce this forever — and
                // the useful thing to tell the user is which side to upgrade, which the message says.
                //
                // FIRST, and specifically ahead of the cancellation-filtered arms below: this derives
                // from GameCaptureException, and catch clauses match in textual order, so placing it
                // after them would let a shutdown racing the refusal report an incompatible engine as
                // a clean exit 0. Cancellation cannot manufacture this exception — it needs either the
                // engine's FAILED_PRECONDITION with range trailers or the range check in
                // WaitForEngineAsync — so whenever it is raised it is the true reason the run ended.
                _output.WriteLine($"incompatible engine: {ex.Message}");
                return (StreamEndReason.Faulted, 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // our own Ctrl+C: the channel maps a cancelled call to this, not RpcException
                return (StreamEndReason.Cancelled, 0);
            }
            catch (GameCaptureException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C again: the channel's OCE mapping covers the call, but a write already in
                // flight on the request stream can still surface as CANCELLED. Not an engine failure.
                return (StreamEndReason.Cancelled, 0);
            }
            catch (TimeoutException)
            {
                continue; // engine still not serving; the line above already says we are waiting
            }
            catch (Exception ex) when (ex is GameCaptureException or OperationCanceledException)
            {
                // The engine went away mid-session. Reconnecting means a fresh subscription, and the
                // plugin's state is deliberately kept: what it already saw is still true, and the
                // first read after reconnect is a re-sighting rather than a new event.
                //
                // OperationCanceledException lands here too, and only because ct did NOT cause it:
                // the channel sets ThrowOperationCanceledOnCancellation, which maps a call the ENGINE
                // cancelled (a restart aborting the in-flight Track with CANCELLED) to the same
                // exception type as our own Ctrl+C. Caught unfiltered, that would exit the plugin 0
                // on exactly the failure this loop exists to survive.
                _output.WriteLine("engine connection lost — reconnecting");
                _plugin.OnSessionEvent(new SessionEvent.Reconnecting(++reconnectAttempt));

                // Paced: WaitForEngineAsync returns immediately whenever GetStatus answers, so an
                // engine that is up but cannot serve a Track stream (mid-shutdown, for one) would
                // otherwise spin this loop with no delay at all.
                try { await Task.Delay(_options.ReconnectDelay, ct); }
                catch (OperationCanceledException) { return (StreamEndReason.Cancelled, 0); }

                continue;
            }

            // Stream ended normally. Which of the two endings it was is the engine's to know: a
            // replay that finished is a completed run, a live engine completing the stream is a
            // shutdown, and a plugin that persists anything usually cares about the difference.
            return (replayMode ? StreamEndReason.ReplayCompleted : StreamEndReason.EngineShutdown, 0);
        }
    }

    private void WriteConnectedBanner(EngineInfo engine)
    {
        _output.WriteLine(
            $"Engine:    {engine.EngineVersion}{(engine.ReplayMode ? " (replay)" : "")}");
        _output.WriteLine($"Frame:     {(engine.FrameWidth == 0
            ? "no frame scanned yet"
            : $"{engine.FrameWidth}x{engine.FrameHeight}")}");
        _output.WriteLine($"Cadence:   {engine.ScanInterval.TotalMilliseconds:0} ms per scan");
        _output.WriteLine($"ROIs:      {string.Join(", ", _rois.Select(r => r.Id))}");
        _output.WriteLine();
        _output.WriteLine("Running. Ctrl+C to quit.");
        _output.WriteLine();
    }
}
