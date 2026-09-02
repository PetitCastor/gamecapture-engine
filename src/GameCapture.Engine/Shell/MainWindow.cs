using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using GameCapture.Engine.Tray;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace GameCapture.Engine.Shell;

/// <summary>
/// The engine's primary interactive surface (TASK-UI-04): a taskbar window whose entire client area is
/// a <see cref="WebView2"/> pointed at the loopback control API's static UI. Constructed eagerly on the
/// tray's STA thread by <see cref="Tray.TrayApplication"/> and passed to <c>Application.Run</c>, so it
/// always has a window handle — unlike the tray-only <c>StatusForm</c> it replaces as the main form,
/// nothing here needs the handle-forcing workaround that existed only to give shutdown's
/// <c>BeginInvoke</c> a valid target.
/// </summary>
/// <remarks>
/// UI/interop edge, excluded from the coverage gate alongside <c>Tray/TrayApplication.cs</c>: it needs
/// a real WebView2 runtime and a desktop to run at all. <see cref="WindowChrome"/> (the DWM P/Invoke
/// wrapper) and <see cref="SingleInstance"/> (the mutex/event pair) are the parts of this redesign that
/// factor out cleanly enough to unit test, and do.
/// </remarks>
internal sealed class MainWindow : Form
{
    // Microsoft's Evergreen Bootstrapper — the one link to hand a user whose Windows build has no
    // WebView2 runtime installed. Never changes per release, so it is safe to hardcode rather than
    // resolve dynamically.
    private const string EvergreenInstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly WebView2 _webView;
    private readonly int _controlApiPort;
    private readonly string _controlApiToken;
    // Not readonly: ApplyThemeSetting (TASK-UI-05 section 6) mutates this in place when the persisted
    // theme changes live, so IsEffectivelyDark and a later System.UserPreferenceChanged both see the
    // current choice rather than the one this window was constructed with.
    private EngineTheme _theme;
    private readonly bool _closeToTrayEnabled;
    private readonly Action _onExitRequested;
    private readonly Action _onFirstHideToTray;

    private readonly OneTimeNotice _closeToTrayNotice;

    // Written from whichever thread the tray's Exit handler or the control API's request thread run
    // on, read from the UI thread inside OnFormClosing. Both sides only ever move it true -> true or
    // false -> true, so a plain volatile bool needs no lock: there is no ordering to preserve between
    // two writers that agree on the value, only prompt visibility to the UI-thread reader.
    private volatile bool _exiting;

    public MainWindow(
        int controlApiPort,
        string controlApiToken,
        EngineTheme theme,
        bool closeToTrayEnabled,
        bool closeToTrayNoticeAlreadyShown,
        Action onExitRequested,
        Action onFirstHideToTray)
    {
        _controlApiPort = controlApiPort;
        _controlApiToken = controlApiToken;
        _theme = theme;
        _closeToTrayEnabled = closeToTrayEnabled;
        _closeToTrayNotice = new OneTimeNotice(closeToTrayNoticeAlreadyShown);
        _onExitRequested = onExitRequested;
        _onFirstHideToTray = onFirstHideToTray;

        Text = "GameCapture";
        ShowInTaskbar = true;
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadApplicationIcon();

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        HandleCreated += (_, _) => WindowChrome.ApplyTheme(Handle, IsEffectivelyDark());
        Load += async (_, _) => await InitializeWebViewAsync();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// Re-applies the native caption bar after a live theme change (TASK-UI-05 section 6):
    /// <c>POST /api/settings</c> persists a theme-only change without restarting, and the WebView2 page
    /// picks up its own CSS from that response — but the DWM chrome around it is owned here, on a
    /// different thread, and the page has no way to touch it itself. Safe to call from any thread
    /// (settings changes arrive from a Kestrel request thread, never the UI thread); a no-op once the
    /// window or its handle is gone.
    /// </summary>
    public void ApplyThemeSetting(EngineTheme theme)
    {
        if (IsDisposed)
            return;

        try
        {
            BeginInvoke((Action)(() =>
            {
                _theme = theme;
                if (IsHandleCreated)
                    WindowChrome.ApplyTheme(Handle, IsEffectivelyDark());
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The UI thread's handle is already gone (shutdown race); nothing left to theme.
        }
    }

    /// <summary>
    /// Opens a native <see cref="FolderBrowserDialog"/> on the UI thread on behalf of
    /// <c>POST /api/settings/browse</c> (TASK-UI-05 section 5) — the web page cannot show one itself.
    /// Safe to call from any thread; resolves to <c>null</c> (treated as "cancelled" by the caller) if
    /// the dialog was dismissed, or if the window/handle is already gone.
    /// </summary>
    public Task<string?> BrowseForFolderAsync(string? initialDirectory)
    {
        if (IsDisposed)
            return Task.FromResult<string?>(null);

        var completion = new TaskCompletionSource<string?>();
        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "Where frame dumps land",
                        ShowNewFolderButton = true,
                    };
                    if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                        dialog.SelectedPath = initialDirectory;

                    completion.TrySetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    /// <summary>Marks a real exit as already underway (tray Exit, or <c>POST /api/exit</c>) so a
    /// subsequent <see cref="OnFormClosing"/> — e.g. a race with the user also clicking the window's
    /// close button — does not cancel back into a hidden window. Safe to call from any thread.</summary>
    public void PrepareForExit() => _exiting = true;

    /// <summary>Un-minimizes, shows and activates the window. Used by the tray's "Show GameCapture"
    /// item, double-clicking the tray icon, and a second-launch handoff via <see cref="SingleInstance.Signaled"/>.</summary>
    public void ShowAndActivate()
    {
        if (IsDisposed)
            return;

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (CloseToTrayDecision.ShouldHideInsteadOfClose(_exiting, e.CloseReason, _closeToTrayEnabled))
        {
            e.Cancel = true;
            Hide();
            _closeToTrayNotice.TryFire(_onFirstHideToTray);
            return;
        }

        if (CloseToTrayDecision.ShouldRequestExit(_exiting, e.CloseReason, _closeToTrayEnabled))
        {
            // Every accepted close must stop capture, including Windows shutdown and Task Manager.
            // Closing the form only ends this dedicated UI thread; Program independently awaits the
            // engine cancellation token and otherwise keeps the process alive.
            _exiting = true;
            _onExitRequested();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        base.Dispose(disposing);
    }

    private bool IsEffectivelyDark() => _theme switch
    {
        EngineTheme.Dark => true,
        EngineTheme.Light => false,
        _ => WindowChrome.IsSystemDarkModeEnabled(),
    };

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Only System follows the OS live; an explicit Light/Dark choice never needs to react to this
        // event, and re-reading the registry for every unrelated preference change (colors, mouse,
        // window metrics, ...) that also raises General would be pointless work.
        if (_theme != EngineTheme.System || e.Category != UserPreferenceCategory.General)
            return;

        if (IsHandleCreated)
            WindowChrome.ApplyTheme(Handle, WindowChrome.IsSystemDarkModeEnabled());
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameCapture",
                "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = Debugger.IsAttached;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;

            // Injected into the page's own globals before navigation — never in the URL, a query
            // string, or any log line (see ControlApiToken's contract).
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__GC_TOKEN={JsonSerializer.Serialize(_controlApiToken)};window.__GC_PORT={_controlApiPort};");

            _webView.CoreWebView2.Navigate($"http://127.0.0.1:{_controlApiPort}/");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The runtime genuinely absent on an old Windows 10 build is the expected case here, but
            // whatever the cause, this must not take the capture engine down — same defensive posture
            // as TrayApplication's own top-level catch.
            ShowInitializationFailure(ex);
        }
    }

    private void ShowInitializationFailure(Exception ex)
    {
        Controls.Remove(_webView);
        _webView.Dispose();

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };

        var message = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 32, 0),
            Padding = new Padding(16),
            Text = $"GameCapture could not start its web view ({ex.Message}).\r\n\r\n"
                + "Capture keeps running in the background and the tray icon still works. Install the "
                + "WebView2 Runtime and relaunch to restore this window.",
        };

        var link = new LinkLabel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16, 0, 16, 16),
            Text = EvergreenInstallerUrl,
        };
        link.LinkClicked += (_, _) => OpenEvergreenInstaller();

        panel.Controls.Add(message);
        panel.Controls.Add(link);
        Controls.Add(panel);
    }

    private static void OpenEvergreenInstaller()
    {
        try
        {
            Process.Start(new ProcessStartInfo(EvergreenInstallerUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser association on this machine; nothing more this can do.
        }
    }

    private static Icon LoadApplicationIcon()
    {
        // Extracted from the running exe's own resources rather than read from assets/app.ico on
        // disk: GameCapture.Engine.csproj already embeds that file as ApplicationIcon (for Explorer
        // and the taskbar), so this is the same icon by construction and needs no separate content
        // item or install-time packaging of its own.
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            return SystemIcons.Application;
        }
    }
}
