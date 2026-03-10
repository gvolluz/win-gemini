using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WinGeminiWrapper;

internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private bool _exitRequested;
    private bool _balloonShown;

    internal MainForm()
    {
        Text = "Gemini";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1320, 880);
        Icon = SystemIcons.Application;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        _trayMenu = BuildTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Gemini",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        Load += MainForm_Load;
        Resize += MainForm_Resize;
        FormClosing += MainForm_FormClosing;
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.WebViewUserDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppConfig.WebViewUserDataFolder);

            await _webView.EnsureCoreWebView2Async(environment);
            ConfigureWebView(_webView.CoreWebView2);
            _webView.CoreWebView2.Navigate(AppConfig.GeminiAppUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Unable to start Gemini window.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _exitRequested = true;
            Close();
        }
    }

    private void ConfigureWebView(CoreWebView2 coreWebView2)
    {
        coreWebView2.Settings.IsStatusBarEnabled = false;
        coreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            _webView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open Gemini", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Refresh", null, (_, _) => _webView.CoreWebView2?.Reload());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        return menu;
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
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
        _trayIcon.ShowBalloonTip(
            2000,
            "Gemini is still running",
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
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
