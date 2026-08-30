using System.Drawing;
using System.Windows.Forms;
using GameCapture.Engine.Plugins;

namespace GameCapture.Engine.Tray;

/// <summary>
/// The plugin manager reached from the tray menu: the published catalog, what is installed, and the
/// install / update / remove / launch / stop actions over it. Replaces the manual round trip of
/// finding the plugins repository, picking the right release asset and unzipping it by hand.
/// </summary>
/// <remarks>
/// UI edge, excluded from the coverage gate: it cannot instantiate without a desktop. What each row
/// says and which buttons it offers is decided by <see cref="PluginRowBuilder"/> and
/// <see cref="PluginRow"/>, which are pure and tested — this form paints those answers and sequences
/// the installer. It is also the one place in the tray that awaits: the catalog fetch and the
/// download are network work, and blocking the STA thread for them would freeze the tray icon along
/// with the dialog. The handlers are <c>async void</c> because that is what a WinForms event
/// signature allows; each one owns a try/catch so a failed await surfaces in the status line rather
/// than as an unhandled exception on the message loop.
/// </remarks>
public sealed class PluginsForm : Form
{
    private readonly PluginServices _services;
    private readonly ListView _list;
    private readonly Button _install;
    private readonly Button _remove;
    private readonly Button _launch;
    private readonly Button _stop;
    private readonly Button _close;
    private readonly CheckBox _includePreviews;
    private readonly ProgressBar _progress;
    private readonly Label _status;

    private readonly Dictionary<string, string> _latestVersions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _work = new();

    private IReadOnlyList<CatalogEntry> _catalog = [];
    private IReadOnlyList<PluginRow> _rows = [];
    private bool _busy;
    private bool _stableSourceConfirmed;
    private bool _previewSourceConfirmed;
    private bool _changingPreviewSetting;
    private bool _closePending;

    public PluginsForm(PluginServices services)
    {
        _services = services;

        Text = "GameCapture plugins";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Width = 660,
            Height = 200,
        };
        _list.Columns.Add("Plugin", 150);
        _list.Columns.Add("Status", 190);
        _list.Columns.Add("Description", 300);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        _install = MakeButton("Install", OnInstall);
        _remove = MakeButton("Remove", OnRemove);
        _launch = MakeButton("Launch", OnLaunch);
        _stop = MakeButton("Stop", OnStop);
        _close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(3) };
        _includePreviews = new CheckBox
        {
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 3),
            Text = "Include preview plugins (may be unstable)",
            Checked = _services.Settings.IncludePreviews,
        };
        _includePreviews.CheckedChanged += async (_, _) => await ChangePreviewSettingAsync();

        _progress = new ProgressBar { Width = 220, Height = 16, Visible = false, Margin = new Padding(3, 6, 3, 3) };
        _status = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Loading the plugin catalog…" };

        // The source and the signing state are the two things a user cannot check for themselves once
        // the download is a button, so they are stated on the dialog rather than only in the docs.
        var notice = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Margin = new Padding(3, 3, 3, 8),
            Text = "Plugins are downloaded from github.com/PetitCastor/gamecapture-plugins, the only "
                   + "source GameCapture accepts. They are not code-signed.",
        };

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        actions.Controls.Add(_install);
        actions.Controls.Add(_launch);
        actions.Controls.Add(_stop);
        actions.Controls.Add(_remove);

        var closeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
        closeRow.Controls.Add(_close);

        var statusRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        statusRow.Controls.Add(_progress);
        statusRow.Controls.Add(_status);

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(notice, 0, 0);
        root.Controls.Add(_includePreviews, 0, 1);
        root.Controls.Add(_list, 0, 2);
        root.Controls.Add(actions, 0, 3);
        root.Controls.Add(statusRow, 0, 4);
        root.Controls.Add(closeRow, 0, 5);
        Controls.Add(root);

        AcceptButton = _close;
        CancelButton = _close;

        Load += async (_, _) => await LoadCatalogAsync();
    }

    /// <summary>
    /// Holds the dialog open until in-flight work unwinds. The title-bar close and Alt+F4 cannot be
    /// disabled the way the Close button can, and letting one through mid-download would dispose the
    /// form under an awaiting handler — whose continuation would then touch disposed controls on the
    /// tray's message loop. Cancelling and closing on the way out is the same thing from the user's
    /// side, minus that exception.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            _closePending = true;
            _work.Cancel();
            e.Cancel = true;
            Report("Finishing up…", error: false);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _work.Dispose();

        base.Dispose(disposing);
    }

    private Button MakeButton(string text, Func<PluginRow, Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Enabled = false, Margin = new Padding(3) };
        button.Click += async (_, _) =>
        {
            if (_busy || SelectedRow() is not { } row)
                return;

            try
            {
                SetBusy(true);
                await action(row);
            }
            catch (OperationCanceledException)
            {
                Report("Cancelled.", error: false);
            }
            catch (Exception ex)
            {
                Report(ex.Message, error: true);
            }
            finally
            {
                Finish();
            }
        };
        return button;
    }

    // Single exit path for every awaiting handler: drop the busy state, repaint, and honour a close
    // the user asked for while the work was still running.
    private void Finish()
    {
        if (IsDisposed || Disposing)
            return;

        SetBusy(false);
        Rebuild();

        if (_closePending)
            Close();
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            SetBusy(true);
            _latestVersions.Clear();
            var stable = await _services.Installer.FetchCatalogAsync(_work.Token);
            var catalog = stable.ToList();
            var previewError = "";
            if (_services.Settings.IncludePreviews)
            {
                try
                {
                    var previews = await _services.Installer.FetchPreviewCatalogAsync(_work.Token);
                    catalog = PluginCatalogMerge.Combine(stable, previews, out var droppedIds).ToList();
                    if (droppedIds.Count > 0)
                        previewError = $"Preview catalog id(s) already used by a stable plugin were skipped: {string.Join(", ", droppedIds)}.";
                }
                catch (HttpRequestException ex)
                {
                    previewError = $"Preview catalog unavailable: {ex.Message}";
                }
                catch (OperationCanceledException ex) when (!_work.IsCancellationRequested)
                {
                    previewError = $"Preview catalog unavailable: {ex.Message}";
                }
            }

            var catalogIds = catalog.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var installed in _services.Installer.State.Entries.Values)
            {
                if (catalogIds.Contains(installed.Id))
                    continue;

                // Keep any catalog-orphaned install manageable. Legacy state predates DownloadUrl;
                // reconstruct its immutable release path from the stored tag rather than hiding it.
                var url = string.IsNullOrWhiteSpace(installed.DownloadUrl)
                    ? $"https://github.com/PetitCastor/gamecapture-plugins/releases/download/{Uri.EscapeDataString(installed.Version)}/{Uri.EscapeDataString(installed.Name)}-win-x64.zip"
                    : installed.DownloadUrl;
                var description = installed.Channel == ReleaseChannel.Preview
                    ? "Preview release; updates are paused."
                    : "Installed plugin no longer appears in the stable catalog.";
                catalog.Add(new CatalogEntry(installed.Id, installed.Name, description, url, installed.Channel));
                catalogIds.Add(installed.Id);
            }

            _catalog = catalog;
            Rebuild();

            // Version probes are one HEAD each and only decide whether a row says "Update available",
            // so a failure here degrades the list rather than emptying it.
            Report("Checking for updates…", error: false);
            foreach (var entry in _catalog)
            {
                _work.Token.ThrowIfCancellationRequested();
                if (entry.Channel == ReleaseChannel.Preview && !_services.Settings.IncludePreviews)
                    continue;
                try
                {
                    var version = await _services.Installer.ResolveLatestVersionAsync(entry, _work.Token);
                    if (version.Length > 0)
                        _latestVersions[entry.Id] = version;
                }
                catch (HttpRequestException)
                {
                    // Leave this row without update information.
                }
            }

            Report(
                previewError.Length > 0
                    ? $"{_catalog.Count} plugin(s) available. {previewError}"
                    : $"{_catalog.Count} plugin(s) in the catalog.",
                error: previewError.Length > 0);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.", error: false);
        }
        catch (Exception ex)
        {
            Report($"Could not load the catalog: {ex.Message}", error: true);
        }
        finally
        {
            Finish();
        }
    }

    private async Task OnInstall(PluginRow row)
    {
        if (!ConfirmSource(row.Entry.Channel))
            return;

        _progress.Visible = true;
        _progress.Value = 0;
        Report($"Downloading {row.Name}…", error: false);

        var progress = new Progress<int>(percent => _progress.Value = Math.Clamp(percent, 0, 100));
        var installed = await _services.Installer.InstallAsync(row.Entry, progress, _work.Token);

        _progress.Visible = false;
        Report($"{installed.Name} {installed.Version} installed. Use Launch to start it.", error: false);
    }

    private Task OnRemove(PluginRow row)
    {
        var confirm = MessageBox.Show(
            this,
            $"Remove {row.Name} and delete its files?",
            "GameCapture",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
            return Task.CompletedTask;

        _services.Launcher.Stop(row.Id);
        _services.Installer.Uninstall(row.Id);
        Report($"{row.Name} removed.", error: false);
        return Task.CompletedTask;
    }

    private Task OnLaunch(PluginRow row)
    {
        if (!_services.Installer.State.TryGet(row.Id, out var installed))
            return Task.CompletedTask;

        _services.Launcher.Start(installed);
        Report($"{row.Name} started. It appears in the tray status once it connects.", error: false);
        return Task.CompletedTask;
    }

    private Task OnStop(PluginRow row)
    {
        _services.Launcher.Stop(row.Id);
        Report($"{row.Name} stopped.", error: false);
        return Task.CompletedTask;
    }

    // Named once, before the first download of the session, so "install" is never a one-click action
    // whose source the user was never shown.
    private bool ConfirmSource(ReleaseChannel channel)
    {
        if (channel == ReleaseChannel.Stable && _stableSourceConfirmed)
            return true;
        if (channel == ReleaseChannel.Preview && _previewSourceConfirmed)
            return true;

        var previewWarning = channel == ReleaseChannel.Preview
            ? "\n\nThis is a preview release. It may be incomplete or change incompatibly."
            : "";
        var confirm = MessageBox.Show(
            this,
            "Plugins are downloaded from github.com/PetitCastor/gamecapture-plugins and are not "
            + "code-signed. They run as separate processes with your user's permissions."
            + previewWarning
            + "\n\nContinue?",
            "GameCapture",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (channel == ReleaseChannel.Preview)
            _previewSourceConfirmed = confirm == DialogResult.OK;
        else
            _stableSourceConfirmed = confirm == DialogResult.OK;

        return confirm == DialogResult.OK;
    }

    private PluginRow? SelectedRow()
        => _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is PluginRow row ? row : null;

    private void Rebuild()
    {
        var selectedId = SelectedRow()?.Id;
        _rows = PluginRowBuilder.Build(
            _catalog,
            _services.Installer.State.Entries,
            _services.Launcher.RunningIds,
            _latestVersions,
            PausedPreviewIds());

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            var item = new ListViewItem([row.Name, row.StateText, row.Entry.Description]) { Tag = row };
            if (row.State == PluginRowState.Blocked)
                item.ForeColor = Color.Firebrick;
            _list.Items.Add(item);
            if (row.Id == selectedId)
                item.Selected = true;
        }
        _list.EndUpdate();

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var row = SelectedRow();
        _install.Text = row?.InstallActionText ?? "Install";
        _install.Enabled = !_busy && row is not null && (row.CanInstall || row.CanReinstall);
        _remove.Enabled = !_busy && row is { CanRemove: true };
        _launch.Enabled = !_busy && row is { CanLaunch: true };
        _stop.Enabled = !_busy && row is { CanStop: true };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _list.Enabled = !busy;
        _includePreviews.Enabled = !busy;
        _close.Enabled = !busy;
        UseWaitCursor = busy;
        if (!busy)
            _progress.Visible = false;
        UpdateButtons();
    }

    private void Report(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
    }

    private IReadOnlyCollection<string> PausedPreviewIds()
        => !_services.Settings.IncludePreviews
            ? _services.Installer.State.Entries.Values
                .Where(installed => installed.Channel == ReleaseChannel.Preview)
                .Select(installed => installed.Id)
                .ToHashSet(StringComparer.Ordinal)
            : [];

    private async Task ChangePreviewSettingAsync()
    {
        if (!IsHandleCreated || _busy || _changingPreviewSetting)
            return;

        try
        {
            _services.Settings.IncludePreviews = _includePreviews.Checked;
            _services.Settings.Save();
            await LoadCatalogAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Settings.IncludePreviews = !_includePreviews.Checked;
            _changingPreviewSetting = true;
            try
            {
                _includePreviews.Checked = _services.Settings.IncludePreviews;
            }
            finally
            {
                _changingPreviewSetting = false;
            }
            Report($"Could not save preview preference: {ex.Message}", error: true);
        }
    }
}
