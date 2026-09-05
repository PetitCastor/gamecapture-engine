namespace Ocrx.Engine;

/// <summary>Render-ready availability and visibility state for one plugin-manager row.</summary>
internal readonly record struct RoiOverlayState(bool CanShow, bool IsVisible);
