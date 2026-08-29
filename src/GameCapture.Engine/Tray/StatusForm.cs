using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace GameCapture.Engine.Tray;

/// <summary>
/// The small read-only popup behind the debug-only "Status…" menu item: engine mode, frame size, OCR
/// language, scanned FPS, process metrics, and the connected-plugin list. A tool window that hides
/// itself as soon as it loses focus, so it behaves like a menu rather than a window to manage.
/// </summary>
/// <remarks>
/// UI edge, excluded from the coverage gate. All formatting is done in <see cref="TrayViewBuilder"/>;
/// this only lays the strings out.
/// </remarks>
public sealed class StatusForm : Form
{
    private readonly Label _body;

    public StatusForm()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "GameCapture engine";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(300, 0);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _body = new Label
        {
            AutoSize = true,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Padding = new Padding(12),
        };
        Controls.Add(_body);
    }

    /// <summary>Replaces the popup contents. Safe to call from the UI thread only.</summary>
    public void Update(TrayView view)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mode      {view.Mode}   v{view.EngineVersion}");
        sb.AppendLine($"Frame     {view.Frame}");
        sb.AppendLine($"OCR       {view.OcrLanguage}");
        sb.AppendLine($"Scan FPS  {view.Fps}");
        sb.AppendLine();
        sb.AppendLine(view.Metrics);
        sb.AppendLine();
        sb.Append(view.Plugins.Count == 0
            ? "Plugins   none connected"
            : $"Plugins   {string.Join(Environment.NewLine + "          ", view.Plugins)}");
        _body.Text = sb.ToString();
    }

    /// <summary>Shows the popup anchored above-left of the cursor, clamped onto the working area.</summary>
    public void ShowNear(Point anchor)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Min(anchor.X, area.Right - Width);
        var y = Math.Min(anchor.Y - Height, area.Bottom - Height);
        Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    // Closing the popup (Alt+F4, its own chrome) must not tear down the tray; hide instead, and let
    // TrayApplication dispose it for real on shutdown.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }
}
