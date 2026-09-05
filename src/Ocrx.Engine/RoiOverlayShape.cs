using System.Drawing;

namespace Ocrx.Engine;

internal sealed record RoiOverlayShape(Rectangle Bounds, string Label, bool IsInvalid);
