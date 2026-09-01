using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameCapture.Engine;

/// <summary>Native process edge for the diagnostic ROI outlines.</summary>
internal sealed class RoiOverlayRenderer : IRoiOverlayRenderer
{
    private static readonly nint PerMonitorV2 = new(-4);
    private readonly ConsoleSink _sink;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private RoiOverlayForm? _form;
    private System.Drawing.Rectangle _requestedBounds;
    private IReadOnlyList<RoiOverlayShape> _requestedShapes = [];
    private bool _visible;
    private bool _disposed;

    public RoiOverlayRenderer(ConsoleSink sink) => _sink = sink;

    public void Show(System.Drawing.Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes)
    {
        RoiOverlayForm? form;
        lock (_gate)
        {
            if (_disposed)
                return;

            _requestedBounds = monitorBounds;
            _requestedShapes = shapes;
            _visible = true;
            EnsureStartedUnderLock();
            form = _form;
        }

        Post(form, ApplyRequestedState);
    }

    public void Hide()
    {
        RoiOverlayForm? form;
        lock (_gate)
        {
            if (_disposed)
                return;

            _visible = false;
            form = _form;
        }

        Post(form, ApplyRequestedState);
    }

    public void Dispose()
    {
        RoiOverlayForm? form;
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _visible = false;
            form = _form;
            thread = _thread;
        }

        Post(form, () =>
        {
            form!.Close();
            Application.ExitThread();
        });
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void EnsureStartedUnderLock()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "GameCapture ROI overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            if (!AreDpiAwarenessContextsEqual(context, PerMonitorV2))
                _sink.WriteLine("ROI overlay is not Per-Monitor-V2 DPI-aware; check the engine app manifest.");

            var form = new RoiOverlayForm();
            form.CreateControl();
            lock (_gate)
            {
                if (_disposed)
                {
                    form.Dispose();
                    return;
                }

                _form = form;
            }

            ApplyRequestedState();
            Application.Run();
        }
        catch (Exception ex)
        {
            _sink.WriteLine($"ROI overlay window could not be created: {ex.Message}");
        }
    }

    private void ApplyRequestedState()
    {
        RoiOverlayForm? form;
        System.Drawing.Rectangle bounds;
        IReadOnlyList<RoiOverlayShape> shapes;
        bool visible;
        lock (_gate)
        {
            if (_disposed)
                return;

            form = _form;
            bounds = _requestedBounds;
            shapes = _requestedShapes;
            visible = _visible;
        }

        if (form is null || form.IsDisposed)
            return;
        if (visible)
            form.Apply(bounds, shapes);
        else
            form.Hide();
    }

    private static void Post(RoiOverlayForm? form, Action action)
    {
        if (form is null || form.IsDisposed)
            return;

        try
        {
            form.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
