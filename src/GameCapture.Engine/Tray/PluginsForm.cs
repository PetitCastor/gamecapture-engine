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
    private readonly ProgressBar _progress;
    private readonly Label _status;

    private IReadOnlyList<CatalogEntry> _catalog = [];
    private readonly Dictionary<string, string> _latestVersions = new(StringComparer.Ordinal);
    private IReadOnlyList<PluginRow> _rows = [];
    private bool _busy;
    private bool _sourceConfirmed;

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
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(actions, 0, 2);
        root.Controls.Add(statusRow, 0, 3);
        root.Controls.Add(closeRow, 0, 4);
        Controls.Add(root);

        AcceptButton = _close;
        CancelButton = _close;

        Load += async (_, _) => await LoadCatalogAsync();
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
            catch (Exception ex)
            {
                Report(ex.Message, error: true);
            }
            finally
            {
                SetBusy(false);
                Rebuild();
            }
        };
        return button;
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            SetBusy(true);
            _catalog = await _services.Installer.FetchCatalogAsync(CancellationToken.None);
            Rebuild();

            // Version probes are one HEAD each and only decide whether a row says "Update available",
            // so a failure here degrades the list rather than emptying it.
            Report("Checking for updates…", error: false);
            foreach (var entry in _catalog)
            {
                try
                {
                    var version = await _services.Installer.ResolveLatestVersionAsync(entry, CancellationToken.None);
                    if (version.Length > 0)
                        _latestVersions[entry.Id] = version;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Leave this row without update information.
                }
            }

            Report($"{_catalog.Count} plugin(s) in the catalog.", error: false);
        }
        catch (Exception ex)
        {
            Report($"Could not load the catalog: {ex.Message}", error: true);
        }
        finally
        {
            SetBusy(false);
            Rebuild();
        }
    }

    private async Task OnInstall(PluginRow row)
    {
        if (!ConfirmSource())
            return;

        _progress.Visible = true;
        _progress.Value = 0;
        Report($"Downloading {row.Name}…", error: false);

        var progress = new Progress<int>(percent => _progress.Value = Math.Clamp(percent, 0, 100));
        var installed = await _services.Installer.InstallAsync(row.Entry, progress, CancellationToken.None);

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
    private bool ConfirmSource()
    {
        if (_sourceConfirmed)
            return true;

        var confirm = MessageBox.Show(
            this,
            "Plugins are downloaded from github.com/PetitCastor/gamecapture-plugins and are not "
            + "code-signed. They run as separate processes with your user's permissions.\n\nContinue?",
            "GameCapture",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        _sourceConfirmed = confirm == DialogResult.OK;
        return _sourceConfirmed;
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
            _latestVersions);

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
}
