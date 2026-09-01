using System.Drawing;

namespace GameCapture.Engine;

internal interface IRoiOverlayRenderer : IDisposable
{
    void Show(Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes);

    void Hide();
}
