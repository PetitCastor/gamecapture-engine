using System.Drawing;
using System.Windows.Forms;

namespace GameCapture.Engine.Tray;

/// <summary>
/// The modal settings screen reached from the tray menu: output directory, OCR language, scan
/// interval, and hotkey — the <see cref="EngineSettings"/> fields exposed for editing. The remaining
/// fields (pipe name, metrics, tray) pass through unedited. Seeded from an <see cref="EngineSettings"/>
/// and, on OK, exposes the edited one through <see cref="Result"/>. Applying a change is the host's job
/// (persist + restart); this form only collects it — every field here is bound at engine startup and
/// cannot change in place.
/// </summary>
/// <remarks>
/// UI edge, excluded from the coverage gate: it cannot instantiate without a desktop. It holds no
/// logic worth pinning — the fields map one-to-one onto <see cref="EngineSettings"/>.
/// </remarks>
public sealed class SettingsForm : Form
{
    // Sentinel row for OcrLanguage == "": empty means "first installed pack", shown as a readable label.
    private const string AutoLanguageLabel = "(auto — first installed)";

    private readonly TextBox _outputDir;
    private readonly ComboBox _ocrLanguage;
    private readonly NumericUpDown _scanInterval;
    private readonly TextBox _hotkey;

    /// <summary>The edited settings; only meaningful after the dialog closes with <see cref="DialogResult.OK"/>.</summary>
    public EngineSettings Result { get; private set; }

    public SettingsForm(EngineSettings current, IReadOnlyList<string> availableOcrLanguages)
    {
        Result = current;

        Text = "GameCapture settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _outputDir = new TextBox { Width = 320, Text = current.OutputDir, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseForOutputDir();

        var outputRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        outputRow.Controls.Add(_outputDir);
        outputRow.Controls.Add(browse);

        _ocrLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _ocrLanguage.Items.Add(AutoLanguageLabel);
        foreach (var tag in availableOcrLanguages)
            _ocrLanguage.Items.Add(tag);
        // A tag persisted earlier whose pack is no longer installed must still round-trip, not silently
        // reset to auto — keep it selectable so OK preserves the operator's choice. Match case-insensitively
        // (as the save-time validation does), so a tag differing only in case is not added as a duplicate.
        var alreadyListed = _ocrLanguage.Items.Cast<string>()
            .Any(t => string.Equals(t, current.OcrLanguage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(current.OcrLanguage) && !alreadyListed)
            _ocrLanguage.Items.Add(current.OcrLanguage);
        _ocrLanguage.SelectedItem = string.IsNullOrEmpty(current.OcrLanguage) ? AutoLanguageLabel : current.OcrLanguage;

        _scanInterval = new NumericUpDown
        {
            Minimum = 100,   // matches the engine's clamp floor
            Maximum = 60_000,
            Increment = 50,
            Value = Math.Clamp(current.ScanIntervalMs, 100, 60_000),
            Width = 100,
        };

        _hotkey = new TextBox { Width = 200, Text = current.Hotkey };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        grid.Controls.Add(new Label { Text = "Output directory", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, 0);
        grid.Controls.Add(outputRow, 1, 0);
        grid.Controls.Add(new Label { Text = "OCR language", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, 1);
        grid.Controls.Add(_ocrLanguage, 1, 1);
        grid.Controls.Add(new Label { Text = "Scan interval (ms)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, 2);
        grid.Controls.Add(_scanInterval, 1, 2);
        grid.Controls.Add(new Label { Text = "Hotkey", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, 3);
        grid.Controls.Add(_hotkey, 1, 3);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BrowseForOutputDir()
    {
        using var dialog = new FolderBrowserDialog { Description = "Where frame dumps land", ShowNewFolderButton = true };
        if (Directory.Exists(_outputDir.Text))
            dialog.SelectedPath = _outputDir.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputDir.Text = dialog.SelectedPath;
    }

    private void Commit()
    {
        var language = _ocrLanguage.SelectedItem is string s && s != AutoLanguageLabel ? s : "";
        // A blank directory would resolve to the engine's own install folder on the next load, silently
        // redirecting dumps there — keep the existing value rather than persist an empty string.
        var outputDir = _outputDir.Text.Trim();
        if (outputDir.Length == 0)
            outputDir = Result.OutputDir;
        var hotkey = _hotkey.Text.Trim();
        if (hotkey.Length == 0)
            hotkey = Result.Hotkey;
        Result = new EngineSettings(
            outputDir,
            language,
            (int)_scanInterval.Value,
            hotkey,
            Result.PipeName,
            Result.MetricsEnabled,
            Result.MetricsIntervalMs,
            Result.TrayEnabled);
    }
}
