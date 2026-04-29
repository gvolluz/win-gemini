namespace WinGemini;

internal sealed partial class MainForm
{
    private async void OpenSettings()
    {
        if (_settingsFormOpenInstance is not null && !_settingsFormOpenInstance.IsDisposed)
        {
            _settingsFormOpenInstance.Activate();
            _settingsFormOpenInstance.BringToFront();
            return;
        }

        var previousUiLanguageCode = _appState.UiLanguageCode;
        using var settingsForm = new SettingsForm(
            _appState.UiLanguageCode,
            _appState.CloseButtonBehavior,
            _appState.EnableDebugLogs,
            _appState.GoogleDriveSyncEnabled,
            _appState.GoogleDriveAutoRestoreOnStartup,
            _appState.GoogleDriveClientId,
            _appState.GoogleDriveClientSecret,
            _appState.GoogleDriveConfigFileId,
            _googleDriveSyncService);
        _settingsFormOpenInstance = settingsForm;
        settingsForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsFormOpenInstance, settingsForm))
            {
                _settingsFormOpenInstance = null;
            }
        };
        settingsForm.ExportSettingsRequested += () => ExportSettingsWithDialog(settingsForm);
        settingsForm.UiLanguagePreviewChanged += previewLanguageCode =>
        {
            UiLanguageService.Apply(previewLanguageCode);
            UpdateAppChrome();
            ApplyLocalizedEvernoteExportText();
            UpdateEvernoteNodeMenuState();
            UpdateEvernotePollingUiState();
        };
        settingsForm.ImportSettingsRequested += () =>
        {
            if (!ImportSettingsWithDialog(settingsForm))
            {
                return;
            }

            settingsForm.DialogResult = DialogResult.Cancel;
            settingsForm.Close();
        };
        if (settingsForm.ShowDialog(this) != DialogResult.OK)
        {
            UiLanguageService.Apply(previousUiLanguageCode);
            UpdateAppChrome();
            ApplyLocalizedEvernoteExportText();
            UpdateEvernoteNodeMenuState();
            UpdateEvernotePollingUiState();
            _settingsFormOpenInstance = null;
            return;
        }

        var stateChanged = false;
        var uiLanguageChanged = false;

        if (settingsForm.SelectedCloseButtonBehavior != _appState.CloseButtonBehavior)
        {
            _appState.CloseButtonBehavior = settingsForm.SelectedCloseButtonBehavior;
            stateChanged = true;
        }

        if (settingsForm.IsGoogleDriveSyncEnabled != _appState.GoogleDriveSyncEnabled)
        {
            _appState.GoogleDriveSyncEnabled = settingsForm.IsGoogleDriveSyncEnabled;
            stateChanged = true;
        }

        if (settingsForm.IsGoogleDriveAutoRestoreOnStartup != _appState.GoogleDriveAutoRestoreOnStartup)
        {
            _appState.GoogleDriveAutoRestoreOnStartup = settingsForm.IsGoogleDriveAutoRestoreOnStartup;
            stateChanged = true;
        }

        if (settingsForm.IsDebugLoggingEnabled != _appState.EnableDebugLogs)
        {
            _appState.EnableDebugLogs = settingsForm.IsDebugLoggingEnabled;
            AppLogger.SetDebugLoggingEnabled(_appState.EnableDebugLogs);
            stateChanged = true;
        }

        if (!string.Equals(settingsForm.SelectedUiLanguageCode, _appState.UiLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            _appState.UiLanguageCode = settingsForm.SelectedUiLanguageCode;
            UiLanguageService.Apply(_appState.UiLanguageCode);
            stateChanged = true;
            uiLanguageChanged = true;
        }

        if (!string.Equals(settingsForm.GoogleDriveClientId, _appState.GoogleDriveClientId, StringComparison.Ordinal))
        {
            _appState.GoogleDriveClientId = settingsForm.GoogleDriveClientId;
            stateChanged = true;
        }

        if (!string.Equals(settingsForm.GoogleDriveClientSecret, _appState.GoogleDriveClientSecret, StringComparison.Ordinal))
        {
            _appState.GoogleDriveClientSecret = settingsForm.GoogleDriveClientSecret;
            stateChanged = true;
        }

        if (!string.Equals(settingsForm.GoogleDriveConfigFileId, _appState.GoogleDriveConfigFileId, StringComparison.Ordinal))
        {
            _appState.GoogleDriveConfigFileId = settingsForm.GoogleDriveConfigFileId;
            stateChanged = true;
        }

        if (!stateChanged)
        {
            RefreshTrayPollingIconState();
            _settingsFormOpenInstance = null;
            return;
        }

        if (uiLanguageChanged)
        {
            UpdateAppChrome();
            UpdateEvernoteNodeMenuState();
        }

        SaveAppStateNow(queueGoogleDriveSync: false);
        await SyncDistributedPollingStateAsync(showErrors: true, processIncomingRequests: false);
        _ = SyncConfigToGoogleDriveAsync(showErrors: true);
        _settingsFormOpenInstance = null;
    }

    private void ExportSettingsWithDialog(IWin32Window owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = UiLanguageService.T("Settings.ExportSettings"),
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"WinGemini-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = "json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            var json = AppStateStore.Serialize(_appState);
            File.WriteAllText(dialog.FileName, json);
            MessageBox.Show(
                owner,
                UiLanguageService.Tf("Settings.ExportedTo", Environment.NewLine, dialog.FileName),
                UiLanguageService.T("Settings.ExportSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                UiLanguageService.Tf("Settings.UnableToExport", exception.Message),
                UiLanguageService.T("Settings.ExportSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool ImportSettingsWithDialog(IWin32Window owner)
    {
        if (MessageBox.Show(
                owner,
                UiLanguageService.T("Settings.ImportWillReplaceConfirm"),
                UiLanguageService.T("Settings.ImportSettings"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return false;
        }

        using var dialog = new OpenFileDialog
        {
            Title = UiLanguageService.T("Settings.ImportSettings"),
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                UiLanguageService.Tf("Settings.UnableToReadSelectedFile", exception.Message),
                UiLanguageService.T("Settings.ImportSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (!AppStateStore.TryDeserialize(json, out var importedState))
        {
            MessageBox.Show(
                owner,
                UiLanguageService.T("Settings.InvalidSettingsJson"),
                UiLanguageService.T("Settings.ImportSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        ApplyMachineLocalEvernoteSettings(source: _appState, target: importedState);
        _appState = importedState;
        _appState.Normalize();
        UiLanguageService.Apply(_appState.UiLanguageCode);
        AppLogger.SetDebugLoggingEnabled(_appState.EnableDebugLogs);

        var selectedApp = Enum.IsDefined(typeof(WrappedApp), _appState.LastSelectedApp)
            ? _appState.LastSelectedApp
            : AppConfig.DefaultApp;
        if (_appSwitcher.SelectedIndex != (int)selectedApp)
        {
            _appSwitcher.SelectedIndex = (int)selectedApp;
        }

        _evernoteDbPathTextBox.Text = GetConfiguredEvernoteRootPath() ?? string.Empty;
        SyncEvernoteShowIgnoredCheckboxFromState();
        ApplyEvernotePollingSettings();
        UpdateAppChrome();
        LoadEvernoteTreeFromConfiguredRoot(showErrors: false, refreshTracking: false);
        SaveAppStateNow(queueGoogleDriveSync: false);
        _ = SyncConfigToGoogleDriveAsync(showErrors: true);

        MessageBox.Show(
            owner,
                UiLanguageService.Tf("Settings.ImportedFrom", Environment.NewLine, dialog.FileName),
            UiLanguageService.T("Settings.ImportSettings"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return true;
    }

    private static AppState CreateSharedSyncState(AppState source)
    {
        var cloned = AppStateStore.Deserialize(AppStateStore.Serialize(source));
        cloned.EvernoteLocalDbPath = null;
        cloned.EvernotePollingPaused = false;
        cloned.EvernotePollingIntervalMinutes = AppState.DefaultEvernotePollingIntervalMinutes;
        cloned.Normalize();
        return cloned;
    }

    private static void ApplyMachineLocalEvernoteSettings(AppState source, AppState target)
    {
        target.EvernoteLocalDbPath = source.EvernoteLocalDbPath;
        target.EvernotePollingPaused = source.EvernotePollingPaused;
        target.EvernotePollingIntervalMinutes = source.EvernotePollingIntervalMinutes;
    }
}

