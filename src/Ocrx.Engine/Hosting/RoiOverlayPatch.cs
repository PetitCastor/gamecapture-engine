namespace Ocrx.Engine;

/// <summary>Desired, idempotent visibility state for one plugin's ROI diagnostic overlay.</summary>
internal sealed record RoiOverlayPatch(bool? Visible);
