namespace WinGeminiWrapper;

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

        using var settingsForm = new SettingsForm(
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
            _settingsFormOpenInstance = null;
            return;
        }

        var stateChanged = false;

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

        SaveAppStateNow(queueGoogleDriveSync: false);
        await SyncDistributedPollingStateAsync(showErrors: true, processIncomingRequests: false);
        _ = SyncConfigToGoogleDriveAsync(showErrors: true);
        _settingsFormOpenInstance = null;
    }

    private void ExportSettingsWithDialog(IWin32Window owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export settings",
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
                $"Settings exported to:{Environment.NewLine}{dialog.FileName}",
                "Settings export",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                $"Unable to export settings.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Settings export",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool ImportSettingsWithDialog(IWin32Window owner)
    {
        if (MessageBox.Show(
                owner,
                "Importing a settings file will replace your current local configuration. Continue?",
                "Import settings",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return false;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Import settings",
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
                $"Unable to read the selected file.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Import settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (!AppStateStore.TryDeserialize(json, out var importedState))
        {
            MessageBox.Show(
                owner,
                "The selected file does not contain valid settings JSON.",
                "Import settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        ApplyMachineLocalEvernoteSettings(source: _appState, target: importedState);
        _appState = importedState;
        _appState.Normalize();
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
            $"Settings imported from:{Environment.NewLine}{dialog.FileName}",
            "Import settings",
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
