namespace GameCapture.Sdk;

/// <summary>Supported overlay positioning modes.</summary>
public enum OverlayAnchor
{
    /// <summary>Centre horizontally at the top of the primary screen.</summary>
    TopCenter,

    /// <summary>Use <see cref="OverlaySpec.X"/> and <see cref="OverlaySpec.Y"/>.</summary>
    Custom,
}
