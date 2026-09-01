using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameCapture.Engine;

/// <summary>Native process edge for the diagnostic ROI outlines.</summary>
internal sealed class RoiOverlayRenderer : IRoiOverlayRenderer
{
    private static readonly nint PerMonitorV2 = new(-4);
    private readonly ConsoleSink _sink;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private RoiOverlayForm? _form;
    private Exception? _failure;
    private int _disposed;

    public RoiOverlayRenderer(ConsoleSink sink) => _sink = sink;

    public void Show(System.Drawing.Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureStarted();
        var form = _form!;
        try
        {
            form.BeginInvoke((Action)(() => form.Apply(monitorBounds, shapes)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _sink.WriteLine("ROI overlay window is no longer available.");
        }
    }

    public void Hide()
    {
        var form = _form;
        if (form is null || form.IsDisposed)
            return;
        try
        {
            form.BeginInvoke((Action)form.Hide);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var form = _form;
        if (form is { IsDisposed: false })
        {
            try { form.BeginInvoke((Action)form.Close); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void EnsureStarted()
    {
        if (_thread is null)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "GameCapture ROI overlay",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("ROI overlay window did not start within 10 seconds.");
        if (_failure is not null)
            throw new InvalidOperationException("ROI overlay window could not be created.", _failure);
    }

    private void Run()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            if (!AreDpiAwarenessContextsEqual(context, PerMonitorV2))
                _sink.WriteLine("ROI overlay is not Per-Monitor-V2 DPI-aware; check the engine app manifest.");

            _form = new RoiOverlayForm();
            _ready.Set();
            Application.Run(_form);
        }
        catch (Exception ex)
        {
            _failure = ex;
            _ready.Set();
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
