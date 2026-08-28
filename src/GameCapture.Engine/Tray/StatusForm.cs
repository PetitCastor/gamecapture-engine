using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace GameCapture.Engine.Tray;

/// <summary>
/// An invisible carrier for engine mode, frame size, OCR language, scanned FPS, process metrics, and
/// the connected-plugin list. Never shown; <see cref="TrayApplication"/> keeps it alive only for its
/// window handle, which <see cref="TrayApplication.Dispose"/> needs as a valid <c>BeginInvoke</c>
/// target to marshal shutdown onto the tray's UI thread.
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

    /// <summary>Replaces the tracked contents. Safe to call from the UI thread only.</summary>
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
}
