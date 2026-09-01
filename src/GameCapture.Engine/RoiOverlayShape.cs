using System.Drawing;

namespace GameCapture.Engine;

internal sealed record RoiOverlayShape(Rectangle Bounds, string Label, bool IsInvalid);
