namespace WinGeminiWrapper;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _closeBehaviorComboBox;
    private readonly NumericUpDown _pollIntervalMinutesNumeric;
    private readonly CheckBox _pausePollingCheckBox;
    private readonly NumericUpDown _maxMarkdownFilesNumeric;
    private readonly CheckBox _googleDriveSyncEnabledCheckBox;
    private readonly CheckBox _googleDriveAutoRestoreCheckBox;
    private readonly TextBox _googleDriveFileIdTextBox;
    private readonly Label _googleOAuthStatusLabel;
    private readonly Label _googleOAuthClientSourceLabel;
    private readonly Button _googleOAuthConnectButton;

    private string? _googleDriveClientIdValue;
    private string? _googleDriveClientSecretValue;
    internal event Action<bool>? EvernotePollingPausedChanged;

    internal CloseButtonBehavior SelectedCloseButtonBehavior =>
        _closeBehaviorComboBox.SelectedIndex == 1
            ? CloseButtonBehavior.CloseApp
            : CloseButtonBehavior.MinimizeToTray;

    internal int SelectedEvernotePollingIntervalMinutes => (int)_pollIntervalMinutesNumeric.Value;
    internal bool IsEvernotePollingPaused => _pausePollingCheckBox.Checked;
    internal int SelectedMaxMarkdownFilesToKeep => (int)_maxMarkdownFilesNumeric.Value;
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
        bool currentGoogleDriveSyncEnabled,
        bool currentGoogleDriveAutoRestoreOnStartup,
        string? currentGoogleDriveClientId,
        string? currentGoogleDriveClientSecret,
        string? currentGoogleDriveConfigFileId)
    {
        _googleDriveClientIdValue = NormalizeOptionalText(currentGoogleDriveClientId);
        _googleDriveClientSecretValue = NormalizeOptionalText(currentGoogleDriveClientSecret);

        Text = AppVersionProvider.FormatWindowTitle("Settings");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 580);
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
            Width = 344,
            Text = "Pause automatic polling",
            Checked = currentEvernotePollingPaused
        };
        _pausePollingCheckBox.CheckedChanged += (_, _) => EvernotePollingPausedChanged?.Invoke(_pausePollingCheckBox.Checked);

        var maxMarkdownLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 188,
            Text = "Keep last X markdown exports:"
        };

        _maxMarkdownFilesNumeric = new NumericUpDown
        {
            Left = 16,
            Top = 214,
            Width = 120,
            Minimum = 1,
            Maximum = 1000,
            Value = Math.Clamp(currentMaxMarkdownFilesToKeep, 1, 1000)
        };

        var evernoteHelpLabel = new Label
        {
            Left = 152,
            Top = 216,
            Width = 530,
            Height = 36,
            Text = "Older files in ./markdown will be deleted automatically after each export."
        };

        var localConfigPathLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 262,
            Text = $"Local config file: {AppConfig.LocalConfigFilePath}"
        };

        var driveSectionLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 292,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Google Drive config sync"
        };

        _googleDriveSyncEnabledCheckBox = new CheckBox
        {
            Left = 16,
            Top = 320,
            Width = 330,
            Text = "Enable Google Drive sync",
            Checked = currentGoogleDriveSyncEnabled
        };

        _googleDriveAutoRestoreCheckBox = new CheckBox
        {
            Left = 360,
            Top = 320,
            Width = 280,
            Text = "Auto-restore at startup",
            Checked = currentGoogleDriveAutoRestoreOnStartup
        };

        var oauthHintLabel = new Label
        {
            Left = 16,
            Top = 352,
            Width = 660,
            Height = 36,
            Text = "Clique sur le bouton ci-dessous: une fenetre Google OAuth standard s'ouvre pour te connecter."
        };

        _googleOAuthConnectButton = new Button
        {
            AutoSize = true,
            Left = 16,
            Top = 392,
            Width = 220,
            Text = "Se connecter avec Google"
        };
        _googleOAuthConnectButton.Click += async (_, _) => await ConnectGoogleDriveOAuthAsync();

        _googleOAuthStatusLabel = new Label
        {
            Left = 252,
            Top = 398,
            Width = 424,
            Height = 32,
            Text = HasSavedOAuthClient() ? "OAuth status: configured." : "OAuth status: not connected."
        };

        _googleOAuthClientSourceLabel = new Label
        {
            Left = 16,
            Top = 432,
            Width = 660,
            Height = 32,
            Text = $"OAuth client source: {(File.Exists(AppConfig.GoogleDriveOAuthClientJsonPath) ? AppConfig.GoogleDriveOAuthClientJsonPath : "(not detected)")}"
        };

        var driveFileIdLabel = new Label
        {
            AutoSize = true,
            Left = 16,
            Top = 470,
            Text = "Drive file ID (optional):"
        };

        _googleDriveFileIdTextBox = new TextBox
        {
            Left = 16,
            Top = 492,
            Width = 660,
            Text = currentGoogleDriveConfigFileId ?? string.Empty
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Left = 500,
            Top = 532,
            Width = 80
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 588,
            Top = 532,
            Width = 80
        };

        Controls.Add(closeBehaviorLabel);
        Controls.Add(_closeBehaviorComboBox);
        Controls.Add(helpTextLabel);
        Controls.Add(pollingLabel);
        Controls.Add(_pollIntervalMinutesNumeric);
        Controls.Add(_pausePollingCheckBox);
        Controls.Add(maxMarkdownLabel);
        Controls.Add(_maxMarkdownFilesNumeric);
        Controls.Add(evernoteHelpLabel);
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
        Controls.Add(saveButton);
        Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
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
