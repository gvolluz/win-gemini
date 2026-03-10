namespace WinGeminiWrapper;

internal static class AppConfig
{
    internal const string GeminiAppUrl = "https://gemini.google.com/app";

    internal static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGeminiWrapper",
        "WebView2");
}
