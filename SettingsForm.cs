namespace WinGeminiWrapper;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _closeBehaviorComboBox;

    internal CloseButtonBehavior SelectedCloseButtonBehavior =>
        _closeBehaviorComboBox.SelectedIndex == 1
            ? CloseButtonBehavior.CloseApp
            : CloseButtonBehavior.MinimizeToTray;

    internal SettingsForm(CloseButtonBehavior currentCloseBehavior)
    {
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 170);
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
            Width = 420,
            Height = 30,
            Text = "Controls what happens when you click the window close button."
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Left = 270,
            Top = 126,
            Width = 80
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 356,
            Top = 126,
            Width = 80
        };

        Controls.Add(closeBehaviorLabel);
        Controls.Add(_closeBehaviorComboBox);
        Controls.Add(helpTextLabel);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }
}
