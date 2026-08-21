using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GameCapture.Sdk;

namespace GameCapture.Sdk.Overlay;

[SupportedOSPlatform("windows")]
internal sealed class Win32OverlayWindow : IOverlayWindow
{
    private const int ErrorClassAlreadyExists = 1410;
    private const int SmCxScreen = 0;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint UlwAlpha = 0x00000002;
    private const uint WmClose = 0x0010;
    private const uint WmQuit = 0x0012;
    private const uint WmDestroy = 0x0002;
    private const uint WmTimer = 0x0113;
    private const uint WmAppShow = 0x8001;
    private const uint WmAppHide = 0x8002;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const nuint LingerTimerId = 1;
    private const int IdcArrow = 32512;
    private const string WindowClassName = "GameCaptureSdkOverlayWindow";

    private static readonly ConcurrentDictionary<nint, Win32OverlayWindow> Windows = new();
    private static readonly WindowProcedureDelegate WindowProcedureCallback = WindowProcedure;

    private readonly OverlaySpec _options;
    private readonly IPluginOutput _log;
    private readonly Color _foreground;
    private readonly Color _background;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private Exception? _startupFailure;
    private nint _window;
    private uint _threadId;
    private string _pendingText = "";
    private int _pendingLingerMs;
    private int _started;
    private int _disposed;

    static Win32OverlayWindow()
    {
        SetProcessDpiAwarenessContext(new nint(-4));
    }

    public Win32OverlayWindow(OverlaySpec options, IPluginOutput log)
    {
        _options = options;
        _log = log;
        _foreground = ParseColor(options.ForegroundColor, nameof(options.ForegroundColor));
        _background = ParseColor(options.BackgroundColor, nameof(options.BackgroundColor));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _thread = new Thread(RunMessagePump)
        {
            IsBackground = true,
            Name = "GameCapture overlay",
        };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("overlay window did not start within 10 seconds");
        if (_startupFailure is not null)
            throw new InvalidOperationException("overlay window could not be created", _startupFailure);
    }

    public void Show(string text, TimeSpan linger)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        Volatile.Write(ref _pendingText, text);
        Volatile.Write(ref _pendingLingerMs, checked((int)linger.TotalMilliseconds));
        PostMessageW(_window, WmAppShow, 0, 0);
    }

    public void Hide()
    {
        if (Volatile.Read(ref _disposed) == 0)
            PostMessageW(_window, WmAppHide, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var window = Volatile.Read(ref _window);
        if (window != 0)
            PostMessageW(window, WmClose, 0, 0);
        else if (_threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, 0, 0);

        var stopped = _thread is not { } thread
            || thread == Thread.CurrentThread
            || thread.Join(TimeSpan.FromSeconds(10));
        if (stopped)
            _ready.Dispose();
        else
            _log.WriteLine("overlay window did not stop within 10 seconds");
    }

    private void RunMessagePump()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            var module = GetModuleHandleW(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = WindowProcedureCallback,
                Instance = module,
                Cursor = LoadCursorW(0, new nint(IdcArrow)),
                ClassName = WindowClassName,
            };

            if (RegisterClassExW(ref windowClass) == 0
                && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
            }

            var (x, y) = Position();
            var window = CreateWindowExW(
                WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                WindowClassName,
                "",
                WsPopup,
                x,
                y,
                _options.Width,
                _options.Height,
                0,
                0,
                module,
                0);
            if (window == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");

            Volatile.Write(ref _window, window);
            Windows[window] = this;
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        var current = Interlocked.Exchange(ref _window, 0);
        if (current != 0)
        {
            Windows.TryRemove(current, out _);
            DestroyWindow(current);
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(window, out var owner))
        {
            switch (message)
            {
                case WmAppShow:
                    owner.ShowPending();
                    return 0;
                case WmAppHide:
                    owner.HideNow();
                    return 0;
                case WmTimer when wParam == LingerTimerId:
                    owner.HideNow();
                    return 0;
                case WmClose:
                    DestroyWindow(window);
                    return 0;
                case WmDestroy:
                    Windows.TryRemove(window, out _);
                    Volatile.Write(ref owner._window, 0);
                    PostQuitMessage(0);
                    return 0;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowPending()
    {
        try
        {
            Draw(Volatile.Read(ref _pendingText));
            ShowWindow(_window, SwShowNoActivate);

            KillTimer(_window, LingerTimerId);
            var lingerMs = Volatile.Read(ref _pendingLingerMs);
            if (lingerMs > 0 && SetTimer(_window, LingerTimerId, (uint)lingerMs, 0) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTimer failed");
        }
        catch (Exception ex)
        {
            ShowWindow(_window, SwHide);
            _log.WriteLine($"overlay render failed: {ex.Message}");
        }
    }

    private void HideNow()
    {
        KillTimer(_window, LingerTimerId);
        ShowWindow(_window, SwHide);
    }

    private void Draw(string text)
    {
        using var bitmap = new Bitmap(_options.Width, _options.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var path = RoundedRectangle(_options.Width, _options.Height, _options.CornerRadius))
        using (var background = new SolidBrush(Color.FromArgb(_options.BackgroundAlpha, _background)))
        using (var foreground = new SolidBrush(_foreground))
        using (var font = new Font(_options.FontFamily, _options.FontSize, FontStyle.Regular, GraphicsUnit.Point))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        })
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.FillPath(background, path);

            var padding = _options.Padding;
            var textBounds = new RectangleF(
                padding,
                padding,
                Math.Max(0, _options.Width - (padding * 2)),
                Math.Max(0, _options.Height - (padding * 2)));
            graphics.DrawString(text, font, foreground, textBounds, format);
        }

        PremultiplyAlpha(bitmap);
        PushBitmap(bitmap);
    }

    private void PushBitmap(Bitmap bitmap)
    {
        var screen = GetDC(0);
        if (screen == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed");

        var memory = CreateCompatibleDC(screen);
        if (memory == 0)
        {
            ReleaseDC(0, screen);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed");
        }

        nint nativeBitmap = 0;
        nint previous = 0;
        try
        {
            nativeBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, nativeBitmap);
            if (previous == 0 || previous == new nint(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SelectObject failed");

            var (x, y) = Position();
            var destination = new NativePoint(x, y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(_options.Width, _options.Height);
            var blend = new BlendFunction
            {
                BlendOperation = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            if (!UpdateLayeredWindow(
                _window, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed");
            }
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1))
                SelectObject(memory, previous);
            if (nativeBitmap != 0)
                DeleteObject(nativeBitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private (int X, int Y) Position()
    {
        var x = _options.Anchor == OverlayAnchor.Custom
            ? _options.X
            : (GetSystemMetrics(SmCxScreen) - _options.Width) / 2;
        var y = _options.Anchor == OverlayAnchor.Custom ? _options.Y : 0;
        return (x + _options.OffsetX, y + _options.OffsetY);
    }

    private static GraphicsPath RoundedRectangle(int width, int height, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(width, height));
        if (diameter <= 0)
        {
            path.AddRectangle(new Rectangle(0, 0, width, height));
            return path;
        }

        var arc = new Rectangle(0, 0, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = width - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = height - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = 0;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void PremultiplyAlpha(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            var pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, length);

            for (var index = 0; index < pixels.Length; index += 4)
            {
                var alpha = pixels[index + 3];
                pixels[index] = (byte)((pixels[index] * alpha + 127) / 255);
                pixels[index + 1] = (byte)((pixels[index + 1] * alpha + 127) / 255);
                pixels[index + 2] = (byte)((pixels[index + 2] * alpha + 127) / 255);
            }

            Marshal.Copy(pixels, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Color ParseColor(string value, string parameterName)
    {
        try
        {
            var color = ColorTranslator.FromHtml(value);
            if (!color.IsEmpty)
                return color;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new ArgumentException($"overlay colour '{value}' is invalid", parameterName, ex);
        }

        throw new ArgumentException($"overlay colour '{value}' is invalid", parameterName);
    }

    [UnconditionalSuppressMessage("Interoperability", "SYSLIB1054",
        Justification = "These callbacks and mutable Win32 structs require classic DllImport marshalling.")]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint window, nuint eventId, uint milliseconds, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint eventId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc,
        ref NativePoint destination, ref NativeSize size, nint sourceDc, ref NativePoint source,
        uint colorKey, ref BlendFunction blend, uint flags);

    private delegate nint WindowProcedureDelegate(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WindowProcedureDelegate WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOperation;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}
