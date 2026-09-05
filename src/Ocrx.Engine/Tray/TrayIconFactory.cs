using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ocrx.Engine.Tray;

/// <summary>
/// Builds the tray icon for each <see cref="TrayIconState"/> as a coloured dot, drawn at runtime so
/// the engine ships no <c>.ico</c> assets. Icons are created once and cached; the caller disposes the
/// factory (which disposes them) when the tray shuts down.
/// </summary>
/// <remarks>
/// UI/interop edge, excluded from the coverage gate alongside the other process-edge files: it does
/// GDI drawing and a handle free that cannot be asserted meaningfully without a display.
/// </remarks>
public sealed class TrayIconFactory : IDisposable
{
    // Task Manager-ish reading at a glance: grey idle, green live-with-plugin, blue replay, red fault.
    private static readonly IReadOnlyDictionary<TrayIconState, Color> Palette = new Dictionary<TrayIconState, Color>
    {
        [TrayIconState.Idle] = Color.Gray,
        [TrayIconState.Capturing] = Color.LimeGreen,
        [TrayIconState.Replay] = Color.DodgerBlue,
        [TrayIconState.Error] = Color.Firebrick,
    };

    private readonly Dictionary<TrayIconState, Icon> _cache = [];

    public Icon For(TrayIconState state)
    {
        if (_cache.TryGetValue(state, out var cached))
            return cached;

        var icon = Create(Palette.TryGetValue(state, out var color) ? color : Color.Gray);
        _cache[state] = icon;
        return icon;
    }

    private static Icon Create(Color color)
    {
        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }

        // GetHicon hands back an unmanaged HICON we own. Wrap it, clone into a self-contained managed
        // Icon, then destroy the handle — otherwise the tray leaks one GDI handle per icon built.
        var handle = bmp.GetHicon();
        try
        {
            using var owned = Icon.FromHandle(handle);
            return (Icon)owned.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
            icon.Dispose();
        _cache.Clear();
    }
}
