namespace WinGeminiWrapper;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _closeBehaviorComboBox;
    private readonly NumericUpDown _pollIntervalMinutesNumeric;
    private readonly CheckBox _pausePollingCheckBox;
    private readonly Label _pollingLockOwnerLabel;
    private readonly Button _pollingForceLockButton;
    private readonly Label _pollingPendingRequestLabel;
    private readonly Label _pollingNextStatePollLabel;
    private readonly NumericUpDown _maxMarkdownFilesNumeric;
    private readonly CheckBox _enableDebugLogsCheckBox;
    private readonly CheckBox _googleDriveSyncEnabledCheckBox;
    private readonly CheckBox _googleDriveAutoRestoreCheckBox;
    private readonly TextBox _googleDriveFileIdTextBox;
    private readonly Label _googleOAuthStatusLabel;
    private readonly Label _googleOAuthClientSourceLabel;
    private readonly Button _googleOAuthConnectButton;

    private string? _googleDriveClientIdValue;
    private string? _googleDriveClientSecretValue;
    private bool _suspendPausePollingChangedEvent;
    private bool _pausePollingToggleBusy;
    private bool _pausePollingRequestInProgress;
    private bool _confirmedPausePollingState;
    internal event Action<bool>? EvernotePollingPausedChanged;
    internal event Action? ForcePollingLockRequested;
    internal event Action? ExportSettingsRequested;
    internal event Action? ImportSettingsRequested;

    internal CloseButtonBehavior SelectedCloseButtonBehavior =>
        _closeBehaviorComboBox.SelectedIndex == 1
            ? CloseButtonBehavior.CloseApp
            : CloseButtonBehavior.MinimizeToTray;

    internal int SelectedEvernotePollingIntervalMinutes => (int)_pollIntervalMinutesNumeric.Value;
    internal bool IsEvernotePollingPaused => _pausePollingCheckBox.Checked;
    internal int SelectedMaxMarkdownFilesToKeep => (int)_maxMarkdownFilesNumeric.Value;
    internal bool IsDebugLoggingEnabled => _enableDebugLogsCheckBox.Checked;
    internal bool IsGoogleDriveSyncEnabled => _googleDriveSyncEnabledCheckBox.Checked;
    internal bool IsGoogleDriveAutoRestoreOnStartup => _googleDriveAutoRestoreCheckBox.Checked;
    internal string? GoogleDriveClientId => NormalizeOptionalText(_googleDriveClientIdValue);
    internal string? GoogleDriveClientSecret => NormalizeOptionalText(_googleDriveClientSecretValue);
    internal string? GoogleDriveConfigFileId => NormalizeOptionalText(_googleDriveFileIdTextBox.Text);

    internal SettingsForm(
        CloseButtonBehavior currentCloseBehavior,
        int currentEvernotePollingIntervalMinutes,
        bool currentEvernotePollingPaused,
        int currentMaxMarkdownFilesToKeep,
        bool currentEnableDebugLogs,
        bool currentGoogleDriveSyncEnabled,
        bool currentGoogleDriveAutoRestoreOnStartup,
        string? currentGoogleDriveClientId,
        string? currentGoogleDriveClientSecret,
        string? currentGoogleDriveConfigFileId)
    {
        _googleDriveClientIdValue = NormalizeOptionalText(currentGoogleDriveClientId);
        _googleDriveClientSecretValue = NormalizeOptionalText(currentGoogleDriveClientSecret);
        _confirmedPausePollingState = currentEvernotePollingPaused;

        Text = AppVersionProvider.FormatWindowTitle("Settings");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 630);
        Icon = AppIconProvider.GetIcon();

        var closeBehaviorLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 22,
            Text = "Close button behavior:"
        };

        _closeBehaviorComboBox = new ComboBox
        {
            Left = 16,
            Top = 48,
            Width = 420,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _closeBehaviorComboBox.Items.Add("Minimize to tray");
        _closeBehaviorComboBox.Items.Add("Close app");
        _closeBehaviorComboBox.SelectedIndex = currentCloseBehavior == CloseButtonBehavior.CloseApp ? 1 : 0;

        var helpTextLabel = new Label
        {
            Left = 16,
            Top = 82,
            Width = 650,
            Height = 30,
            Text = "Controls what happens when you click the window close button."
        };

        var pollingLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 122,
            Text = "Evernote polling frequency (minutes):"
        };

        _pollIntervalMinutesNumeric = new NumericUpDown
        {
            Left = 16,
            Top = 148,
            Width = 120,
            Minimum = 1,
            Maximum = 1440,
            Value = Math.Clamp(currentEvernotePollingIntervalMinutes, 1, 1440)
        };

        _pausePollingCheckBox = new CheckBox
        {
            Left = 152,
            Top = 150,
            Width = 220,
            Text = "Pause automatic polling",
            Checked = currentEvernotePollingPaused
        };
        _pausePollingCheckBox.CheckedChanged += (_, _) =>
        {
            if (_suspendPausePollingChangedEvent)
            {
                return;
            }

            if (_pausePollingRequestInProgress)
            {
                SetPausePollingCheckedSilently(_confirmedPausePollingState);
                return;
            }

            var requestedPauseState = _pausePollingCheckBox.Checked;
            _pausePollingRequestInProgress = true;
            SetPausePollingCheckedSilently(_confirmedPausePollingState);
            RefreshPausePollingEnabledState();
            EvernotePollingPausedChanged?.Invoke(requestedPauseState);
        };

        _pollingLockOwnerLabel = new Label
        {
            AutoSize = false,
            Left = 380,
            Top = 152,
            Width = 210,
            Height = 24,
            ForeColor = Color.DarkRed,
            Text = "Lock: checking..."
        };

        _pollingForceLockButton = new Button
        {
            Left = 594,
            Top = 148,
            Width = 82,
            Height = 28,
            Text = "Force"
        };
        _pollingForceLockButton.Click += (_, _) => ForcePollingLockRequested?.Invoke();

        _pollingPendingRequestLabel = new Label
        {
            AutoSize = false,
            Left = 152,
            Top = 172,
            Width = 520,
            Height = 24,
            ForeColor = Color.DarkOrange,
            Text = string.Empty,
            Visible = false
        };

        _pollingNextStatePollLabel = new Label
        {
            AutoSize = false,
            Left = 152,
            Top = 190,
            Width = 520,
            Height = 24,
            ForeColor = Color.DimGray,
            Text = "State poll: every 6s, next in --s"
        };

        var maxMarkdownLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 220,
            Text = "Keep last X markdown exports:"
        };

        _maxMarkdownFilesNumeric = new NumericUpDown
        {
            Left = 16,
            Top = 246,
            Width = 120,
            Minimum = 1,
            Maximum = 1000,
            Value = Math.Clamp(currentMaxMarkdownFilesToKeep, 1, 1000)
        };

        var evernoteHelpLabel = new Label
        {
            Left = 152,
            Top = 248,
            Width = 530,
            Height = 36,
            Text = "Older files in ./markdown will be deleted automatically after each export."
        };

        _enableDebugLogsCheckBox = new CheckBox
        {
            Left = 16,
            Top = 282,
            Width = 280,
            Text = "Enable debug logs",
            Checked = currentEnableDebugLogs
        };

        var localConfigPathLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 318,
            Text = $"Local config file: {AppConfig.LocalConfigFilePath}"
        };

        var driveSectionLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 348,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Google Drive config sync"
        };

        _googleDriveSyncEnabledCheckBox = new CheckBox
        {
            Left = 16,
            Top = 376,
            Width = 330,
            Text = "Enable Google Drive sync",
            Checked = currentGoogleDriveSyncEnabled
        };

        _googleDriveAutoRestoreCheckBox = new CheckBox
        {
            Left = 360,
            Top = 376,
            Width = 280,
            Text = "Auto-restore at startup",
            Checked = currentGoogleDriveAutoRestoreOnStartup
        };

        var oauthHintLabel = new Label
        {
            Left = 16,
            Top = 408,
            Width = 660,
            Height = 36,
            Text = "Clique sur le bouton ci-dessous: une fenetre Google OAuth standard s'ouvre pour te connecter."
        };

        _googleOAuthConnectButton = new Button
        {
            AutoSize = true,
            Left = 16,
            Top = 448,
            Width = 220,
            Text = "Se connecter avec Google"
        };
        _googleOAuthConnectButton.Click += async (_, _) => await ConnectGoogleDriveOAuthAsync();

        _googleOAuthStatusLabel = new Label
        {
            Left = 252,
            Top = 454,
            Width = 424,
            Height = 32,
            Text = HasSavedOAuthClient() ? "OAuth status: configured." : "OAuth status: not connected."
        };

        _googleOAuthClientSourceLabel = new Label
        {
            Left = 16,
            Top = 488,
            Width = 660,
            Height = 32,
            Text = $"OAuth client source: {(File.Exists(AppConfig.GoogleDriveOAuthClientJsonPath) ? AppConfig.GoogleDriveOAuthClientJsonPath : "(not detected)")}"
        };

        var driveFileIdLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 526,
            Text = "Drive file ID (optional):"
        };

        _googleDriveFileIdTextBox = new TextBox
        {
            Left = 16,
            Top = 548,
            Width = 660,
            Text = currentGoogleDriveConfigFileId ?? string.Empty
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Left = 500,
            Top = 582,
            Width = 80
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 588,
            Top = 582,
            Width = 80
        };

        var exportButton = new Button
        {
            Text = "Export settings",
            Left = 16,
            Top = 582,
            Width = 130
        };
        exportButton.Click += (_, _) => ExportSettingsRequested?.Invoke();

        var importButton = new Button
        {
            Text = "Import settings",
            Left = 154,
            Top = 582,
            Width = 130
        };
        importButton.Click += (_, _) => ImportSettingsRequested?.Invoke();

        Controls.Add(closeBehaviorLabel);
        Controls.Add(_closeBehaviorComboBox);
        Controls.Add(helpTextLabel);
        Controls.Add(pollingLabel);
        Controls.Add(_pollIntervalMinutesNumeric);
        Controls.Add(_pausePollingCheckBox);
        Controls.Add(_pollingLockOwnerLabel);
        Controls.Add(_pollingForceLockButton);
        Controls.Add(_pollingPendingRequestLabel);
        Controls.Add(_pollingNextStatePollLabel);
        Controls.Add(maxMarkdownLabel);
        Controls.Add(_maxMarkdownFilesNumeric);
        Controls.Add(evernoteHelpLabel);
        Controls.Add(_enableDebugLogsCheckBox);
        Controls.Add(localConfigPathLabel);
        Controls.Add(driveSectionLabel);
        Controls.Add(_googleDriveSyncEnabledCheckBox);
        Controls.Add(_googleDriveAutoRestoreCheckBox);
        Controls.Add(oauthHintLabel);
        Controls.Add(_googleOAuthConnectButton);
        Controls.Add(_googleOAuthStatusLabel);
        Controls.Add(_googleOAuthClientSourceLabel);
        Controls.Add(driveFileIdLabel);
        Controls.Add(_googleDriveFileIdTextBox);
        Controls.Add(exportButton);
        Controls.Add(importButton);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    internal void UpdatePollingLockStatus(string? lockOwnerDisplayName, bool isOwnedByCurrentHost)
    {
        var ownerLabel = string.IsNullOrWhiteSpace(lockOwnerDisplayName) ? "(none)" : lockOwnerDisplayName.Trim();
        _pollingLockOwnerLabel.Text = $"Lock: {ownerLabel}";
        _pollingLockOwnerLabel.ForeColor = isOwnedByCurrentHost ? Color.DarkGreen : Color.DarkRed;
    }

    internal void UpdatePendingPollingRequest(string? pendingTargetDisplayName)
    {
        var hasPending = !string.IsNullOrWhiteSpace(pendingTargetDisplayName);
        _pollingPendingRequestLabel.Visible = hasPending;
        _pollingPendingRequestLabel.Text = hasPending
            ? $"en attente de confirmation sur {pendingTargetDisplayName}"
            : string.Empty;
        RefreshPausePollingEnabledState();
    }

    internal void UpdateNextStatePollInfo(int intervalSeconds, int secondsUntilNextPoll)
    {
        var safeIntervalSeconds = Math.Max(1, intervalSeconds);
        var safeRemainingSeconds = Math.Max(0, secondsUntilNextPoll);
        _pollingNextStatePollLabel.Text = $"State poll: every {safeIntervalSeconds}s, next in {safeRemainingSeconds}s";
    }

    internal void SetPausePollingCheckedSilently(bool isPaused)
    {
        if (_pausePollingCheckBox.Checked == isPaused)
        {
            return;
        }

        _suspendPausePollingChangedEvent = true;
        try
        {
            _pausePollingCheckBox.Checked = isPaused;
        }
        finally
        {
            _suspendPausePollingChangedEvent = false;
        }
    }

    internal void ConfirmPausePollingState(bool isPaused)
    {
        _confirmedPausePollingState = isPaused;
        _pausePollingRequestInProgress = false;
        SetPausePollingCheckedSilently(isPaused);
        RefreshPausePollingEnabledState();
    }

    internal void SetPausePollingBusy(bool isBusy)
    {
        _pausePollingToggleBusy = isBusy;
        RefreshPausePollingEnabledState();
    }

    internal void SetForceLockBusy(bool isBusy)
    {
        _pollingForceLockButton.Enabled = !isBusy;
    }

    private void RefreshPausePollingEnabledState()
    {
        var hasPendingLabel = _pollingPendingRequestLabel.Visible;
        _pausePollingCheckBox.Enabled = !_pausePollingToggleBusy && !_pausePollingRequestInProgress && !hasPendingLabel;
    }

    private bool HasSavedOAuthClient()
    {
        return !string.IsNullOrWhiteSpace(_googleDriveClientIdValue) &&
               !string.IsNullOrWhiteSpace(_googleDriveClientSecretValue);
    }

    private async Task ConnectGoogleDriveOAuthAsync()
    {
        if (!TryEnsureOAuthClientCredentials())
        {
            return;
        }

        UseWaitCursor = true;
        _googleOAuthConnectButton.Enabled = false;
        _googleOAuthStatusLabel.Text = "OAuth status: connecting...";

        try
        {
            var result = await GoogleDriveConfigSyncService.AuthorizeInteractiveAsync(
                _googleDriveClientIdValue ?? string.Empty,
                _googleDriveClientSecretValue ?? string.Empty,
                NormalizeOptionalText(_googleDriveFileIdTextBox.Text),
                CancellationToken.None);

            if (!result.IsSuccess)
            {
                var error = result.Error ?? "Unknown OAuth error.";
                _googleOAuthStatusLabel.Text = $"OAuth status: failed ({error})";
                MessageBox.Show(
                    this,
                    $"Connexion OAuth echouee.{Environment.NewLine}{Environment.NewLine}{error}",
                    "OAuth Google Drive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.ExistingConfigFileId))
            {
                _googleDriveFileIdTextBox.Text = result.ExistingConfigFileId;
            }

            _googleDriveSyncEnabledCheckBox.Checked = true;
            _googleDriveAutoRestoreCheckBox.Checked = true;

            var identity = !string.IsNullOrWhiteSpace(result.UserEmail)
                ? result.UserEmail
                : (result.UserDisplayName ?? "Google account connected");
            _googleOAuthStatusLabel.Text = $"OAuth status: connected ({identity}).";
        }
        finally
        {
            UseWaitCursor = false;
            _googleOAuthConnectButton.Enabled = true;
        }
    }

    private bool TryEnsureOAuthClientCredentials()
    {
        if (HasSavedOAuthClient())
        {
            return true;
        }

        if (GoogleDriveConfigSyncService.TryLoadClientSecretsFromDefaultLocations(
                out var autoClientId,
                out var autoClientSecret,
                out var sourcePath,
                out _))
        {
            _googleDriveClientIdValue = autoClientId;
            _googleDriveClientSecretValue = autoClientSecret;
            _googleOAuthClientSourceLabel.Text = $"OAuth client source: {sourcePath}";
            return true;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Select Google OAuth credentials JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            _googleOAuthStatusLabel.Text = "OAuth status: credentials file required.";
            return false;
        }

        if (!GoogleDriveConfigSyncService.TryExtractClientSecretsFromFile(
                dialog.FileName,
                out var fileClientId,
                out var fileClientSecret,
                out var extractError))
        {
            _googleOAuthStatusLabel.Text = $"OAuth status: error ({extractError})";
            MessageBox.Show(
                this,
                $"Impossible de lire les credentials OAuth.{Environment.NewLine}{Environment.NewLine}{extractError}",
                "OAuth Google Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        _googleDriveClientIdValue = fileClientId;
        _googleDriveClientSecretValue = fileClientSecret;
        _googleOAuthClientSourceLabel.Text = $"OAuth client source: {dialog.FileName}";

        try
        {
            Directory.CreateDirectory(AppConfig.AppDataRootFolder);
            File.Copy(dialog.FileName, AppConfig.GoogleDriveOAuthClientJsonPath, overwrite: true);
            _googleOAuthClientSourceLabel.Text = $"OAuth client source: {AppConfig.GoogleDriveOAuthClientJsonPath}";
        }
        catch
        {
            // Best effort cache only.
        }

        return true;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
