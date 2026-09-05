using System.Drawing;

namespace Ocrx.Engine;

internal interface IRoiOverlayRenderer : IDisposable
{
    void Show(Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes);

    void Hide();
}
