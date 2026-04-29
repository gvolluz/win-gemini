using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Security.Cryptography;

namespace WinGeminiWrapper;

internal sealed partial class MainForm : Form
{
    private const int TopRevealThresholdPixels = 8;
    private const int WindowStateSaveDebounceMs = 350;
    private const int TopBarPollMs = 120;
    private const int GoogleDriveSyncDebounceMs = 1200;
    private const int PollingLockSyncIntervalMs = 6000;
    private const int PollingStateWriteHeartbeatSeconds = 30;
    private const int EvernoteAutoExportCooldownSeconds = 30;
    private const int TraySyncAnimationIntervalMs = 90;
    private static readonly TimeSpan PollingLockOwnerStaleThreshold = TimeSpan.FromMinutes(2);

    private readonly Panel _webViewHost;
    private readonly Panel _evernoteExportPanel;
    private readonly ToolStrip _topBar;
    private readonly ToolStripComboBox _appSwitcher;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _evernoteTreeNodeMenu;
    private readonly TextBox _evernoteDbPathTextBox;
    private readonly TreeView _evernoteTreeView;
    private readonly Label _evernoteStatusLabel;
    private readonly CheckBox _evernoteShowIgnoredCheckBox;
    private readonly ProgressBar _evernoteExportProgressBar;
    private readonly SemaphoreSlim _evernoteExportSemaphore = new(1, 1);
    private readonly Dictionary<WrappedApp, WebView2> _webViews = new();
    private readonly System.Windows.Forms.Timer _windowStateSaveTimer;
    private readonly System.Windows.Forms.Timer _topBarVisibilityTimer;
    private readonly System.Windows.Forms.Timer _evernotePollingTimer;
    private readonly System.Windows.Forms.Timer _googleDriveSyncTimer;
    private readonly System.Windows.Forms.Timer _pollingLockSyncTimer;
    private readonly System.Windows.Forms.Timer _settingsUiRefreshTimer;
    private readonly System.Windows.Forms.Timer _traySyncAnimationTimer;
    private CoreWebView2Environment? _webViewEnvironment;
    private AppState _appState;
    private ToolStripMenuItem _switchAppMenuItem = null!;
    private ToolStripMenuItem _toggleIgnoreEvernoteNodeMenuItem = null!;
    private ToolStripMenuItem _setExportFileNameEvernoteNodeMenuItem = null!;
    private ToolStripMenuItem _clearExportFileNameEvernoteNodeMenuItem = null!;
    private TreeNode? _evernoteContextNode;
    private bool _syncingEvernoteTreeChecks;
    private bool _evernotePollingInProgress;
    private bool _evernoteExportInProgress;
    private int _traySyncAnimationFrameIndex;
    private DateTime _lastEvernoteAutoExportUtc = DateTime.MinValue;
    private bool _googleDriveSyncInProgress;
    private bool _suspendGoogleDriveSyncQueue;
    private bool _syncingEvernoteShowIgnoredToggle;
    private bool _exitRequested;
    private bool _balloonShown;
    private bool _logoutInProgress;
    private WrappedApp _currentApp;
    private readonly string _localMachineHostName;
    private readonly string _localMachineInstanceSuffix;
    private readonly string _localMachineInstanceId;
    private readonly string _localMachineDefaultStateFileName;
    private readonly string _localMachineSuffixedStateFileName;
    private string _localMachineStateFileName;
    private string? _localMachineStateFileId;
    private string? _lockOwnerInstanceId;
    private string? _lockOwnerDisplayName;
    private bool _distributedPollingSyncInProgress;
    private string? _pendingTakeoverRequestId;
    private string? _pendingTakeoverTargetInstanceId;
    private string? _pendingTakeoverTargetDisplayName;
    private bool _pendingTakeoverObservedInDrive;
    private DateTimeOffset? _pendingTakeoverRequestedAtUtc;
    private SettingsForm? _settingsFormOpenInstance;
    private bool _settingsPauseChangeInProgress;
    private bool _settingsForceLockInProgress;
    private DateTimeOffset _nextPollingStateSyncAtUtc;
    private int _pendingTakeoverMissingObservationCount;
    private readonly Dictionary<string, DateTimeOffset?> _pollingStateMetaSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private List<GoogleDrivePollingStateFile> _cachedPollingStates = [];
    private DateTimeOffset _lastLocalPollingStateWriteUtc = DateTimeOffset.MinValue;
    private string? _lastLocalPollingStateSignature;

    internal MainForm()
    {
        AppLogger.Debug("MainForm constructor started.");
        _localMachineHostName = NormalizeHostName(Environment.MachineName);
        _localMachineInstanceSuffix = BuildStableInstanceSuffix();
        _localMachineInstanceId = $"{_localMachineHostName}:{_localMachineInstanceSuffix}";
        _localMachineDefaultStateFileName = BuildStateFileName(_localMachineHostName, null);
        _localMachineSuffixedStateFileName = BuildStateFileName(_localMachineHostName, _localMachineInstanceSuffix);
        _localMachineStateFileName = _localMachineDefaultStateFileName;
        _appState = AppStateStore.Load();
        _appState.Normalize();
        AppLogger.SetDebugLoggingEnabled(_appState.EnableDebugLogs);
        // Do not trust local PauseAutomaticPolling at startup; Drive state is source of truth.
        _appState.EvernotePollingPaused = true;
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
        _evernoteTreeNodeMenu = BuildEvernoteTreeNodeMenu();

        _webViewHost = new Panel
        {
            Dock = DockStyle.Fill
        };
        (_evernoteExportPanel,
            _evernoteDbPathTextBox,
            _evernoteTreeView,
            _evernoteStatusLabel,
            _evernoteShowIgnoredCheckBox,
            _evernoteExportProgressBar) = BuildEvernoteExportPanel();
        _webViewHost.Controls.Add(_evernoteExportPanel);

        Controls.Add(_webViewHost);
        Controls.Add(_topBar);

        _trayMenu = BuildTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = AppIconProvider.GetTrayIdleIcon(),
            Text = AppConfig.GetAppDisplayName(_currentApp),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        AppLogger.Debug("Tray icon initialized.");
        _trayIcon.MouseClick += TrayIcon_MouseClick;

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

        _evernotePollingTimer = new System.Windows.Forms.Timer();
        _evernotePollingTimer.Tick += EvernotePollingTimer_Tick;

        _googleDriveSyncTimer = new System.Windows.Forms.Timer
        {
            Interval = GoogleDriveSyncDebounceMs
        };
        _googleDriveSyncTimer.Tick += GoogleDriveSyncTimer_Tick;

        _pollingLockSyncTimer = new System.Windows.Forms.Timer
        {
            Interval = PollingLockSyncIntervalMs
        };
        _pollingLockSyncTimer.Tick += PollingLockSyncTimer_Tick;
        _nextPollingStateSyncAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(_pollingLockSyncTimer.Interval);

        _settingsUiRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _settingsUiRefreshTimer.Tick += (_, _) => UpdateSettingsLockStatus();

        _traySyncAnimationTimer = new System.Windows.Forms.Timer
        {
            Interval = TraySyncAnimationIntervalMs
        };
        _traySyncAnimationTimer.Tick += TraySyncAnimationTimer_Tick;
        AppLogger.Debug("Tray animation timer initialized.");

        AppLogger.Debug("Applying Evernote polling settings.");
        ApplyEvernotePollingSettings();

        UpdateAppChrome();
        RefreshTrayPollingIconState();
        AppLogger.Debug("MainForm constructor completed.");

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
            await TryAutoRestoreConfigFromGoogleDriveAsync();
            await SyncDistributedPollingStateAsync(showErrors: false, processIncomingRequests: false);

            _currentApp = Enum.IsDefined(typeof(WrappedApp), _appState.LastSelectedApp)
                ? _appState.LastSelectedApp
                : AppConfig.DefaultApp;
            if (_appSwitcher.SelectedIndex != (int)_currentApp)
            {
                _appSwitcher.SelectedIndex = (int)_currentApp;
            }

            _evernoteDbPathTextBox.Text = GetConfiguredEvernoteRootPath() ?? string.Empty;
            SyncEvernoteShowIgnoredCheckboxFromState();
            ApplyEvernotePollingSettings();
            UpdateAppChrome();

            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppConfig.WebViewUserDataFolder);

            await EnsureWebViewInitializedAsync(WrappedApp.Gemini);
            await EnsureWebViewInitializedAsync(WrappedApp.NotebookLm);
            await EnsureWebViewInitializedAsync(WrappedApp.GoogleDrive);
            ShowActiveContent();
        }
        catch (Exception exception)
        {
            AppLogger.Error("MainForm_Load failed.", exception);
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
        _pollingLockSyncTimer.Start();
        _nextPollingStateSyncAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(_pollingLockSyncTimer.Interval);
        _settingsUiRefreshTimer.Start();
        ApplyEvernotePollingSettings();
        LoadEvernoteTreeFromConfiguredRoot(showErrors: false);
        UpdateTopBarVisibility();
        _ = SyncDistributedPollingStateAsync(showErrors: false, processIncomingRequests: true);
    }

    private async Task TryAutoRestoreConfigFromGoogleDriveAsync()
    {
        var hasLocalConfigFile = File.Exists(AppConfig.LocalConfigFilePath) || File.Exists(AppConfig.LegacyStateFilePath);
        if (!GoogleDriveConfigSyncService.IsConfigured(_appState) &&
            TryAttachGoogleDriveCredentialsFromDefaultFile(out var loadedPath))
        {
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive OAuth client loaded from: {loadedPath}");
            if (!hasLocalConfigFile)
            {
                _appState.GoogleDriveSyncEnabled = true;
                _appState.GoogleDriveAutoRestoreOnStartup = true;
            }
        }

        var shouldAttemptAutoRestore = _appState.GoogleDriveAutoRestoreOnStartup || !hasLocalConfigFile;
        if (!shouldAttemptAutoRestore || !GoogleDriveConfigSyncService.IsConfigured(_appState))
        {
            return;
        }

        var localFallbackState = _appState;

        var downloadResult = await GoogleDriveConfigSyncService.DownloadConfigAsync(_appState, CancellationToken.None);
        if (downloadResult.IsNotFound)
        {
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive config auto-restore: no remote config found.");
            return;
        }

        if (!downloadResult.IsSuccess)
        {
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive config auto-restore failed: {downloadResult.Error}");
            return;
        }

        if (string.IsNullOrWhiteSpace(downloadResult.ConfigJson) ||
            !AppStateStore.TryDeserialize(downloadResult.ConfigJson, out var remoteState))
        {
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive config auto-restore skipped: invalid remote JSON.");
            return;
        }

        if (string.IsNullOrWhiteSpace(remoteState.GoogleDriveClientId))
        {
            remoteState.GoogleDriveClientId = localFallbackState.GoogleDriveClientId;
        }

        if (string.IsNullOrWhiteSpace(remoteState.GoogleDriveClientSecret))
        {
            remoteState.GoogleDriveClientSecret = localFallbackState.GoogleDriveClientSecret;
        }

        if (string.IsNullOrWhiteSpace(remoteState.GoogleDriveConfigFileId))
        {
            remoteState.GoogleDriveConfigFileId = downloadResult.FileId ?? localFallbackState.GoogleDriveConfigFileId;
        }

        remoteState.GoogleDriveSyncEnabled = localFallbackState.GoogleDriveSyncEnabled || remoteState.GoogleDriveSyncEnabled;
        remoteState.GoogleDriveAutoRestoreOnStartup =
            localFallbackState.GoogleDriveAutoRestoreOnStartup || remoteState.GoogleDriveAutoRestoreOnStartup;
        ApplyMachineLocalEvernoteSettings(source: localFallbackState, target: remoteState);

        _appState = remoteState;
        _appState.Normalize();

        _suspendGoogleDriveSyncQueue = true;
        try
        {
            AppStateStore.Save(_appState);
        }
        finally
        {
            _suspendGoogleDriveSyncQueue = false;
        }

        var modifiedInfo = downloadResult.ModifiedTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Config restored from Google Drive (modified: {modifiedInfo}).");
    }

    private bool TryAttachGoogleDriveCredentialsFromDefaultFile(out string sourcePath)
    {
        sourcePath = string.Empty;
        if (!GoogleDriveConfigSyncService.TryLoadClientSecretsFromDefaultLocations(
                out var clientId,
                out var clientSecret,
                out sourcePath,
                out var error))
        {
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive OAuth client not available: {error}");
            return false;
        }

        _appState.GoogleDriveClientId = clientId;
        _appState.GoogleDriveClientSecret = clientSecret;
        return true;
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
            Width = 170,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        foreach (var app in Enum.GetValues<WrappedApp>())
        {
            switcher.Items.Add(AppConfig.GetAppDisplayName(app));
        }

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
        var logoutButton = new ToolStripButton("Log out", null, async (_, _) => await LogoutAsync())
        {
            Alignment = ToolStripItemAlignment.Right
        };

        topBar.Items.Add(new ToolStripLabel("App:"));
        topBar.Items.Add(_appSwitcher);
        topBar.Items.Add(new ToolStripSeparator());
        topBar.Items.Add(new ToolStripButton("Refresh", null, (_, _) => RefreshCurrentApp()));
        topBar.Items.Add(new ToolStripButton("Settings", null, (_, _) => OpenSettings()));
        topBar.Items.Add(logoutButton);

        return topBar;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        _switchAppMenuItem = new ToolStripMenuItem("Switch to");
        menu.Opening += (_, _) => UpdateTrayMenuItems();

        menu.Items.Add(_switchAppMenuItem);
        menu.Items.Add("Refresh", null, (_, _) => RefreshCurrentApp());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Log out", null, async (_, _) => await LogoutAsync());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        UpdateTrayMenuItems();

        return menu;
    }

    private ContextMenuStrip BuildEvernoteTreeNodeMenu()
    {
        var menu = new ContextMenuStrip();
        _toggleIgnoreEvernoteNodeMenuItem = new ToolStripMenuItem("Ignore");
        _toggleIgnoreEvernoteNodeMenuItem.Click += (_, _) => ToggleIgnoreForContextNode();
        _setExportFileNameEvernoteNodeMenuItem = new ToolStripMenuItem("Set export file name...");
        _setExportFileNameEvernoteNodeMenuItem.Click += (_, _) => SetExportFileNameForContextNode();
        _clearExportFileNameEvernoteNodeMenuItem = new ToolStripMenuItem("Clear export file name");
        _clearExportFileNameEvernoteNodeMenuItem.Click += (_, _) => ClearExportFileNameForContextNode();
        menu.Items.Add(_toggleIgnoreEvernoteNodeMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_setExportFileNameEvernoteNodeMenuItem);
        menu.Items.Add(_clearExportFileNameEvernoteNodeMenuItem);
        menu.Opening += (_, _) => UpdateEvernoteNodeMenuState();
        return menu;
    }

    private (
        Panel Panel,
        TextBox PathTextBox,
        TreeView TreeView,
        Label StatusLabel,
        CheckBox ShowIgnoredCheckBox,
        ProgressBar ExportProgressBar) BuildEvernoteExportPanel()
    {
        var container = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Evernote Export"
        };

        var subtitleLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 12),
            Text = "Selectionne le dossier racine Evernote, puis exporte les notebooks coches. Clic droit: ignorer ou definir le nom de fichier d'export."
        };

        var rootPathLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true
        };
        rootPathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootPathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var rootPathTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true
        };

        var chooseRootFolderButton = new Button
        {
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
            Text = "Dossier Evernote..."
        };
        chooseRootFolderButton.Click += (_, _) => ChooseEvernoteRootPath();

        rootPathLayout.Controls.Add(rootPathTextBox, 0, 0);
        rootPathLayout.Controls.Add(chooseRootFolderButton, 1, 0);

        var actionsLayout = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0)
        };

        var reloadButton = new Button
        {
            AutoSize = true,
            Text = "Recharger"
        };
        reloadButton.Click += (_, _) => LoadEvernoteTreeFromConfiguredRoot(showErrors: true);

        var exportButton = new Button
        {
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
            Text = "Export"
        };
        exportButton.Click += async (_, _) => await ExportSelectedEvernoteContentToMarkdownAsync(
            showDialogs: true,
            source: "manual",
            targetExportFileNames: null);

        actionsLayout.Controls.Add(reloadButton);
        actionsLayout.Controls.Add(exportButton);

        var statusLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            Text = "Aucun dossier Evernote selectionne."
        };

        var treeHeaderLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        treeHeaderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        treeHeaderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var showIgnoredCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Afficher ignores",
            Checked = _appState.EvernoteShowIgnoredItems,
            Anchor = AnchorStyles.Right
        };
        showIgnoredCheckBox.CheckedChanged += EvernoteShowIgnoredCheckBox_CheckedChanged;

        treeHeaderLayout.Controls.Add(new Label { AutoSize = true, Text = string.Empty }, 0, 0);
        treeHeaderLayout.Controls.Add(showIgnoredCheckBox, 1, 0);

        var exportProgressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Continuous,
            Visible = false,
            Minimum = 0,
            Maximum = 1,
            Value = 0
        };

        var treeView = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            HideSelection = false
        };
        treeView.AfterCheck += EvernoteTreeView_AfterCheck;
        treeView.NodeMouseClick += EvernoteTreeView_NodeMouseClick;
        treeView.BeforeExpand += EvernoteTreeView_BeforeExpand;

        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(subtitleLabel, 0, 1);
        layout.Controls.Add(rootPathLayout, 0, 2);
        layout.Controls.Add(actionsLayout, 0, 3);
        layout.Controls.Add(statusLabel, 0, 4);
        layout.Controls.Add(treeHeaderLayout, 0, 5);
        layout.Controls.Add(exportProgressBar, 0, 6);
        layout.Controls.Add(treeView, 0, 7);

        container.Controls.Add(layout);

        var configuredRootPath = GetConfiguredEvernoteRootPath();
        rootPathTextBox.Text = configuredRootPath ?? string.Empty;
        return (container, rootPathTextBox, treeView, statusLabel, showIgnoredCheckBox, exportProgressBar);
    }

    private void EvernoteShowIgnoredCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        if (_syncingEvernoteShowIgnoredToggle)
        {
            return;
        }

        var shouldShowIgnored = _evernoteShowIgnoredCheckBox.Checked;
        if (_appState.EvernoteShowIgnoredItems == shouldShowIgnored)
        {
            return;
        }

        _appState.EvernoteShowIgnoredItems = shouldShowIgnored;
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Evernote show ignored set: {shouldShowIgnored}");
        QueueAppStateSave();
        LoadEvernoteTreeFromConfiguredRoot(showErrors: false, refreshTracking: false);
    }

    private void ChooseEvernoteRootPath()
    {
        var currentPath = GetConfiguredEvernoteRootPath();
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisir le dossier racine de l'installation Evernote",
            ShowNewFolderButton = false
        };

        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.SelectedPath = currentPath;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.SelectedPath);
        if (string.Equals(currentPath, selectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appState.EvernoteLocalDbPath = selectedPath;
        _evernoteDbPathTextBox.Text = selectedPath;
        ResetEvernoteTrackingBaseline();
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Evernote root path set: {selectedPath}");
        QueueAppStateSave();

        LoadEvernoteTreeFromConfiguredRoot(showErrors: true);
    }

    private string? GetConfiguredEvernoteRootPath()
    {
        var savedPath = _appState.EvernoteLocalDbPath;
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            return null;
        }

        try
        {
            if (File.Exists(savedPath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(savedPath));
            }

            return Path.GetFullPath(savedPath);
        }
        catch
        {
            return savedPath;
        }
    }

    private void LoadEvernoteTreeFromConfiguredRoot(bool showErrors, bool refreshTracking = true)
    {
        var rootPath = GetConfiguredEvernoteRootPath();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            PopulateEvernoteTree([]);
            SetEvernoteStatus("Aucun dossier Evernote selectionne.");
            return;
        }

        try
        {
            var stacks = EvernoteLocalDbService.GetStacksAndNotebooks(rootPath, out var dbPath);
            PopulateEvernoteTree(stacks);
            SetEvernoteStatus(
                $"DB detectee: {dbPath} | Stacks: {stacks.Count} | Notebooks: {stacks.Sum(stack => stack.Notebooks.Count)}");
            if (refreshTracking)
            {
                PollEvernoteTracking(allowAutoExport: false, showErrors: false, ignorePause: false);
            }
        }
        catch (Exception exception)
        {
            PopulateEvernoteTree([]);
            SetEvernoteStatus($"Erreur DB: {exception.Message}");
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    $"Impossible de lire la base Evernote.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                    "Evernote Export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private void SetEvernoteStatus(string message)
    {
        _evernoteStatusLabel.Text = message;
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Evernote status: {message}");
    }

    private void SyncEvernoteShowIgnoredCheckboxFromState()
    {
        _syncingEvernoteShowIgnoredToggle = true;
        try
        {
            _evernoteShowIgnoredCheckBox.Checked = _appState.EvernoteShowIgnoredItems;
        }
        finally
        {
            _syncingEvernoteShowIgnoredToggle = false;
        }
    }

    private void PopulateEvernoteTree(IReadOnlyList<EvernoteStackInfo> stacks)
    {
        _syncingEvernoteTreeChecks = true;
        _evernoteTreeView.BeginUpdate();

        try
        {
            _evernoteTreeView.Nodes.Clear();
            var showIgnoredItems = _appState.EvernoteShowIgnoredItems;
            foreach (var stack in stacks)
            {
                var stackIgnored = _appState.IsEvernoteStackIgnored(stack.Id);
                if (stackIgnored && !showIgnoredItems)
                {
                    continue;
                }

                var notebooksToDisplay = stack.Notebooks
                    .Where(notebook => showIgnoredItems || !_appState.IsEvernoteNotebookIgnored(notebook.Id))
                    .ToArray();
                if (notebooksToDisplay.Length == 0 && !showIgnoredItems)
                {
                    continue;
                }

                var stackExportFileName = _appState.GetEvernoteStackExportFileName(stack.Id);
                var stackNode = new TreeNode(
                    FormatStackNodeText(
                        stack.DisplayName,
                        notebooksToDisplay.Length,
                        stack.LatestChangeMs,
                        stackIgnored,
                        stackExportFileName))
                {
                    Tag = new EvernoteTreeNodeTag(
                        EvernoteTreeNodeKind.Stack,
                        stack.Id,
                        stack.DisplayName,
                        notebooksToDisplay.Length,
                        stack.LatestChangeMs)
                };

                var anyNotebookSelected = false;
                foreach (var notebook in notebooksToDisplay)
                {
                    var notebookSelected = _appState.IsEvernoteNotebookSelected(notebook.Id);
                    anyNotebookSelected = anyNotebookSelected || notebookSelected;
                    var notebookIgnored = _appState.IsEvernoteNotebookIgnored(notebook.Id);
                    var notebookExportFileName = _appState.GetEvernoteNotebookExportFileName(notebook.Id);

                    stackNode.Nodes.Add(new TreeNode(
                        FormatNotebookNodeText(
                            notebook.Name,
                            notebook.NoteCount,
                            notebook.LatestChangeMs,
                            notebookIgnored,
                            notebookExportFileName))
                    {
                        Tag = new EvernoteTreeNodeTag(
                            EvernoteTreeNodeKind.Notebook,
                            notebook.Id,
                            notebook.Name,
                            notebook.NoteCount,
                            notebook.LatestChangeMs),
                        Checked = notebookSelected
                    });
                }

                stackNode.Checked = _appState.IsEvernoteStackSelected(stack.Id) || anyNotebookSelected;
                ApplyIgnoreVisualState(stackNode);
                _evernoteTreeView.Nodes.Add(stackNode);
                if (stackIgnored)
                {
                    stackNode.Collapse();
                }
                else
                {
                    stackNode.Expand();
                }
            }
        }
        finally
        {
            _evernoteTreeView.EndUpdate();
            _syncingEvernoteTreeChecks = false;
        }
    }

    private static string FormatStackNodeText(
        string stackName,
        int notebookCount,
        long? latestChangeMs,
        bool ignored,
        string? exportFileName)
    {
        var suffix = ignored ? " [ignored]" : string.Empty;
        var dateText = FormatEvernoteTimestamp(latestChangeMs);
        var dateSuffix = string.IsNullOrWhiteSpace(dateText) ? string.Empty : $" | maj: {dateText}";
        var exportSuffix = string.IsNullOrWhiteSpace(exportFileName) ? string.Empty : $" | export: {exportFileName}.md";
        return $"{stackName} ({notebookCount} notebooks){exportSuffix}{dateSuffix}{suffix}";
    }

    private static string FormatNotebookNodeText(
        string notebookName,
        int noteCount,
        long? latestChangeMs,
        bool ignored,
        string? exportFileName)
    {
        var suffix = ignored ? " [ignored]" : string.Empty;
        var dateText = FormatEvernoteTimestamp(latestChangeMs);
        var dateSuffix = string.IsNullOrWhiteSpace(dateText) ? string.Empty : $" | maj: {dateText}";
        var exportSuffix = string.IsNullOrWhiteSpace(exportFileName) ? string.Empty : $" | export: {exportFileName}.md";
        return $"{notebookName} ({noteCount} notes){exportSuffix}{dateSuffix}{suffix}";
    }

    private static string FormatEvernoteTimestamp(long? timestampMs)
    {
        if (!timestampMs.HasValue)
        {
            return string.Empty;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeExportFileBaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '_');
        }

        normalized = normalized.Replace(".md", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
    }

    private void EvernoteTreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.Node.Tag is not EvernoteTreeNodeTag)
        {
            return;
        }

        _evernoteContextNode = e.Node;
        _evernoteTreeView.SelectedNode = e.Node;
        _evernoteTreeNodeMenu.Show(_evernoteTreeView, e.Location);
    }

    private void EvernoteTreeView_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        var node = e.Node;
        if (node is null || node.Tag is not EvernoteTreeNodeTag tag || tag.Kind != EvernoteTreeNodeKind.Stack)
        {
            return;
        }

        if (_appState.IsEvernoteStackIgnored(tag.Id))
        {
            e.Cancel = true;
        }
    }

    private void UpdateEvernoteNodeMenuState()
    {
        var node = _evernoteContextNode;
        if (node?.Tag is not EvernoteTreeNodeTag tag)
        {
            _toggleIgnoreEvernoteNodeMenuItem.Enabled = false;
            _toggleIgnoreEvernoteNodeMenuItem.Text = "Ignore";
            _setExportFileNameEvernoteNodeMenuItem.Enabled = false;
            _clearExportFileNameEvernoteNodeMenuItem.Enabled = false;
            return;
        }

        var ignored = tag.Kind == EvernoteTreeNodeKind.Stack
            ? _appState.IsEvernoteStackIgnored(tag.Id)
            : _appState.IsEvernoteNotebookIgnored(tag.Id);
        var currentExportFileName = tag.Kind == EvernoteTreeNodeKind.Stack
            ? _appState.GetEvernoteStackExportFileName(tag.Id)
            : _appState.GetEvernoteNotebookExportFileName(tag.Id);

        _toggleIgnoreEvernoteNodeMenuItem.Enabled = true;
        _toggleIgnoreEvernoteNodeMenuItem.Text = ignored
            ? $"Unignore {tag.Kind.ToString().ToLowerInvariant()}"
            : $"Ignore {tag.Kind.ToString().ToLowerInvariant()}";
        _setExportFileNameEvernoteNodeMenuItem.Enabled = true;
        _setExportFileNameEvernoteNodeMenuItem.Text = $"Set export file for {tag.Kind.ToString().ToLowerInvariant()}...";
        _clearExportFileNameEvernoteNodeMenuItem.Enabled = !string.IsNullOrWhiteSpace(currentExportFileName);
        _clearExportFileNameEvernoteNodeMenuItem.Text = string.IsNullOrWhiteSpace(currentExportFileName)
            ? "Clear export file name"
            : $"Clear export file ({currentExportFileName})";
    }

    private void ToggleIgnoreForContextNode()
    {
        var node = _evernoteContextNode;
        if (node?.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        if (tag.Kind == EvernoteTreeNodeKind.Stack)
        {
            var willIgnore = !_appState.IsEvernoteStackIgnored(tag.Id);
            _appState.SetEvernoteStackIgnored(tag.Id, willIgnore);
            LogEvernoteIgnoreChange("Stack", tag.Name, tag.Id, willIgnore);
            if (willIgnore)
            {
                node.Collapse();
            }
        }
        else
        {
            var willIgnore = !_appState.IsEvernoteNotebookIgnored(tag.Id);
            _appState.SetEvernoteNotebookIgnored(tag.Id, willIgnore);
            LogEvernoteIgnoreChange("Notebook", tag.Name, tag.Id, willIgnore);
        }

        UpdateNodeTextFromTag(node);
        ApplyIgnoreVisualState(node);
        if (node.Parent is not null)
        {
            UpdateNodeTextFromTag(node.Parent);
            ApplyIgnoreVisualState(node.Parent);
        }

        QueueAppStateSave();
    }

    private void SetExportFileNameForContextNode()
    {
        var node = _evernoteContextNode;
        if (node?.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        var currentValue = tag.Kind == EvernoteTreeNodeKind.Stack
            ? _appState.GetEvernoteStackExportFileName(tag.Id)
            : _appState.GetEvernoteNotebookExportFileName(tag.Id);

        var proposedValue = PromptForExportFileName(tag.Name, currentValue);
        if (proposedValue is null)
        {
            return;
        }

        if (tag.Kind == EvernoteTreeNodeKind.Stack)
        {
            _appState.SetEvernoteStackExportFileName(tag.Id, proposedValue);
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Stack export file set: {tag.Name} ({tag.Id}) -> {proposedValue}");
        }
        else
        {
            _appState.SetEvernoteNotebookExportFileName(tag.Id, proposedValue);
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Notebook export file set: {tag.Name} ({tag.Id}) -> {proposedValue}");
        }

        UpdateNodeTextFromTag(node);
        if (node.Parent is not null)
        {
            UpdateNodeTextFromTag(node.Parent);
        }

        QueueAppStateSave();
    }

    private void ClearExportFileNameForContextNode()
    {
        var node = _evernoteContextNode;
        if (node?.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        if (tag.Kind == EvernoteTreeNodeKind.Stack)
        {
            _appState.SetEvernoteStackExportFileName(tag.Id, null);
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Stack export file cleared: {tag.Name} ({tag.Id})");
        }
        else
        {
            _appState.SetEvernoteNotebookExportFileName(tag.Id, null);
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Notebook export file cleared: {tag.Name} ({tag.Id})");
        }

        UpdateNodeTextFromTag(node);
        if (node.Parent is not null)
        {
            UpdateNodeTextFromTag(node.Parent);
        }

        QueueAppStateSave();
    }

    private string? PromptForExportFileName(string containerName, string? currentValue)
    {
        using var dialog = new Form
        {
            Text = "Export File Name",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Width = 520,
            Height = 190
        };

        var label = new Label
        {
            Left = 12,
            Top = 12,
            Width = 480,
            Text = $"Nom de fichier d'export pour {containerName} (sans .md):"
        };

        var textBox = new TextBox
        {
            Left = 12,
            Top = 38,
            Width = 480,
            Text = currentValue ?? string.Empty
        };

        var hint = new Label
        {
            Left = 12,
            Top = 66,
            Width = 480,
            Text = "Le meme nom permet de grouper plusieurs stacks/notebooks."
        };

        var okButton = new Button
        {
            Text = "Save",
            Left = 326,
            Top = 98,
            Width = 80,
            DialogResult = DialogResult.OK
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 412,
            Top = 98,
            Width = 80,
            DialogResult = DialogResult.Cancel
        };

        dialog.Controls.Add(label);
        dialog.Controls.Add(textBox);
        dialog.Controls.Add(hint);
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        var sanitized = SanitizeExportFileBaseName(textBox.Text);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            MessageBox.Show(
                this,
                "Nom invalide. Utilise au moins un caractere valide.",
                "Evernote Export",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        return sanitized;
    }

    private void UpdateNodeTextFromTag(TreeNode node)
    {
        if (node.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        if (tag.Kind == EvernoteTreeNodeKind.Stack)
        {
            node.Text = FormatStackNodeText(
                tag.Name,
                tag.ItemCount,
                tag.LatestChangeMs,
                _appState.IsEvernoteStackIgnored(tag.Id),
                _appState.GetEvernoteStackExportFileName(tag.Id));
            return;
        }

        node.Text = FormatNotebookNodeText(
            tag.Name,
            tag.ItemCount,
            tag.LatestChangeMs,
            _appState.IsEvernoteNotebookIgnored(tag.Id),
            _appState.GetEvernoteNotebookExportFileName(tag.Id));
    }

    private void ApplyIgnoreVisualState(TreeNode node)
    {
        if (node.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        if (tag.Kind == EvernoteTreeNodeKind.Stack)
        {
            var ignored = _appState.IsEvernoteStackIgnored(tag.Id);
            node.ForeColor = ignored ? SystemColors.GrayText : SystemColors.WindowText;
            foreach (TreeNode child in node.Nodes)
            {
                ApplyIgnoreVisualState(child);
            }

            if (ignored)
            {
                node.Collapse();
            }

            return;
        }

        var parentIgnored = node.Parent?.Tag is EvernoteTreeNodeTag parentTag &&
                            parentTag.Kind == EvernoteTreeNodeKind.Stack &&
                            _appState.IsEvernoteStackIgnored(parentTag.Id);
        var notebookIgnored = _appState.IsEvernoteNotebookIgnored(tag.Id);
        node.ForeColor = parentIgnored || notebookIgnored ? SystemColors.GrayText : SystemColors.WindowText;
    }

    private async Task<bool> ExportSelectedEvernoteContentToMarkdownAsync(
        bool showDialogs,
        string source,
        IReadOnlyCollection<string>? targetExportFileNames)
    {
        if (!await _evernoteExportSemaphore.WaitAsync(0))
        {
            var message = "Un export est deja en cours.";
            if (showDialogs)
            {
                MessageBox.Show(
                    this,
                    message,
                    "Evernote Export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] {message}");
            }

            return false;
        }

        try
        {
            var rootPath = GetConfiguredEvernoteRootPath();
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                if (showDialogs)
                {
                    MessageBox.Show(
                        this,
                        "Choisis d'abord le dossier racine Evernote.",
                        "Evernote Export",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return false;
            }

            var selectedNotebooks = GetSelectedNotebooksForExport();
            if (selectedNotebooks.Count == 0)
            {
                if (showDialogs)
                {
                    MessageBox.Show(
                        this,
                        "Aucun notebook selectionne. Coche au moins un notebook ou une stack.",
                        "Evernote Export",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return false;
            }

            var normalizedTargetExportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targetExportFileNames is not null)
            {
                foreach (var target in targetExportFileNames)
                {
                    var normalized = SanitizeExportFileBaseName(target);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        normalizedTargetExportNames.Add(normalized);
                    }
                }
            }

            var groups = selectedNotebooks
                .Where(notebook => normalizedTargetExportNames.Count == 0 ||
                                   normalizedTargetExportNames.Contains(notebook.ExportFileBaseName))
                .GroupBy(notebook => notebook.ExportFileBaseName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new EvernoteExportGroupWorkItem(
                    group.Key,
                    group.Select(notebook => notebook.NotebookId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .Where(group => group.NotebookIds.Count > 0)
                .ToList();

            if (groups.Count == 0)
            {
                SetEvernoteStatus("Aucun groupe d'export cible.");
                return false;
            }

            var exportRootDirectory = Path.Combine(Directory.GetCurrentDirectory(), AppConfig.EvernoteExportRootFolderName);
            var exportBackupsDirectory = Path.Combine(exportRootDirectory, AppConfig.EvernoteExportBackupsFolderName);
            Directory.CreateDirectory(exportRootDirectory);
            Directory.CreateDirectory(exportBackupsDirectory);

            var totalSteps = Math.Max(1, groups.Count);
            BeginEvernoteExportProgress(totalSteps, source);

            var maxBackupsToKeep = _appState.MaxMarkdownFilesToKeep;
            IProgress<EvernoteExportProgressState> progress = new Progress<EvernoteExportProgressState>(state =>
            {
                SetEvernoteExportProgress(state.CompletedSteps, totalSteps, state.Message);
            });

            var completedSteps = 0;
            var exportTasks = groups
                .Select(group => Task.Run(() =>
                {
                    var result = EvernoteLocalDbService.ExportNotebookGroupToMarkdown(
                        rootPath,
                        group.NotebookIds,
                        group.ExportFileBaseName,
                        exportRootDirectory,
                        exportBackupsDirectory,
                        maxBackupsToKeep);

                    var finished = Interlocked.Increment(ref completedSteps);
                    progress.Report(new EvernoteExportProgressState(
                        finished,
                        $"Export termine ({finished}/{groups.Count}): {group.ExportFileBaseName}.md"));
                    return result;
                }))
                .ToArray();

            var groupResults = (await Task.WhenAll(exportTasks)).ToList();

            if (groupResults.Count == 0)
            {
                SetEvernoteStatus("Aucun export genere.");
                return false;
            }

            if (GoogleDriveConfigSyncService.IsConfigured(_appState))
            {
                var uploadItems = new List<EvernoteDriveFileUploadItem>();
                foreach (var result in groupResults)
                {
                    uploadItems.Add(new EvernoteDriveFileUploadItem(
                        result.OutputFilePath,
                        IsBackup: false,
                        ConvertToGoogleDoc: true));
                    if (!string.IsNullOrWhiteSpace(result.BackupFilePath))
                    {
                        uploadItems.Add(new EvernoteDriveFileUploadItem(
                            result.BackupFilePath,
                            IsBackup: true,
                            ConvertToGoogleDoc: false));
                    }
                }

                if (uploadItems.Count > 0)
                {
                    SetEvernoteExportProgressIndeterminate("Sync Google Drive en cours...");
                    var driveSyncResult = await GoogleDriveConfigSyncService.SyncEvernoteMarkdownFilesAsync(
                        _appState,
                        uploadItems,
                        CancellationToken.None);

                    if (!driveSyncResult.IsSuccess)
                    {
                        AppLogger.Debug(
                            $"[{DateTime.Now:HH:mm:ss}] Google Drive markdown sync failed: {driveSyncResult.Error}");
                    }
                    else
                    {
                        AppLogger.Debug(
                            $"[{DateTime.Now:HH:mm:ss}] Google Drive markdown sync ok: {driveSyncResult.UploadedFiles} file(s), {driveSyncResult.ConvertedGoogleDocs} Google Doc(s).");
                    }
                }
            }

            var totalNotes = groupResults.Sum(result => result.ExportedNotes);
            var deletedBackups = groupResults.Sum(result => result.DeletedBackupFiles);
            var exportedFiles = string.Join(
                ", ",
                groupResults.Select(result => Path.GetFileName(result.OutputFilePath)));
            var cleanupInfo = deletedBackups > 0 ? $" | backups supprimes: {deletedBackups}" : string.Empty;
            var status = $"Export termine ({source}): {groupResults.Count} fichiers, {totalNotes} notes -> {exportedFiles}{cleanupInfo}";
            SetEvernoteStatus(status);
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] {status}");

            if (showDialogs)
            {
                var filesLine = string.Join(
                    Environment.NewLine,
                    groupResults.Select(result => $"- {Path.GetFileName(result.OutputFilePath)} ({result.ExportedNotes} notes)"));
                var backupLine = deletedBackups > 0
                    ? $"{Environment.NewLine}{Environment.NewLine}Backups supprimes: {deletedBackups}"
                    : string.Empty;
                MessageBox.Show(
                    this,
                    $"Export termine.{Environment.NewLine}{Environment.NewLine}Fichiers:{Environment.NewLine}{filesLine}{backupLine}",
                    "Evernote Export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return true;
        }
        catch (Exception exception)
        {
            SetEvernoteStatus($"Export echoue: {exception.Message}");
            if (showDialogs)
            {
                MessageBox.Show(
                    this,
                    $"Export impossible.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                    "Evernote Export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }
        finally
        {
            EndEvernoteExportProgress();
            _evernoteExportSemaphore.Release();
        }
    }

    private void BeginEvernoteExportProgress(int totalSteps, string source)
    {
        _evernoteExportInProgress = true;
        RefreshTrayPollingIconState();

        _evernoteExportProgressBar.Style = ProgressBarStyle.Continuous;
        _evernoteExportProgressBar.MarqueeAnimationSpeed = 0;
        _evernoteExportProgressBar.Minimum = 0;
        _evernoteExportProgressBar.Maximum = Math.Max(1, totalSteps);
        _evernoteExportProgressBar.Value = 0;
        _evernoteExportProgressBar.Visible = true;
        SetEvernoteStatus($"Export {source} en cours...");
    }

    private void SetEvernoteExportProgress(int completedSteps, int totalSteps, string message)
    {
        var max = Math.Max(1, totalSteps);
        if (_evernoteExportProgressBar.Style != ProgressBarStyle.Continuous)
        {
            _evernoteExportProgressBar.Style = ProgressBarStyle.Continuous;
            _evernoteExportProgressBar.MarqueeAnimationSpeed = 0;
        }

        _evernoteExportProgressBar.Maximum = max;
        var boundedValue = Math.Clamp(completedSteps, 0, max);
        _evernoteExportProgressBar.Value = boundedValue;
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetEvernoteStatus(message);
        }
    }

    private void SetEvernoteExportProgressIndeterminate(string message)
    {
        _evernoteExportProgressBar.Style = ProgressBarStyle.Marquee;
        _evernoteExportProgressBar.MarqueeAnimationSpeed = 22;
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetEvernoteStatus(message);
        }
    }

    private void EndEvernoteExportProgress()
    {
        _evernoteExportProgressBar.Style = ProgressBarStyle.Continuous;
        _evernoteExportProgressBar.MarqueeAnimationSpeed = 0;
        _evernoteExportProgressBar.Value = 0;
        _evernoteExportProgressBar.Visible = false;

        _evernoteExportInProgress = false;
        RefreshTrayPollingIconState();
    }

    private List<SelectedEvernoteNotebookForExport> GetSelectedNotebooksForExport()
    {
        var notebooks = new List<SelectedEvernoteNotebookForExport>();
        foreach (TreeNode stackNode in _evernoteTreeView.Nodes)
        {
            if (stackNode.Tag is not EvernoteTreeNodeTag stackTag || stackTag.Kind != EvernoteTreeNodeKind.Stack)
            {
                continue;
            }

            if (_appState.IsEvernoteStackIgnored(stackTag.Id))
            {
                continue;
            }

            foreach (TreeNode notebookNode in stackNode.Nodes)
            {
                if (!notebookNode.Checked ||
                    notebookNode.Tag is not EvernoteTreeNodeTag notebookTag ||
                    notebookTag.Kind != EvernoteTreeNodeKind.Notebook)
                {
                    continue;
                }

                if (_appState.IsEvernoteNotebookIgnored(notebookTag.Id))
                {
                    continue;
                }

                var exportFileBaseName = ResolveExportFileBaseName(stackTag, notebookTag);
                notebooks.Add(new SelectedEvernoteNotebookForExport(
                    notebookTag.Id,
                    notebookTag.Name,
                    stackTag.Id,
                    stackTag.Name,
                    exportFileBaseName));
            }
        }

        return notebooks
            .GroupBy(notebook => notebook.NotebookId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private string ResolveExportFileBaseName(EvernoteTreeNodeTag stackTag, EvernoteTreeNodeTag notebookTag)
    {
        var notebookAssignment = _appState.GetEvernoteNotebookExportFileName(notebookTag.Id);
        var stackAssignment = _appState.GetEvernoteStackExportFileName(stackTag.Id);
        var fallback = stackTag.Name;

        var resolved = notebookAssignment ?? stackAssignment ?? fallback;
        var sanitized = SanitizeExportFileBaseName(resolved);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "export_evernote";
        }

        return sanitized;
    }

    private void EvernoteTreeView_AfterCheck(object? sender, TreeViewEventArgs e)
    {
        var changedNode = e.Node;
        if (_syncingEvernoteTreeChecks || changedNode is null || changedNode.Tag is not EvernoteTreeNodeTag tag)
        {
            return;
        }

        _syncingEvernoteTreeChecks = true;
        try
        {
            if (tag.Kind == EvernoteTreeNodeKind.Stack)
            {
                var previousStackSelection = _appState.IsEvernoteStackSelected(tag.Id);
                _appState.SetEvernoteStackSelection(tag.Id, changedNode.Checked);
                if (previousStackSelection != changedNode.Checked)
                {
                    LogEvernoteSelection("Stack", tag.Name, tag.Id, changedNode.Checked);
                }

                foreach (TreeNode notebookNode in changedNode.Nodes)
                {
                    notebookNode.Checked = changedNode.Checked;
                    ApplyNotebookSelectionFromNode(notebookNode);
                }
            }
            else
            {
                ApplyNotebookSelectionFromNode(changedNode);
                SyncStackSelectionFromChildren(changedNode.Parent);
            }

            QueueAppStateSave();
        }
        finally
        {
            _syncingEvernoteTreeChecks = false;
        }
    }

    private void ApplyNotebookSelectionFromNode(TreeNode notebookNode)
    {
        if (notebookNode.Tag is not EvernoteTreeNodeTag notebookTag ||
            notebookTag.Kind != EvernoteTreeNodeKind.Notebook)
        {
            return;
        }

        var previousNotebookSelection = _appState.IsEvernoteNotebookSelected(notebookTag.Id);
        _appState.SetEvernoteNotebookSelection(notebookTag.Id, notebookNode.Checked);
        if (previousNotebookSelection != notebookNode.Checked)
        {
            LogEvernoteSelection("Notebook", notebookTag.Name, notebookTag.Id, notebookNode.Checked);
        }
    }

    private void SyncStackSelectionFromChildren(TreeNode? stackNode)
    {
        if (stackNode?.Tag is not EvernoteTreeNodeTag stackTag ||
            stackTag.Kind != EvernoteTreeNodeKind.Stack)
        {
            return;
        }

        var shouldBeChecked = stackNode.Nodes.Cast<TreeNode>().Any(child => child.Checked);
        var previousStackSelection = _appState.IsEvernoteStackSelected(stackTag.Id);

        if (stackNode.Checked != shouldBeChecked)
        {
            stackNode.Checked = shouldBeChecked;
        }

        _appState.SetEvernoteStackSelection(stackTag.Id, shouldBeChecked);
        if (previousStackSelection != shouldBeChecked)
        {
            LogEvernoteSelection("Stack", stackTag.Name, stackTag.Id, shouldBeChecked);
        }
    }

    private static void LogEvernoteSelection(string type, string name, string id, bool isSelected)
    {
        var action = isSelected ? "selected" : "deselected";
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] {type} {name} ({id}) {action}.");
    }

    private static void LogEvernoteIgnoreChange(string type, string name, string id, bool ignored)
    {
        var action = ignored ? "ignored" : "unignored";
        AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] {type} {name} ({id}) {action}.");
    }

    private void AppSwitcher_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_appSwitcher.SelectedIndex < 0)
        {
            return;
        }

        var app = (WrappedApp)_appSwitcher.SelectedIndex;
        if (!Enum.IsDefined(typeof(WrappedApp), app))
        {
            app = AppConfig.DefaultApp;
        }

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
        ShowActiveContent();

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

    private void RefreshCurrentApp()
    {
        if (_currentApp == WrappedApp.EvernoteExport)
        {
            LoadEvernoteTreeFromConfiguredRoot(showErrors: true);
            return;
        }

        GetCurrentWebView()?.CoreWebView2?.Reload();
    }

    private void EvernotePollingTimer_Tick(object? sender, EventArgs e)
    {
        LoadEvernoteTreeFromConfiguredRoot(showErrors: false, refreshTracking: false);
        PollEvernoteTracking(allowAutoExport: true, showErrors: false, ignorePause: false);
    }

    private async void PollingLockSyncTimer_Tick(object? sender, EventArgs e)
    {
        _nextPollingStateSyncAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(_pollingLockSyncTimer.Interval);
        UpdateSettingsLockStatus();
        LogPollingLock($"Timer tick: launching distributed sync (next in {_pollingLockSyncTimer.Interval / 1000}s).");
        await SyncDistributedPollingStateAsync(showErrors: false, processIncomingRequests: true);
    }

    private void ApplyEvernotePollingSettings()
    {
        var intervalMinutes = Math.Max(1, _appState.EvernotePollingIntervalMinutes);
        _evernotePollingTimer.Interval = checked(intervalMinutes * 60 * 1000);
        RefreshTrayPollingIconState();

        if (_appState.EvernotePollingPaused)
        {
            _evernotePollingTimer.Stop();
            return;
        }

        if (!_evernotePollingTimer.Enabled)
        {
            _evernotePollingTimer.Start();
        }
    }

    private async Task SyncDistributedPollingStateAsync(bool showErrors, bool processIncomingRequests)
    {
        if (_distributedPollingSyncInProgress)
        {
            LogPollingLock("Sync skipped: another sync is already in progress.");
            UpdateSettingsLockStatus();
            return;
        }

        if (!GoogleDriveConfigSyncService.IsConfigured(_appState))
        {
            _lockOwnerInstanceId = _localMachineInstanceId;
            _lockOwnerDisplayName = GetLocalDisplayName();
            LogPollingLock("Sync skipped: Google Drive sync not configured, local instance treated as lock owner.");
            UpdateSettingsLockStatus();
            return;
        }

        _distributedPollingSyncInProgress = true;
        try
        {
            LogPollingLock($"Sync start (processIncomingRequests={processIncomingRequests}, paused={_appState.EvernotePollingPaused}, pending={DescribePendingLocalRequest()}).");
            var metaListResult = await GoogleDriveConfigSyncService.ListPollingStateMetasAsync(_appState, CancellationToken.None);
            if (!metaListResult.IsSuccess)
            {
                LogPollingLock($"State meta list #1 failed: {metaListResult.Error ?? "unknown error"}");
                if (showErrors && !string.IsNullOrWhiteSpace(metaListResult.Error))
                {
                    MessageBox.Show(
                        this,
                        $"Impossible de lire les state files de synchronisation.{Environment.NewLine}{Environment.NewLine}{metaListResult.Error}",
                        "Distributed Polling Lock",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return;
            }

            var states = await ResolvePollingStatesFromMetasAsync(metaListResult.States, reason: "phase1");
            if (states is null)
            {
                return;
            }
            var existingLocalState = states.FirstOrDefault(file =>
                string.Equals(file.Document.InstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase));

            if (existingLocalState is not null)
            {
                _localMachineStateFileId = existingLocalState.FileId;
                _localMachineStateFileName = existingLocalState.FileName;
                if (existingLocalState.Document.PendingTakeoverRequest?.IsActive == true &&
                    string.Equals(existingLocalState.Document.PendingTakeoverRequest.RequestedByInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingTakeoverRequestId = existingLocalState.Document.PendingTakeoverRequest.RequestId;
                    _pendingTakeoverTargetInstanceId = existingLocalState.Document.PendingTakeoverRequest.RequestedToInstanceId;
                    _pendingTakeoverTargetDisplayName = existingLocalState.Document.PendingTakeoverRequest.RequestedToDisplayName;
                    _pendingTakeoverObservedInDrive = true;
                    _pendingTakeoverRequestedAtUtc = existingLocalState.Document.PendingTakeoverRequest.RequestedAtUtc;
                }

                // If remote already reflects an approved takeover, avoid re-writing an old pending request locally.
                if (!string.IsNullOrWhiteSpace(_pendingTakeoverRequestId) &&
                    existingLocalState.Document.PendingTakeoverRequest is null &&
                    !existingLocalState.Document.PauseAutomaticPolling)
                {
                    _appState.EvernotePollingPaused = false;
                    _pendingTakeoverRequestId = null;
                    _pendingTakeoverTargetInstanceId = null;
                    _pendingTakeoverTargetDisplayName = null;
                    _pendingTakeoverRequestedAtUtc = null;
                    _pendingTakeoverMissingObservationCount = 0;
                    LogPollingLock("Remote state indicates takeover already approved; adopting active local state before upsert.");
                }

                _lastLocalPollingStateWriteUtc = existingLocalState.Document.UpdatedAtUtc;
                _lastLocalPollingStateSignature = BuildLocalPollingStateSignature(existingLocalState.Document);
            }
            else
            {
                _localMachineStateFileName = ResolveLocalStateFileName(states);
            }

            var localDocument = BuildLocalPollingStateDocument();
            if (ShouldUpsertLocalPollingState(localDocument))
            {
                LogPollingLock($"Upserting local state '{_localMachineStateFileName}' (fileId={_localMachineStateFileId ?? "new"}): paused={localDocument.PauseAutomaticPolling}, pending={DescribePending(localDocument.PendingTakeoverRequest)}.");
                var upsertResult = await GoogleDriveConfigSyncService.UpsertPollingStateAsync(
                    _appState,
                    _localMachineStateFileName,
                    localDocument,
                    _localMachineStateFileId,
                    CancellationToken.None);

                if (!upsertResult.IsSuccess)
                {
                    LogPollingLock($"Local state upsert failed: {upsertResult.Error ?? "unknown error"}");
                    if (showErrors && !string.IsNullOrWhiteSpace(upsertResult.Error))
                    {
                        MessageBox.Show(
                            this,
                            $"Impossible de mettre a jour le state local de synchronisation.{Environment.NewLine}{Environment.NewLine}{upsertResult.Error}",
                            "Distributed Polling Lock",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }

                    return;
                }

                if (!string.IsNullOrWhiteSpace(upsertResult.FileId))
                {
                    _localMachineStateFileId = upsertResult.FileId;
                }
                _lastLocalPollingStateWriteUtc = DateTimeOffset.UtcNow;
                _lastLocalPollingStateSignature = BuildLocalPollingStateSignature(localDocument);
                LogPollingLock($"Local state upsert ok (fileId={_localMachineStateFileId ?? "n/a"}).");
                UpdateCachedLocalPollingState(localDocument);
            }
            else
            {
                LogPollingLock("Local state upsert skipped (no relevant change, heartbeat not due).");
            }

            metaListResult = await GoogleDriveConfigSyncService.ListPollingStateMetasAsync(_appState, CancellationToken.None);
            if (!metaListResult.IsSuccess)
            {
                LogPollingLock($"State meta list #2 failed: {metaListResult.Error ?? "unknown error"}");
                return;
            }

            states = await ResolvePollingStatesFromMetasAsync(metaListResult.States, reason: "phase2");
            if (states is null)
            {
                return;
            }
            var owner = DeterminePollingLockOwner(states);
            _lockOwnerInstanceId = owner?.Document.InstanceId;
            _lockOwnerDisplayName = owner?.Document.DisplayName;
            SyncPendingTakeoverFieldsFromLocalState(states, owner);
            LogPollingLock($"Owner resolved: {(_lockOwnerDisplayName ?? "(none)")}, pending(local)={DescribePendingLocalRequest()}.");

            if (!string.Equals(_lockOwnerInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase) &&
                !_appState.EvernotePollingPaused)
            {
                LogPollingLock("Detected local active polling without lock ownership; forcing local pause.");
                _appState.EvernotePollingPaused = true;
                ApplyEvernotePollingSettings();
                SaveAppStateNow(queueGoogleDriveSync: false);
                localDocument = BuildLocalPollingStateDocument();
                var forcedPauseResult = await GoogleDriveConfigSyncService.UpsertPollingStateAsync(
                    _appState,
                    _localMachineStateFileName,
                    localDocument,
                    _localMachineStateFileId,
                    CancellationToken.None);
                if (forcedPauseResult.IsSuccess && !string.IsNullOrWhiteSpace(forcedPauseResult.FileId))
                {
                    _localMachineStateFileId = forcedPauseResult.FileId;
                }
                LogPollingLock($"Forced pause persisted (success={forcedPauseResult.IsSuccess}, fileId={forcedPauseResult.FileId ?? "n/a"}).");
            }

            if (processIncomingRequests &&
                owner is not null &&
                string.Equals(owner.Document.InstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase) &&
                !_appState.EvernotePollingPaused)
            {
                var didHandle = await TryHandleIncomingTakeoverRequestAsync(states);
                if (didHandle)
                {
                    LogPollingLock("Incoming takeover request handled by current lock owner.");
                    return;
                }
            }

            LogPollingLock("Sync completed without incoming takeover action.");
        }
        catch (Exception exception)
        {
            LogPollingLock($"Sync exception: {exception.Message}");
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Distributed polling lock sync failed: {exception.Message}");
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    $"Erreur pendant la synchronisation distribuee du verrou de polling.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                    "Distributed Polling Lock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _distributedPollingSyncInProgress = false;
            UpdateSettingsLockStatus();
        }
    }

    private void SyncPendingTakeoverFieldsFromLocalState(
        IReadOnlyCollection<GoogleDrivePollingStateFile> states,
        GoogleDrivePollingStateFile? owner)
    {
        var localState = states.FirstOrDefault(file =>
            string.Equals(file.Document.InstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase));

        if (localState?.Document.PendingTakeoverRequest?.IsActive == true &&
            string.Equals(localState.Document.PendingTakeoverRequest.RequestedByInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingTakeoverRequestId = localState.Document.PendingTakeoverRequest.RequestId;
            _pendingTakeoverTargetInstanceId = localState.Document.PendingTakeoverRequest.RequestedToInstanceId;
            _pendingTakeoverTargetDisplayName = localState.Document.PendingTakeoverRequest.RequestedToDisplayName;
            _pendingTakeoverObservedInDrive = true;
            _pendingTakeoverMissingObservationCount = 0;
            LogPollingLock($"Local pending request confirmed in Drive: {DescribePending(localState.Document.PendingTakeoverRequest)}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingTakeoverRequestId))
        {
            _pendingTakeoverObservedInDrive = false;
            _pendingTakeoverMissingObservationCount = 0;
            _pendingTakeoverTargetInstanceId = null;
            _pendingTakeoverTargetDisplayName = null;
            _pendingTakeoverRequestedAtUtc = null;
            return;
        }

        if (localState is not null && !localState.Document.PauseAutomaticPolling)
        {
            // Request accepted: requester is now active.
            _pendingTakeoverObservedInDrive = false;
            _pendingTakeoverMissingObservationCount = 0;
            _pendingTakeoverRequestId = null;
            _pendingTakeoverTargetInstanceId = null;
            _pendingTakeoverTargetDisplayName = null;
            _pendingTakeoverRequestedAtUtc = null;
            LogPollingLock("Local pending request cleared: requester became active.");
            return;
        }

        if (localState is not null &&
            localState.Document.PauseAutomaticPolling &&
            localState.Document.PendingTakeoverRequest is null &&
            _pendingTakeoverObservedInDrive)
        {
            // Request was observed previously then disappeared while requester stays paused -> treat as rejection.
            _pendingTakeoverObservedInDrive = false;
            _pendingTakeoverMissingObservationCount = 0;
            _pendingTakeoverRequestId = null;
            _pendingTakeoverTargetInstanceId = null;
            _pendingTakeoverTargetDisplayName = null;
            _pendingTakeoverRequestedAtUtc = null;
            LogPollingLock("Local pending request cleared: requester stayed paused and pending disappeared (rejected).");
            return;
        }

        var localOwnsLock = string.Equals(owner?.Document.InstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase);
        if (localOwnsLock)
        {
            _pendingTakeoverObservedInDrive = false;
            _pendingTakeoverMissingObservationCount = 0;
            _pendingTakeoverRequestId = null;
            _pendingTakeoverTargetInstanceId = null;
            _pendingTakeoverTargetDisplayName = null;
            _pendingTakeoverRequestedAtUtc = null;
            LogPollingLock("Local pending request cleared: local instance owns lock.");
            return;
        }

        // Google Drive list/read can be briefly stale; avoid dropping pending state too quickly.
        _pendingTakeoverMissingObservationCount++;
        if (_pendingTakeoverMissingObservationCount < 3)
        {
            LogPollingLock($"Pending request missing in Drive read; keeping local pending state (miss #{_pendingTakeoverMissingObservationCount}).");
            return;
        }

        _pendingTakeoverMissingObservationCount = 0;
        _pendingTakeoverObservedInDrive = false;
        _pendingTakeoverRequestId = null;
        _pendingTakeoverTargetInstanceId = null;
        _pendingTakeoverTargetDisplayName = null;
        _pendingTakeoverRequestedAtUtc = null;
        LogPollingLock("Local pending request cleared after repeated missing observations.");
    }

    private async Task<List<GoogleDrivePollingStateFile>?> ResolvePollingStatesFromMetasAsync(
        IReadOnlyList<GoogleDrivePollingStateMetaFile> metas,
        string reason)
    {
        var metadataChanged = HasPollingStateMetadataChanged(metas);
        if (!metadataChanged && _cachedPollingStates.Count > 0)
        {
            LogPollingLock($"State load {reason}: metadata unchanged, using cached states ({_cachedPollingStates.Count} file(s)).");
            return _cachedPollingStates
                .Select(state => new GoogleDrivePollingStateFile
                {
                    FileId = state.FileId,
                    FileName = state.FileName,
                    ModifiedTimeUtc = state.ModifiedTimeUtc,
                    Document = ClonePollingStateDocument(state.Document)
                })
                .ToList();
        }

        var fullListResult = await GoogleDriveConfigSyncService.ListPollingStatesAsync(_appState, CancellationToken.None);
        if (!fullListResult.IsSuccess)
        {
            LogPollingLock($"State full list {reason} failed: {fullListResult.Error ?? "unknown error"}");
            return null;
        }

        var loadedStates = fullListResult.States.ToList();
        _cachedPollingStates = loadedStates
            .Select(state => new GoogleDrivePollingStateFile
            {
                FileId = state.FileId,
                FileName = state.FileName,
                ModifiedTimeUtc = state.ModifiedTimeUtc,
                Document = ClonePollingStateDocument(state.Document)
            })
            .ToList();
        ApplyPollingStateMetadataSnapshot(metas);
        LogPollingLock($"State load {reason}: metadata changed, full state reload ({loadedStates.Count} file(s)).");
        return loadedStates;
    }

    private bool HasPollingStateMetadataChanged(IReadOnlyList<GoogleDrivePollingStateMetaFile> metas)
    {
        if (_pollingStateMetaSnapshot.Count != metas.Count)
        {
            return true;
        }

        foreach (var meta in metas)
        {
            if (!_pollingStateMetaSnapshot.TryGetValue(meta.FileId, out var previousModified))
            {
                return true;
            }

            if (previousModified != meta.ModifiedTimeUtc)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyPollingStateMetadataSnapshot(IReadOnlyList<GoogleDrivePollingStateMetaFile> metas)
    {
        _pollingStateMetaSnapshot.Clear();
        foreach (var meta in metas)
        {
            _pollingStateMetaSnapshot[meta.FileId] = meta.ModifiedTimeUtc;
        }
    }

    private void UpdateCachedLocalPollingState(GoogleDrivePollingStateDocument localDocument)
    {
        var cacheIndex = _cachedPollingStates.FindIndex(state =>
            string.Equals(state.Document.InstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase));
        var localStateCopy = new GoogleDrivePollingStateFile
        {
            FileId = _localMachineStateFileId ?? string.Empty,
            FileName = _localMachineStateFileName,
            ModifiedTimeUtc = DateTimeOffset.UtcNow,
            Document = ClonePollingStateDocument(localDocument)
        };

        if (cacheIndex >= 0)
        {
            _cachedPollingStates[cacheIndex] = localStateCopy;
        }
        else
        {
            _cachedPollingStates.Add(localStateCopy);
        }
    }

    private bool ShouldUpsertLocalPollingState(GoogleDrivePollingStateDocument localDocument)
    {
        if (string.IsNullOrWhiteSpace(_localMachineStateFileId))
        {
            return true;
        }

        var signature = BuildLocalPollingStateSignature(localDocument);
        if (!string.Equals(signature, _lastLocalPollingStateSignature, StringComparison.Ordinal))
        {
            return true;
        }

        var heartbeatDueAt = _lastLocalPollingStateWriteUtc.AddSeconds(PollingStateWriteHeartbeatSeconds);
        return DateTimeOffset.UtcNow >= heartbeatDueAt;
    }

    private static string BuildLocalPollingStateSignature(GoogleDrivePollingStateDocument document)
    {
        var pending = document.PendingTakeoverRequest;
        return string.Join("|",
            document.InstanceId,
            document.HostName,
            document.DisplayName,
            document.PauseAutomaticPolling,
            pending?.RequestId ?? string.Empty,
            pending?.RequestedToInstanceId ?? string.Empty,
            pending?.RequestedToDisplayName ?? string.Empty,
            pending?.IsActive ?? false);
    }

    private async Task<bool> TryHandleIncomingTakeoverRequestAsync(IReadOnlyCollection<GoogleDrivePollingStateFile> states)
    {
        var incomingRequest = states
            .Where(state => state.Document.PendingTakeoverRequest?.IsActive == true)
            .Select(state => new { Requester = state, Request = state.Document.PendingTakeoverRequest! })
            .OrderBy(entry => entry.Request.RequestedAtUtc)
            .FirstOrDefault(entry =>
                string.Equals(entry.Request.RequestedToInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase));

        if (incomingRequest is null)
        {
            LogPollingLock("No incoming takeover request for local owner.");
            return false;
        }

        var requesterName = string.IsNullOrWhiteSpace(incomingRequest.Request.RequestedByDisplayName)
            ? incomingRequest.Requester.Document.DisplayName
            : incomingRequest.Request.RequestedByDisplayName;
        LogPollingLock($"Incoming takeover request detected: requestId={incomingRequest.Request.RequestId}, from={requesterName}, to={incomingRequest.Request.RequestedToDisplayName}.");

        var decision = MessageBox.Show(
            this,
            $"{requesterName} demande la main pour activer la synchronisation automatique.{Environment.NewLine}{Environment.NewLine}Confirmer le transfert ?",
            "Demande de transfert de verrou",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        LogPollingLock($"Incoming takeover request decision: {(decision == DialogResult.Yes ? "approved" : "rejected")}.");

        var updatedRequesterDocument = ClonePollingStateDocument(incomingRequest.Requester.Document);
        updatedRequesterDocument.PendingTakeoverRequest = null;

        if (decision == DialogResult.Yes)
        {
            _appState.EvernotePollingPaused = true;
            ApplyEvernotePollingSettings();
            SaveAppStateNow(queueGoogleDriveSync: false);

            var nowUtc = DateTimeOffset.UtcNow;
            updatedRequesterDocument.PauseAutomaticPolling = false;
            updatedRequesterDocument.UpdatedAtUtc = nowUtc;
            var requesterApproved = await UpsertAndVerifyRequesterStateAsync(
                incomingRequest.Requester,
                updatedRequesterDocument,
                expectedPauseAutomaticPolling: false,
                operationLabel: "approval");
            if (!requesterApproved)
            {
                LogPollingLock("Requester update after approval could not be verified on Drive.");
                return true;
            }

            var updatedLocalDocument = BuildLocalPollingStateDocument();
            updatedLocalDocument.PendingTakeoverRequest = null;
            updatedLocalDocument.PauseAutomaticPolling = true;
            updatedLocalDocument.UpdatedAtUtc = nowUtc;
            var localUpsert = await GoogleDriveConfigSyncService.UpsertPollingStateAsync(
                _appState,
                _localMachineStateFileName,
                updatedLocalDocument,
                _localMachineStateFileId,
                CancellationToken.None);
            LogPollingLock(
                $"Owner state updated after approval: {_localMachineStateFileName} " +
                $"(success={localUpsert.IsSuccess}, fileId={localUpsert.FileId ?? "n/a"}).");

            return true;
        }

        updatedRequesterDocument.PauseAutomaticPolling = true;
        updatedRequesterDocument.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await UpsertAndVerifyRequesterStateAsync(
            incomingRequest.Requester,
            updatedRequesterDocument,
            expectedPauseAutomaticPolling: true,
            operationLabel: "rejection");

        return true;
    }

    private async Task<bool> UpsertAndVerifyRequesterStateAsync(
        GoogleDrivePollingStateFile requesterState,
        GoogleDrivePollingStateDocument updatedRequesterDocument,
        bool expectedPauseAutomaticPolling,
        string operationLabel)
    {
        var preferredFileId = requesterState.FileId;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var upsert = await GoogleDriveConfigSyncService.UpsertPollingStateAsync(
                _appState,
                requesterState.FileName,
                updatedRequesterDocument,
                preferredFileId,
                CancellationToken.None);
            if (upsert.IsSuccess && !string.IsNullOrWhiteSpace(upsert.FileId))
            {
                preferredFileId = upsert.FileId;
            }

            LogPollingLock(
                $"Requester upsert ({operationLabel}) attempt {attempt}/3: " +
                $"success={upsert.IsSuccess}, fileId={upsert.FileId ?? preferredFileId ?? "n/a"}, error={upsert.Error ?? "none"}.");
            if (!upsert.IsSuccess)
            {
                await Task.Delay(250);
                continue;
            }

            var verificationList = await GoogleDriveConfigSyncService.ListPollingStatesAsync(_appState, CancellationToken.None);
            if (!verificationList.IsSuccess)
            {
                LogPollingLock($"Requester verify list failed on attempt {attempt}/3: {verificationList.Error ?? "unknown error"}.");
                await Task.Delay(250);
                continue;
            }

            var fileIdForLookup = preferredFileId ?? requesterState.FileId;
            var reloadedRequester = verificationList.States.FirstOrDefault(state =>
                string.Equals(state.FileId, fileIdForLookup, StringComparison.OrdinalIgnoreCase));
            if (reloadedRequester is null)
            {
                LogPollingLock($"Requester verify attempt {attempt}/3: file not found by id={fileIdForLookup ?? "n/a"}.");
                await Task.Delay(250);
                continue;
            }

            var pendingIsCleared = reloadedRequester.Document.PendingTakeoverRequest is null;
            var pauseMatches = reloadedRequester.Document.PauseAutomaticPolling == expectedPauseAutomaticPolling;
            LogPollingLock(
                $"Requester verify attempt {attempt}/3: pendingCleared={pendingIsCleared}, " +
                $"pause={reloadedRequester.Document.PauseAutomaticPolling}, expectedPause={expectedPauseAutomaticPolling}.");
            if (pendingIsCleared && pauseMatches)
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private GoogleDrivePollingStateFile? DeterminePollingLockOwner(IEnumerable<GoogleDrivePollingStateFile> states)
    {
        var now = DateTimeOffset.UtcNow;
        return states
            .Where(state => !state.Document.PauseAutomaticPolling)
            .Where(state =>
            {
                var age = now - state.Document.UpdatedAtUtc;
                return age <= PollingLockOwnerStaleThreshold;
            })
            .OrderByDescending(state => state.Document.UpdatedAtUtc)
            .ThenBy(state => state.Document.InstanceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string ResolveLocalStateFileName(IReadOnlyCollection<GoogleDrivePollingStateFile> states)
    {
        var hasAnotherHostState = states.Any(state =>
            string.Equals(state.Document.HostName, _localMachineHostName, StringComparison.OrdinalIgnoreCase));

        return hasAnotherHostState
            ? _localMachineSuffixedStateFileName
            : _localMachineDefaultStateFileName;
    }

    private GoogleDrivePollingStateDocument BuildLocalPollingStateDocument()
    {
        var pendingRequest = BuildPendingTakeoverRequest();
        return new GoogleDrivePollingStateDocument
        {
            InstanceId = _localMachineInstanceId,
            HostName = _localMachineHostName,
            DisplayName = GetLocalDisplayName(),
            PauseAutomaticPolling = _appState.EvernotePollingPaused,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            PendingTakeoverRequest = pendingRequest
        };
    }

    private GoogleDrivePollingTakeoverRequest? BuildPendingTakeoverRequest()
    {
        if (string.IsNullOrWhiteSpace(_pendingTakeoverRequestId) ||
            string.IsNullOrWhiteSpace(_pendingTakeoverTargetInstanceId) ||
            string.IsNullOrWhiteSpace(_pendingTakeoverTargetDisplayName))
        {
            return null;
        }

        _pendingTakeoverRequestedAtUtc ??= DateTimeOffset.UtcNow;

        return new GoogleDrivePollingTakeoverRequest
        {
            RequestId = _pendingTakeoverRequestId,
            RequestedByInstanceId = _localMachineInstanceId,
            RequestedByDisplayName = GetLocalDisplayName(),
            RequestedToInstanceId = _pendingTakeoverTargetInstanceId,
            RequestedToDisplayName = _pendingTakeoverTargetDisplayName,
            RequestedAtUtc = _pendingTakeoverRequestedAtUtc.Value,
            IsActive = true
        };
    }

    private static GoogleDrivePollingStateDocument ClonePollingStateDocument(GoogleDrivePollingStateDocument source)
    {
        return new GoogleDrivePollingStateDocument
        {
            InstanceId = source.InstanceId,
            HostName = source.HostName,
            DisplayName = source.DisplayName,
            PauseAutomaticPolling = source.PauseAutomaticPolling,
            UpdatedAtUtc = source.UpdatedAtUtc,
            PendingTakeoverRequest = source.PendingTakeoverRequest is null
                ? null
                : new GoogleDrivePollingTakeoverRequest
                {
                    RequestId = source.PendingTakeoverRequest.RequestId,
                    RequestedByInstanceId = source.PendingTakeoverRequest.RequestedByInstanceId,
                    RequestedByDisplayName = source.PendingTakeoverRequest.RequestedByDisplayName,
                    RequestedToInstanceId = source.PendingTakeoverRequest.RequestedToInstanceId,
                    RequestedToDisplayName = source.PendingTakeoverRequest.RequestedToDisplayName,
                    RequestedAtUtc = source.PendingTakeoverRequest.RequestedAtUtc,
                    IsActive = source.PendingTakeoverRequest.IsActive
                }
        };
    }

    private static string NormalizeHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-host";
        }

        var normalized = value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var invalidChar in invalidChars)
        {
            normalized = normalized.Replace(invalidChar, '_');
        }

        return normalized;
    }

    private static string BuildStateFileName(string hostName, string? suffix)
    {
        var sanitizedHost = NormalizeHostName(hostName);
        var sanitizedSuffix = string.IsNullOrWhiteSpace(suffix)
            ? null
            : suffix.Trim();

        return string.IsNullOrWhiteSpace(sanitizedSuffix)
            ? $"{AppConfig.GoogleDrivePollingStateFilePrefix}{sanitizedHost}.json"
            : $"{AppConfig.GoogleDrivePollingStateFilePrefix}{sanitizedHost}_{sanitizedSuffix}.json";
    }

    private static string BuildStableInstanceSuffix()
    {
        var source = Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant();
        var hashBytes = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hashBytes).Substring(0, 6).ToLowerInvariant();
    }

    private string GetLocalDisplayName()
    {
        return string.Equals(_localMachineStateFileName, _localMachineDefaultStateFileName, StringComparison.OrdinalIgnoreCase)
            ? _localMachineHostName
            : $"{_localMachineHostName}_{_localMachineInstanceSuffix}";
    }

    private void UpdateSettingsLockStatus()
    {
        if (_settingsFormOpenInstance is null || _settingsFormOpenInstance.IsDisposed)
        {
            _settingsFormOpenInstance = null;
            return;
        }

        var owner = string.IsNullOrWhiteSpace(_lockOwnerDisplayName) ? "(none)" : _lockOwnerDisplayName;
        var isLocalOwner = string.Equals(_lockOwnerInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase);
        _settingsFormOpenInstance.UpdatePollingLockStatus(owner, isLocalOwner);
        _settingsFormOpenInstance.UpdatePendingPollingRequest(_pendingTakeoverTargetDisplayName);
        _settingsFormOpenInstance.ConfirmPausePollingState(_appState.EvernotePollingPaused);
        _settingsFormOpenInstance.SetPausePollingBusy(_settingsPauseChangeInProgress || _distributedPollingSyncInProgress || _settingsForceLockInProgress);
        _settingsFormOpenInstance.SetForceLockBusy(_settingsForceLockInProgress || _distributedPollingSyncInProgress);
        var intervalSeconds = Math.Max(1, _pollingLockSyncTimer.Interval / 1000);
        var secondsUntilNextPoll = (int)Math.Ceiling((_nextPollingStateSyncAtUtc - DateTimeOffset.UtcNow).TotalSeconds);
        _settingsFormOpenInstance.UpdateNextStatePollInfo(intervalSeconds, secondsUntilNextPoll);
    }

    private async Task HandlePausePollingToggleFromSettingsAsync(bool isPaused)
    {
        if (_settingsPauseChangeInProgress)
        {
            LogPollingLock("Pause toggle ignored: previous pause change still in progress.");
            return;
        }

        _settingsPauseChangeInProgress = true;
        UpdateSettingsLockStatus();
        try
        {
            await SyncDistributedPollingStateAsync(showErrors: false, processIncomingRequests: false);

            if (isPaused)
            {
                LogPollingLock("Settings toggle: user paused polling locally.");
                _appState.EvernotePollingPaused = true;
                _pendingTakeoverRequestId = null;
                _pendingTakeoverTargetInstanceId = null;
                _pendingTakeoverTargetDisplayName = null;
                _pendingTakeoverRequestedAtUtc = null;
                _pendingTakeoverObservedInDrive = false;
                _pendingTakeoverMissingObservationCount = 0;
            }
            else
            {
                LogPollingLock("Settings toggle: user requested to resume polling.");
                var localOwnsLock = string.Equals(_lockOwnerInstanceId, _localMachineInstanceId, StringComparison.OrdinalIgnoreCase);
                var noCurrentOwner = string.IsNullOrWhiteSpace(_lockOwnerInstanceId);
                if (localOwnsLock || noCurrentOwner)
                {
                    LogPollingLock($"Resume accepted immediately (localOwnsLock={localOwnsLock}, noCurrentOwner={noCurrentOwner}).");
                    _appState.EvernotePollingPaused = false;
                    _pendingTakeoverRequestId = null;
                    _pendingTakeoverTargetInstanceId = null;
                    _pendingTakeoverTargetDisplayName = null;
                    _pendingTakeoverRequestedAtUtc = null;
                    _pendingTakeoverObservedInDrive = false;
                    _pendingTakeoverMissingObservationCount = 0;
                }
                else
                {
                    _appState.EvernotePollingPaused = true;
                    _pendingTakeoverRequestId = Guid.NewGuid().ToString("N");
                    _pendingTakeoverTargetInstanceId = _lockOwnerInstanceId;
                    _pendingTakeoverTargetDisplayName = _lockOwnerDisplayName;
                    _pendingTakeoverRequestedAtUtc = DateTimeOffset.UtcNow;
                    _pendingTakeoverObservedInDrive = false;
                    _pendingTakeoverMissingObservationCount = 0;
                    LogPollingLock($"Resume requires takeover request: {DescribePendingLocalRequest()}.");
                }
            }

            ApplyEvernotePollingSettings();
            SaveAppStateNow(queueGoogleDriveSync: false);
            await SyncDistributedPollingStateAsync(showErrors: true, processIncomingRequests: false);
            _ = SyncConfigToGoogleDriveAsync(showErrors: false);
            LogPollingLock($"Pause toggle flow completed: paused={_appState.EvernotePollingPaused}, pending={DescribePendingLocalRequest()}.");
        }
        finally
        {
            _settingsPauseChangeInProgress = false;
            UpdateSettingsLockStatus();
        }
    }

    private async Task HandleForcePollingLockFromSettingsAsync()
    {
        if (_settingsForceLockInProgress)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Cette action supprime tous les fichiers state dans Drive puis force ce poste comme lock owner actif. Continuer ?",
                "Force lock",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _settingsForceLockInProgress = true;
        UpdateSettingsLockStatus();
        try
        {
            if (!GoogleDriveConfigSyncService.IsConfigured(_appState))
            {
                _pendingTakeoverRequestId = null;
                _pendingTakeoverTargetInstanceId = null;
                _pendingTakeoverTargetDisplayName = null;
                _pendingTakeoverRequestedAtUtc = null;
                _pendingTakeoverObservedInDrive = false;
                _pendingTakeoverMissingObservationCount = 0;
                _appState.EvernotePollingPaused = false;
                ApplyEvernotePollingSettings();
                SaveAppStateNow(queueGoogleDriveSync: false);
                _lockOwnerInstanceId = _localMachineInstanceId;
                _lockOwnerDisplayName = GetLocalDisplayName();
                return;
            }

            var deleteResult = await GoogleDriveConfigSyncService.DeleteAllPollingStatesAsync(_appState, CancellationToken.None);
            LogPollingLock($"Force lock delete states: success={deleteResult.IsSuccess}, deleted={deleteResult.DeletedFiles}, error={deleteResult.Error ?? "none"}.");
            if (!deleteResult.IsSuccess)
            {
                MessageBox.Show(
                    this,
                    $"Impossible de forcer le lock (suppression Drive).{Environment.NewLine}{Environment.NewLine}{deleteResult.Error}",
                    "Force lock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _pollingStateMetaSnapshot.Clear();
            _cachedPollingStates = [];
            _localMachineStateFileId = null;
            _localMachineStateFileName = _localMachineDefaultStateFileName;

            _pendingTakeoverRequestId = null;
            _pendingTakeoverTargetInstanceId = null;
            _pendingTakeoverTargetDisplayName = null;
            _pendingTakeoverRequestedAtUtc = null;
            _pendingTakeoverObservedInDrive = false;
            _pendingTakeoverMissingObservationCount = 0;
            _appState.EvernotePollingPaused = false;
            ApplyEvernotePollingSettings();
            SaveAppStateNow(queueGoogleDriveSync: false);

            var localDocument = BuildLocalPollingStateDocument();
            localDocument.PendingTakeoverRequest = null;
            localDocument.PauseAutomaticPolling = false;
            localDocument.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var upsert = await GoogleDriveConfigSyncService.UpsertPollingStateAsync(
                _appState,
                _localMachineStateFileName,
                localDocument,
                _localMachineStateFileId,
                CancellationToken.None);
            LogPollingLock($"Force lock local upsert: success={upsert.IsSuccess}, fileId={upsert.FileId ?? "n/a"}, error={upsert.Error ?? "none"}.");
            if (!upsert.IsSuccess)
            {
                MessageBox.Show(
                    this,
                    $"Impossible de finaliser le force lock.{Environment.NewLine}{Environment.NewLine}{upsert.Error}",
                    "Force lock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _localMachineStateFileId = upsert.FileId;
            _lockOwnerInstanceId = _localMachineInstanceId;
            _lockOwnerDisplayName = GetLocalDisplayName();
            await SyncDistributedPollingStateAsync(showErrors: false, processIncomingRequests: false);
        }
        finally
        {
            _settingsForceLockInProgress = false;
            UpdateSettingsLockStatus();
        }
    }

    private void LogPollingLock(string message)
    {
        var line = $"[PollingLock:{_localMachineInstanceId}] {message}";
        AppLogger.Debug(line);
    }

    private string DescribePendingLocalRequest()
    {
        if (string.IsNullOrWhiteSpace(_pendingTakeoverRequestId))
        {
            return "none";
        }

        return $"id={_pendingTakeoverRequestId},target={_pendingTakeoverTargetDisplayName ?? _pendingTakeoverTargetInstanceId ?? "?"}";
    }

    private static string DescribePending(GoogleDrivePollingTakeoverRequest? request)
    {
        if (request is null || !request.IsActive)
        {
            return "none";
        }

        return $"id={request.RequestId},from={request.RequestedByDisplayName},to={request.RequestedToDisplayName},active={request.IsActive}";
    }

    private void ResetEvernoteTrackingBaseline()
    {
        _appState.EvernoteSnapshotInitialized = false;
        _appState.EvernoteStackNoteSnapshots = [];
        _appState.EvernoteNotebookNoteSnapshots = [];
        _lastEvernoteAutoExportUtc = DateTime.MinValue;
    }

    private void PollEvernoteTracking(bool allowAutoExport, bool showErrors, bool ignorePause)
    {
        if (_evernotePollingInProgress)
        {
            return;
        }

        if (!ignorePause && _appState.EvernotePollingPaused)
        {
            return;
        }

        var rootPath = GetConfiguredEvernoteRootPath();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        _evernotePollingInProgress = true;
        RefreshTrayPollingIconState();
        try
        {
            var previousNotebookMap = _appState.GetEvernoteNotebookSnapshotMap();
            var hasPreviousSnapshot = _appState.EvernoteSnapshotInitialized;

            EvernoteTrackingSnapshot trackingSnapshot;
            string dbPath;
            try
            {
                trackingSnapshot = EvernoteLocalDbService.GetTrackingSnapshot(rootPath, out dbPath);
            }
            catch (Exception exception)
            {
                if (showErrors)
                {
                    MessageBox.Show(
                        this,
                        $"Polling Evernote impossible.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                        "Evernote Export",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Evernote polling failed: {exception.Message}");
                return;
            }

            _appState.SetEvernoteTrackingSnapshot(trackingSnapshot.StackSnapshots, trackingSnapshot.NotebookSnapshots);
            QueueAppStateSave();

            if (!hasPreviousSnapshot || !allowAutoExport)
            {
                return;
            }

            var monitoredNotebooks = GetSelectedNotebooksForExport();
            if (monitoredNotebooks.Count == 0)
            {
                return;
            }

            var monitoredNotebookIds = monitoredNotebooks
                .Select(notebook => notebook.NotebookId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentNotebookMap = BuildNotebookSnapshotMap(trackingSnapshot.NotebookSnapshots);
            var changeSummary = AnalyzeChangedNotes(monitoredNotebookIds, previousNotebookMap, currentNotebookMap);
            if (changeSummary.NoteChangeCount >= 1)
            {
                var targetExportFileNames = monitoredNotebooks
                    .Where(notebook => changeSummary.ChangedNotebookIds.Contains(notebook.NotebookId))
                    .Select(notebook => notebook.ExportFileBaseName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (targetExportFileNames.Length == 0)
                {
                    return;
                }

                var cooldown = TimeSpan.FromSeconds(EvernoteAutoExportCooldownSeconds);
                var nowUtc = DateTime.UtcNow;
                if (_lastEvernoteAutoExportUtc != DateTime.MinValue)
                {
                    var elapsed = nowUtc - _lastEvernoteAutoExportUtc;
                    if (elapsed < cooldown)
                    {
                        var remainingSeconds = Math.Max(1, (int)Math.Ceiling((cooldown - elapsed).TotalSeconds));
                        SetEvernoteStatus(
                            $"{changeSummary.NoteChangeCount} note change(s) detected in {dbPath}. Auto export cooldown active ({remainingSeconds}s remaining).");
                        return;
                    }
                }

                _lastEvernoteAutoExportUtc = nowUtc;
                SetEvernoteStatus(
                    $"{changeSummary.NoteChangeCount} note change(s) detected in {dbPath}. Auto export running for {string.Join(", ", targetExportFileNames)}...");
                _ = ExportSelectedEvernoteContentToMarkdownAsync(
                    showDialogs: false,
                    source: "auto",
                    targetExportFileNames: targetExportFileNames);
            }
        }
        finally
        {
            _evernotePollingInProgress = false;
            RefreshTrayPollingIconState();
        }
    }

    private static Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> BuildNotebookSnapshotMap(
        IEnumerable<EvernoteContainerNoteSnapshotState> containers)
    {
        var map = new Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var notebookId = container.ContainerId ?? string.Empty;
            if (!map.TryGetValue(notebookId, out var noteMap))
            {
                noteMap = new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
                map[notebookId] = noteMap;
            }

            foreach (var note in container.Notes ?? [])
            {
                if (string.IsNullOrWhiteSpace(note.NoteId))
                {
                    continue;
                }

                noteMap[note.NoteId] = note;
            }
        }

        return map;
    }

    private static EvernoteChangeSummary AnalyzeChangedNotes(
        IReadOnlyCollection<string> monitoredNotebookIds,
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> previousMap,
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> currentMap)
    {
        var changeCount = 0;
        var changedNotebookIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var notebookId in monitoredNotebookIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            previousMap.TryGetValue(notebookId, out var previousNotes);
            currentMap.TryGetValue(notebookId, out var currentNotes);

            previousNotes ??= new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
            currentNotes ??= new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);

            var noteIds = new HashSet<string>(previousNotes.Keys, StringComparer.OrdinalIgnoreCase);
            noteIds.UnionWith(currentNotes.Keys);

            foreach (var noteId in noteIds)
            {
                var existedBefore = previousNotes.TryGetValue(noteId, out var oldNote);
                var existsNow = currentNotes.TryGetValue(noteId, out var newNote);

                if (!existedBefore || !existsNow)
                {
                    changeCount++;
                    changedNotebookIds.Add(notebookId);
                    continue;
                }

                if (oldNote?.CreatedMs != newNote?.CreatedMs || oldNote?.UpdatedMs != newNote?.UpdatedMs)
                {
                    changeCount++;
                    changedNotebookIds.Add(notebookId);
                }
            }
        }

        return new EvernoteChangeSummary(changeCount, changedNotebookIds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _traySyncAnimationTimer.Dispose();
            _settingsUiRefreshTimer.Dispose();
            _pollingLockSyncTimer.Dispose();
            _googleDriveSyncTimer.Dispose();
            _evernotePollingTimer.Dispose();
            _topBarVisibilityTimer.Dispose();
            _windowStateSaveTimer.Dispose();
            _evernoteTreeNodeMenu.Dispose();
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

