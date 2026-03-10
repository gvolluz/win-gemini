using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WinGeminiWrapper;

internal sealed class LoginForm : Form
{
    private readonly Label _statusLabel;
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _fallbackRevealTimer;
    private bool _loginUiShown;
    private bool _completed;

    internal LoginForm()
    {
        Text = "Gemini Sign In";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1100, 760);
        Icon = AppIconProvider.GetIcon();
        ShowInTaskbar = false;
        Opacity = 0;

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            Text = "Checking Gemini session..."
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        _fallbackRevealTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000
        };
        _fallbackRevealTimer.Tick += FallbackRevealTimer_Tick;

        Controls.Add(_webView);
        Controls.Add(_statusLabel);

        Shown += LoginForm_Shown;
    }

    private async void LoginForm_Shown(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.WebViewUserDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppConfig.WebViewUserDataFolder);

            await _webView.EnsureCoreWebView2Async(environment);
            ConfigureWebView(_webView.CoreWebView2);
            _fallbackRevealTimer.Start();
            _webView.CoreWebView2.Navigate(AppConfig.GeminiAppUrl);
        }
        catch (Exception exception)
        {
            ShowLoginWindow($"Unable to initialize Gemini login: {exception.Message}");
        }
    }

    private void ConfigureWebView(CoreWebView2 coreWebView2)
    {
        coreWebView2.Settings.IsStatusBarEnabled = false;
        coreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
        coreWebView2.SourceChanged += CoreWebView2_SourceChanged;
        coreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            _webView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        var currentUri = _webView.Source;

        if (NavigationClassifier.IsGeminiChat(currentUri))
        {
            _completed = true;
            _fallbackRevealTimer.Stop();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (!_loginUiShown && NavigationClassifier.RequiresSignIn(currentUri))
        {
            ShowLoginWindow("Sign in with your Google account to continue.");
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess || _completed)
        {
            return;
        }

        ShowLoginWindow("Unable to reach Gemini. Check your network and try again.");
    }

    private void ShowLoginWindow(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowLoginWindow(message));
            return;
        }

        _statusLabel.Text = message;

        if (_loginUiShown)
        {
            return;
        }

        _fallbackRevealTimer.Stop();
        _loginUiShown = true;
        Opacity = 1;
        ShowInTaskbar = true;
        Activate();
    }

    private void FallbackRevealTimer_Tick(object? sender, EventArgs e)
    {
        if (_completed || _loginUiShown)
        {
            _fallbackRevealTimer.Stop();
            return;
        }

        ShowLoginWindow("Sign in with your Google account to continue.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fallbackRevealTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
