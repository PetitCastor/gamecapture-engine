namespace GameCapture.Sdk;

/// <summary>
/// Settings for an <c>"overlay"</c> <see cref="SinkSpec"/>. Core carries only this portable JSON
/// shape; the opt-in <c>GameCapture.Sdk.Overlay</c> package owns the Windows implementation.
/// </summary>
public sealed class OverlaySpec
{
    /// <summary>How the overlay is positioned. Defaults to the top centre of the primary screen.</summary>
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopCenter;

    /// <summary>Absolute horizontal position in physical pixels when <see cref="Anchor"/> is custom.</summary>
    public int X { get; set; }

    /// <summary>Absolute vertical position in physical pixels when <see cref="Anchor"/> is custom.</summary>
    public int Y { get; set; }

    /// <summary>Horizontal offset in physical pixels applied after anchoring.</summary>
    public int OffsetX { get; set; }

    /// <summary>Vertical offset in physical pixels applied after anchoring.</summary>
    public int OffsetY { get; set; } = 24;

    /// <summary>Overlay width in physical pixels.</summary>
    public int Width { get; set; } = 560;

    /// <summary>Overlay height in physical pixels.</summary>
    public int Height { get; set; } = 88;

    /// <summary>Installed font family used for the observation text.</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Font size in points.</summary>
    public float FontSize { get; set; } = 24;

    /// <summary>Foreground colour as a named colour or HTML hex value.</summary>
    public string ForegroundColor { get; set; } = "#FFFFFF";

    /// <summary>Background pill colour as a named colour or HTML hex value.</summary>
    public string BackgroundColor { get; set; } = "#111827";

    /// <summary>Background alpha from 0 (transparent) to 255 (opaque).</summary>
    public int BackgroundAlpha { get; set; } = 224;

    /// <summary>Rounded pill corner radius in physical pixels.</summary>
    public int CornerRadius { get; set; } = 16;

    /// <summary>Inset between the pill edge and text in physical pixels.</summary>
    public int Padding { get; set; } = 16;

    /// <summary>
    /// Text template whose <c>{key}</c> placeholders read <see cref="CaptureRecord.Fields"/>.
    /// Blank uses <see cref="CaptureRecord.RawText"/> directly.
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>Milliseconds without an observation before auto-hide; zero disables auto-hide.</summary>
    public int LingerMs { get; set; } = 5000;
}
