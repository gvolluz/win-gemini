using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WinGeminiWrapper;

internal sealed class MainForm : Form
{
    private const int TopRevealThresholdPixels = 8;
    private const int WindowStateSaveDebounceMs = 350;
    private const int TopBarPollMs = 120;

    private readonly Panel _webViewHost;
    private readonly ToolStrip _topBar;
    private readonly ToolStripComboBox _appSwitcher;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly Dictionary<WrappedApp, WebView2> _webViews = new();
    private readonly System.Windows.Forms.Timer _windowStateSaveTimer;
    private readonly System.Windows.Forms.Timer _topBarVisibilityTimer;
    private CoreWebView2Environment? _webViewEnvironment;
    private AppState _appState;
    private ToolStripMenuItem _openMenuItem = null!;
    private bool _exitRequested;
    private bool _balloonShown;
    private WrappedApp _currentApp;

    internal MainForm()
    {
        _appState = AppStateStore.Load();
        _currentApp = Enum.IsDefined(typeof(WrappedApp), _appState.LastSelectedApp)
            ? _appState.LastSelectedApp
            : AppConfig.DefaultApp;

        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1320, 880);
        Icon = AppIconProvider.GetIcon();
        ApplySavedWindowPlacement();

        _appSwitcher = BuildAppSwitcher();
        _topBar = BuildTopBar();
        _topBar.Visible = false;

        _webViewHost = new Panel
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_webViewHost);
        Controls.Add(_topBar);

        _trayMenu = BuildTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = AppIconProvider.GetIcon(),
            Text = AppConfig.GetAppDisplayName(_currentApp),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        _windowStateSaveTimer = new System.Windows.Forms.Timer
        {
            Interval = WindowStateSaveDebounceMs
        };
        _windowStateSaveTimer.Tick += WindowStateSaveTimer_Tick;

        _topBarVisibilityTimer = new System.Windows.Forms.Timer
        {
            Interval = TopBarPollMs
        };
        _topBarVisibilityTimer.Tick += TopBarVisibilityTimer_Tick;

        UpdateAppChrome();

        Load += MainForm_Load;
        Shown += MainForm_Shown;
        Move += MainForm_WindowPlacementChanged;
        Resize += MainForm_Resize;
        SizeChanged += MainForm_WindowPlacementChanged;
        FormClosing += MainForm_FormClosing;
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.WebViewUserDataFolder);
            Directory.CreateDirectory(AppConfig.AppDataRootFolder);

            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppConfig.WebViewUserDataFolder);

            await EnsureWebViewInitializedAsync(WrappedApp.Gemini);
            await EnsureWebViewInitializedAsync(WrappedApp.NotebookLm);
            ShowActiveWebView();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Unable to start application window.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _exitRequested = true;
            Close();
        }
    }

    private void MainForm_Shown(object? sender, EventArgs e)
    {
        CaptureWindowPlacement();
        QueueAppStateSave();
        _topBarVisibilityTimer.Start();
        UpdateTopBarVisibility();
    }

    private async Task EnsureWebViewInitializedAsync(WrappedApp app)
    {
        if (_webViews.ContainsKey(app))
        {
            return;
        }

        if (_webViewEnvironment is null)
        {
            throw new InvalidOperationException("WebView2 environment was not initialized.");
        }

        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false
        };

        _webViews[app] = webView;
        _webViewHost.Controls.Add(webView);

        await webView.EnsureCoreWebView2Async(_webViewEnvironment);
        ConfigureWebView(webView, app);
        webView.CoreWebView2.Navigate(GetStartupUrl(app));
    }

    private void ConfigureWebView(WebView2 webView, WrappedApp app)
    {
        var coreWebView2 = webView.CoreWebView2;
        coreWebView2.Settings.IsStatusBarEnabled = false;
        coreWebView2.NewWindowRequested += (_, e) => CoreWebView2_NewWindowRequested(coreWebView2, e);
        coreWebView2.SourceChanged += (_, _) => PersistLastUrl(app, webView.Source);
    }

    private void CoreWebView2_NewWindowRequested(CoreWebView2 coreWebView2, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            coreWebView2.Navigate(e.Uri);
        }
    }

    private string GetStartupUrl(WrappedApp app)
    {
        var previousUrl = _appState.GetLastUrl(app);
        if (Uri.TryCreate(previousUrl, UriKind.Absolute, out var previousUri) &&
            NavigationClassifier.IsUriForApp(previousUri, app))
        {
            return previousUri.AbsoluteUri;
        }

        return AppConfig.GetAppUrl(app);
    }

    private void PersistLastUrl(WrappedApp app, Uri? sourceUri)
    {
        if (sourceUri is null || !NavigationClassifier.IsUriForApp(sourceUri, app))
        {
            return;
        }

        var absoluteUri = sourceUri.AbsoluteUri;
        if (string.Equals(_appState.GetLastUrl(app), absoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appState.SetLastUrl(app, absoluteUri);
        QueueAppStateSave();
    }

    private ToolStripComboBox BuildAppSwitcher()
    {
        var switcher = new ToolStripComboBox
        {
            AutoSize = false,
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        switcher.Items.Add(AppConfig.GetAppDisplayName(WrappedApp.Gemini));
        switcher.Items.Add(AppConfig.GetAppDisplayName(WrappedApp.NotebookLm));
        switcher.SelectedIndex = (int)_currentApp;
        switcher.SelectedIndexChanged += AppSwitcher_SelectedIndexChanged;

        return switcher;
    }

    private ToolStrip BuildTopBar()
    {
        var topBar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System
        };

        topBar.Items.Add(new ToolStripLabel("App:"));
        topBar.Items.Add(_appSwitcher);
        topBar.Items.Add(new ToolStripSeparator());
        topBar.Items.Add(new ToolStripButton("Refresh", null, (_, _) => GetCurrentWebView()?.CoreWebView2?.Reload()));
        topBar.Items.Add(new ToolStripButton("Settings", null, (_, _) => OpenSettings()));

        return topBar;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        _openMenuItem = new ToolStripMenuItem("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add(_openMenuItem);
        menu.Items.Add("Go to Gemini", null, (_, _) => SwitchApp(WrappedApp.Gemini, true));
        menu.Items.Add("Go to NotebookLM", null, (_, _) => SwitchApp(WrappedApp.NotebookLm, true));
        menu.Items.Add("Refresh", null, (_, _) => GetCurrentWebView()?.CoreWebView2?.Reload());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        return menu;
    }

    private void AppSwitcher_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_appSwitcher.SelectedIndex < 0)
        {
            return;
        }

        var app = _appSwitcher.SelectedIndex == (int)WrappedApp.NotebookLm
            ? WrappedApp.NotebookLm
            : WrappedApp.Gemini;

        SwitchApp(app, false);
    }

    private void SwitchApp(WrappedApp app, bool restoreFromTray)
    {
        if (app == _currentApp)
        {
            if (restoreFromTray)
            {
                RestoreFromTray();
            }

            return;
        }

        _currentApp = app;

        if (_appSwitcher.SelectedIndex != (int)_currentApp)
        {
            _appSwitcher.SelectedIndex = (int)_currentApp;
        }

        UpdateAppChrome();
        _appState.LastSelectedApp = _currentApp;
        QueueAppStateSave();
        ShowActiveWebView();

        if (restoreFromTray)
        {
            RestoreFromTray();
        }
    }

    private WebView2? GetCurrentWebView()
    {
        return _webViews.TryGetValue(_currentApp, out var webView)
            ? webView
            : null;
    }

    private void ShowActiveWebView()
    {
        foreach (var entry in _webViews)
        {
            entry.Value.Visible = entry.Key == _currentApp;
        }

        var currentWebView = GetCurrentWebView();
        currentWebView?.BringToFront();
    }

    private void UpdateAppChrome()
    {
        var appName = AppConfig.GetAppDisplayName(_currentApp);
        Text = appName;
        _trayIcon.Text = appName;
        _openMenuItem.Text = $"Open {appName}";
    }

    private void MainForm_WindowPlacementChanged(object? sender, EventArgs e)
    {
        CaptureWindowPlacement();
        QueueAppStateSave();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }

        CaptureWindowPlacement();
        QueueAppStateSave();
    }

    private void CaptureWindowPlacement()
    {
        _appState.LastWindowState = WindowState switch
        {
            FormWindowState.Maximized => SavedWindowState.Maximized,
            FormWindowState.Minimized => SavedWindowState.Minimized,
            _ => SavedWindowState.Normal
        };

        var normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (normalBounds.Width <= 0 || normalBounds.Height <= 0)
        {
            return;
        }

        _appState.SetWindowBounds(normalBounds);
    }

    private void ApplySavedWindowPlacement()
    {
        if (_appState.TryGetWindowBounds(out var savedBounds) && IsUsableBounds(savedBounds))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = savedBounds;
        }

        WindowState = _appState.LastWindowState switch
        {
            SavedWindowState.Maximized => FormWindowState.Maximized,
            SavedWindowState.Minimized => FormWindowState.Minimized,
            _ => FormWindowState.Normal
        };
    }

    private static bool IsUsableBounds(Rectangle bounds)
    {
        if (bounds.Width < 400 || bounds.Height < 300)
        {
            return false;
        }

        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(bounds))
            {
                return true;
            }
        }

        return false;
    }

    private void QueueAppStateSave()
    {
        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    private void WindowStateSaveTimer_Tick(object? sender, EventArgs e)
    {
        _windowStateSaveTimer.Stop();
        AppStateStore.Save(_appState);
    }

    private void SaveAppStateNow()
    {
        _windowStateSaveTimer.Stop();
        AppStateStore.Save(_appState);
    }

    private void TopBarVisibilityTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTopBarVisibility();
    }

    private void UpdateTopBarVisibility()
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            SetTopBarVisible(false);
            return;
        }

        var cursorInClient = PointToClient(Cursor.Position);
        var isInsideWindow = ClientRectangle.Contains(cursorInClient);
        var nearTopEdge = isInsideWindow && cursorInClient.Y >= 0 && cursorInClient.Y <= TopRevealThresholdPixels;
        var overTopBar = _topBar.Visible && _topBar.Bounds.Contains(cursorInClient);
        var switcherDropdownOpen = _appSwitcher.ComboBox?.DroppedDown == true;

        SetTopBarVisible(nearTopEdge || overTopBar || switcherDropdownOpen);
    }

    private void SetTopBarVisible(bool visible)
    {
        if (_topBar.Visible == visible)
        {
            return;
        }

        _topBar.Visible = visible;
        if (visible)
        {
            _topBar.BringToFront();
        }
    }

    private void OpenSettings()
    {
        using var settingsForm = new SettingsForm(_appState.CloseButtonBehavior);
        if (settingsForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (settingsForm.SelectedCloseButtonBehavior == _appState.CloseButtonBehavior)
        {
            return;
        }

        _appState.CloseButtonBehavior = settingsForm.SelectedCloseButtonBehavior;
        SaveAppStateNow();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested)
        {
            _trayIcon.Visible = false;
            return;
        }

        if (e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        if (_appState.CloseButtonBehavior == CloseButtonBehavior.CloseApp)
        {
            _exitRequested = true;
            CaptureWindowPlacement();
            SaveAppStateNow();
            _trayIcon.Visible = false;
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();

        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;
        var appName = AppConfig.GetAppDisplayName(_currentApp);
        _trayIcon.ShowBalloonTip(
            2000,
            $"{appName} is still running",
            "Use the tray icon to reopen or exit.",
            ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        UpdateTopBarVisibility();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        CaptureWindowPlacement();
        SaveAppStateNow();
        _trayIcon.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _topBarVisibilityTimer.Dispose();
            _windowStateSaveTimer.Dispose();
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            foreach (var webView in _webViews.Values)
            {
                webView.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
