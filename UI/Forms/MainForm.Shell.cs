namespace WinGemini;

internal sealed partial class MainForm
{
    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ToggleWindowVisibilityFromTray();
    }

    private void ToggleWindowVisibilityFromTray()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            HideToTray();
            return;
        }

        RestoreFromTray();
    }

    private void UpdateTrayMenuItems()
    {
        _switchAppMenuItem.Text = UiLanguageService.T("Main.Tray.SwitchTo");
        _switchAppMenuItem.DropDownItems.Clear();
        foreach (var app in Enum.GetValues<WrappedApp>())
        {
            if (app == _currentApp)
            {
                continue;
            }

            var targetApp = app;
            _switchAppMenuItem.DropDownItems.Add(
                AppConfig.GetAppDisplayName(targetApp),
                null,
                (_, _) => SwitchApp(targetApp, restoreFromTray: true));
        }

        _switchAppMenuItem.Enabled = _switchAppMenuItem.DropDownItems.Count > 0;
    }

    private void ShowActiveContent()
    {
        var showingEvernoteExport = _currentApp == WrappedApp.EvernoteExport;
        _evernoteExportPanel.Visible = showingEvernoteExport;
        if (showingEvernoteExport)
        {
            LoadEvernoteTreeFromConfiguredRoot(showErrors: false);
            _evernoteExportPanel.BringToFront();
        }

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
        Text = AppVersionProvider.FormatWindowTitle(appName);
        _trayIcon.Text = appName;
        ApplyLocalizedChromeText();
        ApplyLocalizedEvernoteExportText();
        UpdateTrayMenuItems();
    }

    private void ApplyLocalizedChromeText()
    {
        foreach (var app in Enum.GetValues<WrappedApp>())
        {
            if ((int)app >= 0 && (int)app < _appSwitcher.Items.Count)
            {
                _appSwitcher.Items[(int)app] = AppConfig.GetAppDisplayName(app);
            }
        }

        _appTopBarLabel.Text = UiLanguageService.T("Main.TopBar.App");
        _refreshTopBarButton.Text = UiLanguageService.T("Main.TopBar.Refresh");
        _settingsTopBarButton.Text = UiLanguageService.T("Common.Settings");
        _logoutTopBarButton.Text = UiLanguageService.T("Main.TopBar.LogOut");
        _refreshTrayMenuItem.Text = UiLanguageService.T("Main.TopBar.Refresh");
        _settingsTrayMenuItem.Text = UiLanguageService.T("Common.Settings");
        _logoutTrayMenuItem.Text = UiLanguageService.T("Main.TopBar.LogOut");
        _exitTrayMenuItem.Text = UiLanguageService.T("Main.Tray.Exit");
    }

    private void RefreshTrayPollingIconState(bool? pausedOverride = null)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var isPollingPaused = pausedOverride ?? _appState.EvernotePollingPaused;
        if (_traySyncAnimationTimer is null)
        {
            _trayIcon.Icon = isPollingPaused
                ? AppIconProvider.GetTrayIdleIcon()
                : AppIconProvider.GetTrayActiveIcon();
            return;
        }

        var isEvernoteWorkInProgress = !isPollingPaused && (_evernotePollingInProgress || _evernoteExportInProgress);
        if (isEvernoteWorkInProgress)
        {
            if (!_traySyncAnimationTimer.Enabled)
            {
                _traySyncAnimationFrameIndex = 0;
                _traySyncAnimationTimer.Start();
            }

            SetTraySyncAnimationFrame(_traySyncAnimationFrameIndex);
            return;
        }

        _traySyncAnimationTimer.Stop();
        _traySyncAnimationFrameIndex = 0;
        _trayIcon.Icon = isPollingPaused
            ? AppIconProvider.GetTrayIdleIcon()
            : AppIconProvider.GetTrayActiveIcon();
    }

    private void TraySyncAnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_traySyncAnimationTimer is null)
        {
            return;
        }

        var isEvernoteWorkInProgress = !_appState.EvernotePollingPaused && (_evernotePollingInProgress || _evernoteExportInProgress);
        if (!isEvernoteWorkInProgress)
        {
            RefreshTrayPollingIconState();
            return;
        }

        _traySyncAnimationFrameIndex++;
        SetTraySyncAnimationFrame(_traySyncAnimationFrameIndex);
    }

    private void SetTraySyncAnimationFrame(int frameIndex)
    {
        var frames = AppIconProvider.GetTraySpinIcons();
        if (frames.Count == 0)
        {
            _trayIcon.Icon = AppIconProvider.GetTrayActiveIcon();
            return;
        }

        var normalizedIndex = Math.Abs(frameIndex % frames.Count);
        _trayIcon.Icon = frames[normalizedIndex];
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
            UiLanguageService.Tf("Main.Tray.Balloon.Title", appName),
            UiLanguageService.T("Main.Tray.Balloon.Text"),
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
}

