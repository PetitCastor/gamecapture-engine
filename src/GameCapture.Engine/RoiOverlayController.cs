using System.Drawing;
using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using GameCapture.Engine.Plugins;

namespace GameCapture.Engine;

/// <summary>Owns ROI-overlay eligibility and lifecycle; native drawing stays behind <see cref="IRoiOverlayRenderer"/>.</summary>
internal sealed class RoiOverlayController : IDisposable
{
    private readonly PluginLauncher _launcher;
    private readonly SubscriptionRegistry _subscriptions;
    private readonly EngineStatus _status;
    private readonly FrameSourceSelection _selection;
    private readonly IRoiOverlayRenderer _renderer;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CatalogEntry> _visible = new(StringComparer.Ordinal);
    private bool _hadFrame;
    private bool _disposed;

    internal RoiOverlayController(
        PluginLauncher launcher,
        SubscriptionRegistry subscriptions,
        EngineStatus status,
        FrameSourceSelection selection,
        IRoiOverlayRenderer renderer)
    {
        _launcher = launcher;
        _subscriptions = subscriptions;
        _status = status;
        _selection = selection;
        _renderer = renderer;
        _launcher.Changed += OnSubscriptionsChanged;
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
        }

        _launcher.Changed -= OnSubscriptionsChanged;
        _subscriptions.Changed -= OnSubscriptionsChanged;
        _status.FrameChanged -= OnFrameChanged;
        _renderer.Dispose();
    }

    private void OnSubscriptionsChanged()
    {
        Refresh();
        Changed?.Invoke();
    }

    private void OnFrameChanged()
    {
        var frame = _status.Snapshot();
        var hasFrame = frame.FrameWidth > 0 && frame.FrameHeight > 0;
        var announce = hasFrame != _hadFrame;
        _hadFrame = hasFrame;
        Refresh();
        if (announce)
            Changed?.Invoke();
    }

    private void Refresh()
    {
        KeyValuePair<string, CatalogEntry>[] requested;
        lock (_gate)
        {
            if (_disposed)
                return;
            requested = _visible.ToArray();
        }

        var shapes = new List<RoiOverlayShape>();
        Rectangle monitorBounds = Rectangle.Empty;
        var removed = false;
        foreach (var (id, entry) in requested)
        {
            if (!TryBuild(entry, out var bounds, out var built, out _))
            {
                lock (_gate)
                    removed |= _visible.Remove(id);
                continue;
            }

            monitorBounds = bounds;
            shapes.AddRange(built);
        }

        if (shapes.Count == 0)
            _renderer.Hide();
        else
            _renderer.Show(monitorBounds, shapes);

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
        if (!_launcher.IsRunning(entry.Id))
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
