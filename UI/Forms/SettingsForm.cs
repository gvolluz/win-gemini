namespace WinGemini;

internal sealed class SettingsForm : Form
{
    private readonly IGoogleDriveSyncService _googleDriveSyncService;

    private readonly Label _languageLabel;
    private readonly Label _languageHelpLabel;
    private readonly Label _closeBehaviorLabel;
    private readonly Label _closeBehaviorHelpLabel;
    private readonly Label _localConfigPathLabel;
    private readonly Label _driveSectionLabel;
    private readonly Label _oauthHintLabel;
    private readonly Label _driveFileIdLabel;

    private readonly ComboBox _languageComboBox;
    private readonly ComboBox _closeBehaviorComboBox;
    private readonly CheckBox _enableDebugLogsCheckBox;
    private readonly CheckBox _googleDriveSyncEnabledCheckBox;
    private readonly CheckBox _googleDriveAutoRestoreCheckBox;
    private readonly TextBox _googleDriveFileIdTextBox;
    private readonly Label _googleOAuthStatusLabel;
    private readonly Label _googleOAuthClientSourceLabel;
    private readonly Button _googleOAuthConnectButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _exportButton;
    private readonly Button _importButton;
    private readonly int _initialCloseBehaviorIndex;

    private string? _googleDriveClientIdValue;
    private string? _googleDriveClientSecretValue;

    internal event Action? ExportSettingsRequested;
    internal event Action? ImportSettingsRequested;
    internal event Action<string?>? UiLanguagePreviewChanged;

    internal CloseButtonBehavior SelectedCloseButtonBehavior =>
        _closeBehaviorComboBox.SelectedIndex == 1
            ? CloseButtonBehavior.CloseApp
            : CloseButtonBehavior.MinimizeToTray;

    internal bool IsDebugLoggingEnabled => _enableDebugLogsCheckBox.Checked;
    internal bool IsGoogleDriveSyncEnabled => _googleDriveSyncEnabledCheckBox.Checked;
    internal bool IsGoogleDriveAutoRestoreOnStartup => _googleDriveAutoRestoreCheckBox.Checked;
    internal string? SelectedUiLanguageCode =>
        _languageComboBox.SelectedItem is UiLanguageOption selected &&
        !string.Equals(selected.Code, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? selected.Code
            : null;
    internal string? GoogleDriveClientId => NormalizeOptionalText(_googleDriveClientIdValue);
    internal string? GoogleDriveClientSecret => NormalizeOptionalText(_googleDriveClientSecretValue);
    internal string? GoogleDriveConfigFileId => NormalizeOptionalText(_googleDriveFileIdTextBox.Text);

    internal SettingsForm(
        string? currentUiLanguageCode,
        CloseButtonBehavior currentCloseBehavior,
        bool currentEnableDebugLogs,
        bool currentGoogleDriveSyncEnabled,
        bool currentGoogleDriveAutoRestoreOnStartup,
        string? currentGoogleDriveClientId,
        string? currentGoogleDriveClientSecret,
        string? currentGoogleDriveConfigFileId,
        IGoogleDriveSyncService? googleDriveSyncService = null)
    {
        _googleDriveSyncService = googleDriveSyncService ?? GoogleDriveSyncServiceAdapter.Instance;
        _googleDriveClientIdValue = NormalizeOptionalText(currentGoogleDriveClientId);
        _googleDriveClientSecretValue = NormalizeOptionalText(currentGoogleDriveClientSecret);
        _initialCloseBehaviorIndex = currentCloseBehavior == CloseButtonBehavior.CloseApp ? 1 : 0;

        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 530);
        Icon = AppIconProvider.GetIcon();
        ApplyRightToLeft(UiLanguageService.IsRightToLeftCurrentLanguage());

        _languageLabel = new Label { AutoSize = true, Left = 16, Top = 14 };
        _languageHelpLabel = new Label { Left = 16, Top = 64, Width = 650, Height = 30 };
        _closeBehaviorLabel = new Label { AutoSize = true, Left = 16, Top = 96 };
        _closeBehaviorHelpLabel = new Label { Left = 16, Top = 156, Width = 650, Height = 30 };
        _localConfigPathLabel = new Label { AutoSize = true, Left = 16, Top = 230 };
        _driveSectionLabel = new Label { AutoSize = true, Left = 16, Top = 262, Font = new Font(Font, FontStyle.Bold) };
        _oauthHintLabel = new Label { Left = 16, Top = 322, Width = 660, Height = 36 };
        _driveFileIdLabel = new Label { AutoSize = true, Left = 16, Top = 436 };

        _languageComboBox = new ComboBox
        {
            Left = 16,
            Top = 36,
            Width = 420,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(UiLanguageOption.NativeDisplayName)
        };
        foreach (var option in UiLanguageService.GetLanguageOptions())
        {
            _languageComboBox.Items.Add(option);
        }

        var normalizedCurrentLanguage = string.IsNullOrWhiteSpace(currentUiLanguageCode)
            ? UiLanguageCatalog.AutoLanguageCode
            : currentUiLanguageCode;
        if (_languageComboBox.Items.Count > 0)
        {
            _languageComboBox.SelectedIndex = 0;
        }
        for (var i = 0; i < _languageComboBox.Items.Count; i++)
        {
            if (_languageComboBox.Items[i] is not UiLanguageOption option)
            {
                continue;
            }

            if (!string.Equals(option.Code, normalizedCurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _languageComboBox.SelectedIndex = i;
            break;
        }

        _languageComboBox.SelectedIndexChanged += LanguageComboBox_SelectedIndexChanged;

        _closeBehaviorComboBox = new ComboBox
        {
            Left = 16,
            Top = 122,
            Width = 420,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _enableDebugLogsCheckBox = new CheckBox { Left = 16, Top = 196, Width = 320, Checked = currentEnableDebugLogs };
        _googleDriveSyncEnabledCheckBox = new CheckBox { Left = 16, Top = 290, Width = 330, Checked = currentGoogleDriveSyncEnabled };
        _googleDriveAutoRestoreCheckBox = new CheckBox { Left = 360, Top = 290, Width = 280, Checked = currentGoogleDriveAutoRestoreOnStartup };

        _googleOAuthConnectButton = new Button { AutoSize = true, Left = 16, Top = 362, Width = 220 };
        _googleOAuthConnectButton.Click += async (_, _) => await ConnectGoogleDriveOAuthAsync();

        _googleOAuthStatusLabel = new Label { Left = 252, Top = 368, Width = 424, Height = 32 };
        _googleOAuthClientSourceLabel = new Label { Left = 16, Top = 402, Width = 660, Height = 32 };

        _googleDriveFileIdTextBox = new TextBox
        {
            Left = 16,
            Top = 458,
            Width = 660,
            Text = currentGoogleDriveConfigFileId ?? string.Empty
        };

        _saveButton = new Button { DialogResult = DialogResult.OK, Left = 500, Top = 494, Width = 80 };
        _cancelButton = new Button { DialogResult = DialogResult.Cancel, Left = 588, Top = 494, Width = 80 };
        _exportButton = new Button { Left = 16, Top = 494, Width = 130 };
        _importButton = new Button { Left = 154, Top = 494, Width = 130 };
        _exportButton.Click += (_, _) => ExportSettingsRequested?.Invoke();
        _importButton.Click += (_, _) => ImportSettingsRequested?.Invoke();

        Controls.Add(_languageLabel);
        Controls.Add(_languageComboBox);
        Controls.Add(_languageHelpLabel);
        Controls.Add(_closeBehaviorLabel);
        Controls.Add(_closeBehaviorComboBox);
        Controls.Add(_closeBehaviorHelpLabel);
        Controls.Add(_enableDebugLogsCheckBox);
        Controls.Add(_localConfigPathLabel);
        Controls.Add(_driveSectionLabel);
        Controls.Add(_googleDriveSyncEnabledCheckBox);
        Controls.Add(_googleDriveAutoRestoreCheckBox);
        Controls.Add(_oauthHintLabel);
        Controls.Add(_googleOAuthConnectButton);
        Controls.Add(_googleOAuthStatusLabel);
        Controls.Add(_googleOAuthClientSourceLabel);
        Controls.Add(_driveFileIdLabel);
        Controls.Add(_googleDriveFileIdTextBox);
        Controls.Add(_exportButton);
        Controls.Add(_importButton);
        Controls.Add(_saveButton);
        Controls.Add(_cancelButton);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        Text = AppVersionProvider.FormatWindowTitle(UiLanguageService.T("Common.Settings"));
        ApplyRightToLeft(UiLanguageService.IsRightToLeftCurrentLanguage());

        var selectedLanguageCode = SelectedUiLanguageCode ?? UiLanguageCatalog.AutoLanguageCode;
        _languageComboBox.SelectedIndexChanged -= LanguageComboBox_SelectedIndexChanged;
        _languageComboBox.Items.Clear();
        foreach (var option in UiLanguageService.GetLanguageOptions())
        {
            _languageComboBox.Items.Add(option);
        }

        if (_languageComboBox.Items.Count > 0)
        {
            _languageComboBox.SelectedIndex = 0;
        }
        for (var i = 0; i < _languageComboBox.Items.Count; i++)
        {
            if (_languageComboBox.Items[i] is not UiLanguageOption option)
            {
                continue;
            }

            if (!string.Equals(option.Code, selectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _languageComboBox.SelectedIndex = i;
            break;
        }
        _languageComboBox.SelectedIndexChanged += LanguageComboBox_SelectedIndexChanged;

        _languageLabel.Text = $"{UiLanguageService.T("Settings.LanguageLabel")}:";
        _languageHelpLabel.Text = UiLanguageService.T("Settings.LanguageHelp");

        _closeBehaviorLabel.Text = UiLanguageService.T("Settings.CloseBehaviorLabel");
        var previousSelection = _closeBehaviorComboBox.SelectedIndex;
        _closeBehaviorComboBox.Items.Clear();
        _closeBehaviorComboBox.Items.Add(UiLanguageService.T("Settings.CloseBehavior.MinimizeToTray"));
        _closeBehaviorComboBox.Items.Add(UiLanguageService.T("Settings.CloseBehavior.CloseApp"));
        if (_closeBehaviorComboBox.Items.Count > 0)
        {
            if (previousSelection >= 0 && previousSelection < _closeBehaviorComboBox.Items.Count)
            {
                _closeBehaviorComboBox.SelectedIndex = previousSelection;
            }
            else
            {
                _closeBehaviorComboBox.SelectedIndex = Math.Clamp(_initialCloseBehaviorIndex, 0, _closeBehaviorComboBox.Items.Count - 1);
            }
        }
        _closeBehaviorHelpLabel.Text = UiLanguageService.T("Settings.CloseBehaviorHelp");

        _enableDebugLogsCheckBox.Text = UiLanguageService.T("Settings.EnableDebugLogs");
        _localConfigPathLabel.Text = UiLanguageService.Tf("Settings.LocalConfigFile", AppConfig.LocalConfigFilePath);
        _driveSectionLabel.Text = UiLanguageService.T("Settings.GoogleDriveSection");
        _googleDriveSyncEnabledCheckBox.Text = UiLanguageService.T("Settings.EnableGoogleDriveSync");
        _googleDriveAutoRestoreCheckBox.Text = UiLanguageService.T("Settings.AutoRestoreOnStartup");
        _oauthHintLabel.Text = UiLanguageService.T("Settings.GoogleOAuthHint");
        _googleOAuthConnectButton.Text = UiLanguageService.T("Settings.ConnectWithGoogle");
        _driveFileIdLabel.Text = UiLanguageService.T("Settings.DriveFileIdOptional");
        _saveButton.Text = UiLanguageService.T("Common.Save");
        _cancelButton.Text = UiLanguageService.T("Common.Cancel");
        _exportButton.Text = UiLanguageService.T("Settings.ExportSettings");
        _importButton.Text = UiLanguageService.T("Settings.ImportSettings");

        UpdateOAuthStatusLabelConfiguredState();
        UpdateOAuthClientSourceLabel();
    }

    private void LanguageComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_languageComboBox.SelectedItem is not UiLanguageOption selected)
        {
            return;
        }

        var previewLanguage = string.Equals(selected.Code, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? null
            : selected.Code;
        UiLanguagePreviewChanged?.Invoke(previewLanguage);
        ApplyLocalizedText();
    }

    private void UpdateOAuthStatusLabelConfiguredState()
    {
        _googleOAuthStatusLabel.Text = HasSavedOAuthClient()
            ? UiLanguageService.T("Settings.OAuthStatusConfigured")
            : UiLanguageService.T("Settings.OAuthStatusNotConnected");
    }

    private void UpdateOAuthClientSourceLabel(string? sourceOverride = null)
    {
        var source = sourceOverride;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = File.Exists(AppConfig.GoogleDriveOAuthClientJsonPath)
                ? AppConfig.GoogleDriveOAuthClientJsonPath
                : UiLanguageService.T("Settings.NotDetected");
        }

        _googleOAuthClientSourceLabel.Text = UiLanguageService.Tf("Settings.OAuthClientSource", source);
    }

    private void ApplyRightToLeft(bool isRightToLeft)
    {
        RightToLeft = isRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = isRightToLeft;
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
        _googleOAuthStatusLabel.Text = UiLanguageService.T("Settings.OAuthStatusConnecting");

        try
        {
            var result = await _googleDriveSyncService.AuthorizeInteractiveAsync(
                _googleDriveClientIdValue ?? string.Empty,
                _googleDriveClientSecretValue ?? string.Empty,
                NormalizeOptionalText(_googleDriveFileIdTextBox.Text),
                CancellationToken.None);

            if (!result.IsSuccess)
            {
                var error = result.Error ?? UiLanguageService.T("Settings.UnknownOAuthError");
                _googleOAuthStatusLabel.Text = UiLanguageService.Tf("Settings.OAuthStatusFailed", error);
                MessageBox.Show(
                    this,
                    UiLanguageService.Tf("Settings.OAuthConnectionFailedMessage", error),
                    UiLanguageService.T("Settings.GoogleOAuthTitle"),
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
                : (result.UserDisplayName ?? UiLanguageService.T("Settings.GoogleAccountConnectedFallback"));
            _googleOAuthStatusLabel.Text = UiLanguageService.Tf("Settings.OAuthStatusConnected", identity);
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

        if (_googleDriveSyncService.TryLoadClientSecretsFromDefaultLocations(
                out var autoClientId,
                out var autoClientSecret,
                out var sourcePath,
                out _))
        {
            _googleDriveClientIdValue = autoClientId;
            _googleDriveClientSecretValue = autoClientSecret;
            UpdateOAuthClientSourceLabel(sourcePath);
            return true;
        }

        using var dialog = new OpenFileDialog
        {
            Title = UiLanguageService.T("Settings.SelectOAuthCredentialsJson"),
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            _googleOAuthStatusLabel.Text = UiLanguageService.T("Settings.OAuthStatusCredentialsFileRequired");
            return false;
        }

        if (!_googleDriveSyncService.TryExtractClientSecretsFromFile(
                dialog.FileName,
                out var fileClientId,
                out var fileClientSecret,
                out var extractError))
        {
            _googleOAuthStatusLabel.Text = UiLanguageService.Tf("Settings.OAuthStatusError", extractError);
            MessageBox.Show(
                this,
                UiLanguageService.Tf("Settings.UnableToReadOAuthCredentials", extractError),
                UiLanguageService.T("Settings.GoogleOAuthTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        _googleDriveClientIdValue = fileClientId;
        _googleDriveClientSecretValue = fileClientSecret;
        UpdateOAuthClientSourceLabel(dialog.FileName);

        try
        {
            Directory.CreateDirectory(AppConfig.AppDataRootFolder);
            File.Copy(dialog.FileName, AppConfig.GoogleDriveOAuthClientJsonPath, overwrite: true);
            UpdateOAuthClientSourceLabel(AppConfig.GoogleDriveOAuthClientJsonPath);
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

