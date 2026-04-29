namespace WinGeminiWrapper;

internal sealed partial class MainForm
{
    private void QueueAppStateSave()
    {
        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    private void WindowStateSaveTimer_Tick(object? sender, EventArgs e)
    {
        _windowStateSaveTimer.Stop();
        PersistAppStateLocally(queueGoogleDriveSync: true);
    }

    private void SaveAppStateNow(bool queueGoogleDriveSync = true)
    {
        _windowStateSaveTimer.Stop();
        PersistAppStateLocally(queueGoogleDriveSync);
    }

    private void PersistAppStateLocally(bool queueGoogleDriveSync)
    {
        AppStateStore.Save(_appState);
        if (queueGoogleDriveSync)
        {
            QueueGoogleDriveSync();
        }
    }

    private void QueueGoogleDriveSync()
    {
        if (_suspendGoogleDriveSyncQueue || !_googleDriveSyncService.IsConfigured(_appState))
        {
            return;
        }

        _googleDriveSyncTimer.Stop();
        _googleDriveSyncTimer.Start();
    }

    private async void GoogleDriveSyncTimer_Tick(object? sender, EventArgs e)
    {
        _googleDriveSyncTimer.Stop();
        await SyncConfigToGoogleDriveAsync(showErrors: false);
    }

    private async Task SyncConfigToGoogleDriveAsync(bool showErrors)
    {
        if (_googleDriveSyncInProgress || !_googleDriveSyncService.IsConfigured(_appState))
        {
            return;
        }

        _googleDriveSyncInProgress = true;
        try
        {
            var syncState = CreateSharedSyncState(_appState);
            var json = AppStateStore.Serialize(syncState);
            var uploadResult = await _googleDriveSyncService.UploadConfigAsync(
                _appState,
                json,
                CancellationToken.None);

            if (!uploadResult.IsSuccess)
            {
                var error = uploadResult.Error ?? "Unknown Google Drive sync error.";
                AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive config sync failed: {error}");
                if (showErrors)
                {
                    MessageBox.Show(
                        this,
                        $"Impossible de sauvegarder la configuration sur Google Drive.{Environment.NewLine}{Environment.NewLine}{error}",
                        "Google Drive Sync",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return;
            }

            var fileId = uploadResult.FileId;
            if (!string.IsNullOrWhiteSpace(fileId) &&
                !string.Equals(_appState.GoogleDriveConfigFileId, fileId, StringComparison.Ordinal))
            {
                _appState.GoogleDriveConfigFileId = fileId;
                AppStateStore.Save(_appState);
            }

            var modifiedLabel = uploadResult.ModifiedTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a";
            AppLogger.Debug($"[{DateTime.Now:HH:mm:ss}] Google Drive config synced (file: {fileId ?? "new"}, modified: {modifiedLabel}).");
        }
        finally
        {
            _googleDriveSyncInProgress = false;
        }
    }

    private async Task LogoutAsync()
    {
        if (_logoutInProgress)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "This will sign you out of Google in this wrapper and clear saved session data. Continue?",
                "Log out",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _logoutInProgress = true;
        UseWaitCursor = true;

        try
        {
            await EnsureWebViewInitializedAsync(WrappedApp.Gemini);
            await EnsureWebViewInitializedAsync(WrappedApp.NotebookLm);
            await EnsureWebViewInitializedAsync(WrappedApp.GoogleDrive);

            var webViews = _webViews.Values.ToArray();
            foreach (var webView in webViews)
            {
                var core = webView.CoreWebView2;
                if (core is null)
                {
                    continue;
                }

                core.CookieManager.DeleteAllCookies();
                await core.Profile.ClearBrowsingDataAsync();
            }

            _appState.SetLastUrl(WrappedApp.Gemini, null);
            _appState.SetLastUrl(WrappedApp.NotebookLm, null);
            _appState.SetLastUrl(WrappedApp.GoogleDrive, null);
            SaveAppStateNow();

            foreach (var app in _webViews.Keys.ToArray())
            {
                var webView = _webViews[app];
                webView.CoreWebView2?.Navigate(AppConfig.GetAppUrl(app));
            }

            SwitchApp(AppConfig.DefaultApp, restoreFromTray: false);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Could not complete logout.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Log out failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _logoutInProgress = false;
        }
    }
}
