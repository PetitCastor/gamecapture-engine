using System.Drawing;
using System.Windows.Forms;

namespace GameCapture.Engine;

/// <summary>Transparent, click-through WinForms surface used only by <see cref="RoiOverlayRenderer"/>.</summary>
internal sealed class RoiOverlayForm : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x8000000;
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;
    private IReadOnlyList<RoiOverlayShape> _shapes = [];

    public RoiOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void Apply(Rectangle monitorBounds, IReadOnlyList<RoiOverlayShape> shapes)
    {
        Bounds = monitorBounds;
        _shapes = shapes;
        Show();
        Invalidate();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = new IntPtr(HtTransparent);
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach (var shape in _shapes)
        {
            var colour = shape.IsInvalid ? Color.OrangeRed : Color.DeepSkyBlue;
            using var pen = new Pen(colour, 2);
            e.Graphics.DrawRectangle(pen, shape.Bounds);
            TextRenderer.DrawText(e.Graphics, shape.Label, Font, new Point(shape.Bounds.Left + 2, shape.Bounds.Top + 2), colour,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
