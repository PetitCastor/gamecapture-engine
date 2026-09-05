using System.Drawing;
using Ocrx.Contracts;
using Ocrx.Contracts.Proto;
using Ocrx.Engine.Plugins;

namespace Ocrx.Engine;

/// <summary>Owns ROI-overlay eligibility and lifecycle; native drawing stays behind <see cref="IRoiOverlayRenderer"/>.</summary>
internal sealed class RoiOverlayController : IDisposable
{
    private readonly Func<string, bool> _isPluginRunning;
    private readonly Action<Action> _subscribeLauncherChanged;
    private readonly Action<Action> _unsubscribeLauncherChanged;
    private readonly SubscriptionRegistry _subscriptions;
    private readonly EngineStatus _status;
    private readonly FrameSourceSelection _selection;
    private readonly IRoiOverlayRenderer _renderer;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CatalogEntry> _visible = new(StringComparer.Ordinal);
    private bool _hadFrame;
    private long _generation;
    private bool _disposed;

    internal RoiOverlayController(
        PluginLauncher launcher,
        SubscriptionRegistry subscriptions,
        EngineStatus status,
        FrameSourceSelection selection,
        IRoiOverlayRenderer renderer)
        : this(
            launcher.IsRunning,
            handler => launcher.Changed += handler,
            handler => launcher.Changed -= handler,
            subscriptions,
            status,
            selection,
            renderer)
    {
    }

    internal RoiOverlayController(
        Func<string, bool> isPluginRunning,
        Action<Action> subscribeLauncherChanged,
        Action<Action> unsubscribeLauncherChanged,
        SubscriptionRegistry subscriptions,
        EngineStatus status,
        FrameSourceSelection selection,
        IRoiOverlayRenderer renderer)
    {
        _isPluginRunning = isPluginRunning;
        _subscribeLauncherChanged = subscribeLauncherChanged;
        _unsubscribeLauncherChanged = unsubscribeLauncherChanged;
        _subscriptions = subscriptions;
        _status = status;
        _selection = selection;
        _renderer = renderer;
        _subscribeLauncherChanged(OnSubscriptionsChanged);
        _subscriptions.Changed += OnSubscriptionsChanged;
        _status.FrameChanged += OnFrameChanged;
    }

    public event Action? Changed;

    public RoiOverlayState GetState(CatalogEntry entry)
    {
        var canShow = TryBuild(entry, out _, out _, out _);
        lock (_gate)
            return new RoiOverlayState(canShow, canShow && _visible.ContainsKey(entry.Id));
    }

    public RoiOverlayState SetVisible(CatalogEntry entry, bool visible)
    {
        // Both directions go through the same eligibility gate as the row. This keeps the HTTP
        // action from becoming a back door for uninstalled, stale, replay, or disconnected rows.
        if (!TryBuild(entry, out _, out _, out var error))
            throw new InvalidOperationException(error);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (visible)
                _visible[entry.Id] = entry;
            else
                _visible.Remove(entry.Id);
            _generation++;
        }

        Refresh();
        Changed?.Invoke();
        return GetState(entry);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _visible.Clear();
            _generation++;
        }

        _unsubscribeLauncherChanged(OnSubscriptionsChanged);
        _subscriptions.Changed -= OnSubscriptionsChanged;
        _status.FrameChanged -= OnFrameChanged;
        _renderer.Dispose();
    }

    private void OnSubscriptionsChanged()
    {
        lock (_gate)
            _generation++;
        Refresh();
        Changed?.Invoke();
    }

    private void OnFrameChanged()
    {
        var frame = _status.Snapshot();
        var hasFrame = frame.FrameWidth > 0 && frame.FrameHeight > 0;
        bool announce;
        lock (_gate)
        {
            announce = hasFrame != _hadFrame;
            _hadFrame = hasFrame;
            _generation++;
        }
        Refresh();
        if (announce)
            Changed?.Invoke();
    }

    private void Refresh()
    {
        KeyValuePair<string, CatalogEntry>[] requested;
        long generation;
        lock (_gate)
        {
            if (_disposed)
                return;
            requested = _visible.ToArray();
            generation = _generation;
        }

        var shapes = new List<RoiOverlayShape>();
        var failedIds = new List<string>();
        Rectangle monitorBounds = Rectangle.Empty;
        var removed = false;
        foreach (var (id, entry) in requested)
        {
            if (!TryBuild(entry, out var bounds, out var built, out _))
            {
                removed = true;
                failedIds.Add(id);
                continue;
            }

            monitorBounds = bounds;
            shapes.AddRange(built);
        }

        lock (_gate)
        {
            if (_disposed || generation != _generation)
                return;

            if (removed)
            {
                foreach (var id in failedIds)
                    _visible.Remove(id);
                _generation++;
            }

            if (shapes.Count == 0)
                _renderer.Hide();
            else
                _renderer.Show(monitorBounds, shapes);
        }

        if (removed)
            Changed?.Invoke();
    }

    private bool TryBuild(
        CatalogEntry entry,
        out Rectangle monitorBounds,
        out IReadOnlyList<RoiOverlayShape> shapes,
        out string error)
    {
        monitorBounds = Rectangle.Empty;
        shapes = [];
        error = "";
        if (_selection.Source.Mode != FrameSourceMode.LiveCapture)
            return Fail("ROI overlays are available only during live capture.", out error);
        if (_selection.CaptureMonitor is not { } monitor)
            return Fail("The selected capture monitor is unavailable.", out error);
        if (!MonitorCapture.TryGetBounds(monitor.Handle, out monitorBounds))
            monitorBounds = monitor.Bounds;
        if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
            return Fail("The selected capture monitor is unavailable.", out error);
        if (!_isPluginRunning(entry.Id))
            return Fail($"{entry.Name} is not running.", out error);
        if (string.IsNullOrWhiteSpace(entry.ClientName))
            return Fail($"{entry.Name} does not publish a capture client identity.", out error);

        var frame = _status.Snapshot();
        if (frame.FrameWidth == 0 || frame.FrameHeight == 0)
            return Fail("No captured frame is available yet.", out error);

        var clients = _subscriptions.Snapshot()
            .Where(client => string.Equals(client.Name, entry.ClientName, StringComparison.Ordinal))
            .Where(client => client.Rois.Count > 0)
            .ToArray();
        if (clients.Length == 0)
            return Fail($"{entry.Name} has not subscribed any active ROIs yet.", out error);

        var frameSize = new Size(checked((int)frame.FrameWidth), checked((int)frame.FrameHeight));
        var result = new List<RoiOverlayShape>();
        foreach (var client in clients)
        foreach (var spec in client.Rois)
        {
            var reference = (spec.Rect ?? new Rect()).ToRoiRect();
            var label = $"{entry.Name} / {spec.Id}";
            try
            {
                var crop = RoiFrameMapper.MapAccepted(reference, frameSize.Width, frameSize.Height);
                result.Add(new RoiOverlayShape(
                    new Rectangle((int)crop.X, (int)crop.Y, (int)crop.Width, (int)crop.Height), label, IsInvalid: false));
            }
            catch (ArgumentOutOfRangeException)
            {
                var raw = RoiFrameMapper.ProjectRequested(reference, frameSize.Width, frameSize.Height);
                var clipped = Rectangle.Intersect(new Rectangle(Point.Empty, monitorBounds.Size), raw);
                if (clipped.Width > 0 && clipped.Height > 0)
                    result.Add(new RoiOverlayShape(clipped, $"{label} (Invalid)", IsInvalid: true));
            }
        }

        shapes = result;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RoiOverlayController));
    }
}
