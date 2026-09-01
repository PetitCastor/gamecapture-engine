using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameCapture.Engine.Plugins;
using GameCapture.Engine.Tray;
// UseWindowsForms (pulled in for the tray) puts System.Windows.Forms.Timer in scope too; this hub's
// timer is the threading one, the same disambiguation MetricsReporter already needs.
using Timer = System.Threading.Timer;

namespace GameCapture.Engine;

/// <summary>
/// Fan-out for <c>WS /api/events</c>: tracks connected sockets, greets each with the current status on
/// connect, and re-polls <see cref="TrayViewBuilder.Build"/> at the tray's own cadence so the socket
/// and the tray icon never disagree about the numbers. A change-only push keeps a quiet engine from
/// spamming an idle client every tick. Plugin-state pushes are triggered by the installer and
/// launcher change notifications; the cadence poll also prunes child processes that exited on their
/// own. Every plugin snapshot is built from in-memory state and never performs a network request.
/// </summary>
/// <remarks>
/// A <see cref="WebSocket"/> is not safe for concurrent sends from two call sites, so every send for a
/// given connection — the initial greeting and every later poll broadcast — goes through that
/// connection's own <see cref="SemaphoreSlim"/>.
/// </remarks>
internal sealed class ControlApiEventHub : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = ControlApiJson.Options;

    private readonly ConcurrentDictionary<Guid, (WebSocket Socket, SemaphoreSlim SendLock)> _connections = new();
    private readonly EngineStatus _status;
    private readonly ControlApiState _state;
    private readonly bool _metricsEnabled;
    private readonly ConsoleSink _sink;
    private readonly FrameRateTracker _fps = new();
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private PluginServices? _plugins;
    private Func<IReadOnlyList<PluginRow>>? _readPluginRows;

    private long _lastPollTimestamp;
    private TrayView _current;
    private string _lastStatusJson;
    private bool _disposed;

    public ControlApiEventHub(EngineStatus status, ControlApiState state, bool metricsEnabled, ConsoleSink sink, TimeSpan interval)
    {
        _status = status;
        _state = state;
        _metricsEnabled = metricsEnabled;
        _sink = sink;
        _interval = interval;

        // Seeded synchronously so /api/status and a socket's greeting always have something to
        // return from the moment the server starts, rather than waiting out the first tick.
        _lastPollTimestamp = Stopwatch.GetTimestamp();
        _current = BuildView();
        _lastStatusJson = JsonSerializer.Serialize(_current, JsonOptions);

        // One-shot re-arming timer, the same idiom as MetricsReporter: the next poll is only
        // scheduled once the current one finishes, so a slow sample (or a broadcast fan-out growing
        // with the connection count) can never overlap the next tick.
        _timer = new Timer(_ => Poll(), null, interval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Latest computed view. Never null: seeded at construction, refreshed on the poll timer.</summary>
    public TrayView Current => _current;

    /// <summary>
    /// Accepts one client: sends the current status immediately, then blocks reading (and discarding)
    /// frames until the client closes, disconnects, or <paramref name="requestAborted"/> fires —
    /// which happens both for a dropped connection and for engine shutdown, so this method returning
    /// is exactly the signal that the socket is no longer holding anything open.
    /// </summary>
    public async Task RunAsync(WebSocket socket, CancellationToken requestAborted)
    {
        var id = Guid.NewGuid();
        var sendLock = new SemaphoreSlim(1, 1);
        _connections[id] = (socket, sendLock);

        try
        {
            await SendAsync(socket, sendLock, "status", _current, requestAborted);

            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, requestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Client dropped, or the engine is shutting down (Dispose cancels every connection below).
        }
        catch (WebSocketException)
        {
            // Connection reset from the other side; nothing to clean up beyond what finally does.
        }
        catch (ObjectDisposedException)
        {
            // Engine shutdown aborted the socket while this request was between operations.
        }
        finally
        {
            _connections.TryRemove(id, out _);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Already gone; nothing left to close cleanly.
            }
            catch (ObjectDisposedException)
            {
                // Shutdown disposed the transport before the close handshake could begin.
            }
            sendLock.Dispose();
        }
    }

    /// <summary>Pushes a plugin-state message to every connected client. Fire-and-forget: a slow or
    /// dead socket must never make the caller (an HTTP handler) wait on it.</summary>
    public void BroadcastPlugins(IReadOnlyList<PluginRow> rows) => _ = BroadcastAsync("plugins", rows);

    public void SetPlugins(PluginServices? plugins, Func<IReadOnlyList<PluginRow>>? readPluginRows = null)
    {
        if (ReferenceEquals(_plugins, plugins) && ReferenceEquals(_readPluginRows, readPluginRows))
            return;

        if (_plugins is not null)
        {
            _plugins.Installer.Changed -= OnPluginsChanged;
            _plugins.Launcher.Changed -= OnPluginsChanged;
            if (_plugins.RoiOverlays is not null)
                _plugins.RoiOverlays.Changed -= OnPluginsChanged;
        }

        _plugins = plugins;
        _readPluginRows = readPluginRows;
        if (_plugins is not null)
        {
            _plugins.Installer.Changed += OnPluginsChanged;
            _plugins.Launcher.Changed += OnPluginsChanged;
            if (_plugins.RoiOverlays is not null)
                _plugins.RoiOverlays.Changed += OnPluginsChanged;
            OnPluginsChanged();
        }
    }

    private void Poll()
    {
        if (_disposed)
            return;

        try
        {
            // RunningIds prunes processes that exited on their own and raises Changed when it does.
            _ = _plugins?.Launcher.RunningIds;

            var view = BuildView();
            _current = view;

            var json = JsonSerializer.Serialize(view, JsonOptions);
            if (json != _lastStatusJson)
            {
                _lastStatusJson = json;
                _ = BroadcastRawAsync("status", json);
            }
        }
        catch (Exception ex)
        {
            // A bad sample must never take the poll timer down; the next tick tries again. Logged
            // (unlike a bare catch) so a persistently failing sample is visible instead of the
            // socket just going quiet with no clue why.
            _sink.WriteLine($"[control-api] poll failed: {ex.Message}");
        }

        lock (_timer)
        {
            if (!_disposed)
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    private TrayView BuildView()
    {
        var now = Stopwatch.GetTimestamp();
        var snapshot = _status.Snapshot();
        // FrameRateTracker is single-writer by contract; the poll timer is its only caller, exactly
        // like the tray's own UI timer is FrameRateTracker's only caller there.
        _fps.Observe(snapshot.FrameSeq, Stopwatch.GetElapsedTime(_lastPollTimestamp, now));
        _lastPollTimestamp = now;

        return TrayViewBuilder.Build(snapshot, _state.LatestMetrics, _fps.Fps, _metricsEnabled);
    }

    private Task BroadcastAsync<T>(string type, T data)
        => BroadcastRawAsync(type, JsonSerializer.Serialize(data, JsonOptions));

    private async Task BroadcastRawAsync(string type, string dataJson)
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"data\":{dataJson}}}");

        foreach (var (_, connection) in _connections)
        {
            var (socket, sendLock) = connection;
            try
            {
                await sendLock.WaitAsync();
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }
                finally
                {
                    sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // The socket died between the state check and the send; RunAsync's own loop notices
                // and removes it — one dead connection must never fault the broadcast to every other.
            }
        }
    }

    private static async Task SendAsync<T>(WebSocket socket, SemaphoreSlim sendLock, string type, T data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"data\":{JsonSerializer.Serialize(data, JsonOptions)}}}");
        await sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Stops the poll timer and aborts every open socket. Registered against
    /// <c>IHostApplicationLifetime.ApplicationStopping</c> so a live WebSocket is torn down the
    /// instant shutdown begins, rather than left for Kestrel's own graceful-shutdown timeout to
    /// eventually reap — the drain contract in <see cref="EngineHost.StopAsync"/> is about the gRPC
    /// registry only and must not also end up waiting on this.
    /// </summary>
    public void Dispose()
    {
        lock (_timer)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        SetPlugins(null, null);

        _timer.Dispose();

        foreach (var (_, connection) in _connections)
        {
            try
            {
                connection.Socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        _connections.Clear();
    }

    private void OnPluginsChanged()
    {
        if (_plugins is null || _readPluginRows is null)
            return;

        BroadcastPlugins(_readPluginRows());
    }
}
