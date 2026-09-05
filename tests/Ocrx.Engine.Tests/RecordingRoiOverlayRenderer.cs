using System.Drawing;

namespace Ocrx.Engine.Tests;

internal sealed class RecordingRoiOverlayRenderer : IRoiOverlayRenderer
{
    public int ShowCount { get; private set; }
    public int HideCount { get; private set; }
    public IReadOnlyList<RoiOverlayShape> Shapes { get; private set; } = [];

    public void Show(Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes)
    {
        ShowCount++;
        Shapes = shapes.ToArray();
    }

    public void Hide() => HideCount++;

    public void Dispose()
    {
    }
}
