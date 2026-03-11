namespace WinGeminiWrapper;

internal static class AppConfig
{
    internal const string ProductName = "Win Gemini";

    internal static readonly string AppDataRootFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGeminiWrapper");

    internal const string GeminiAppUrl = "https://gemini.google.com/app";
    internal const string NotebookLmAppUrl = "https://notebooklm.google.com/";
    internal const WrappedApp DefaultApp = WrappedApp.Gemini;

    internal static readonly string WebViewUserDataFolder = Path.Combine(AppDataRootFolder, "WebView2");
    internal static readonly string StateFilePath = Path.Combine(AppDataRootFolder, "app-state.json");

    internal static string GetAppUrl(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => NotebookLmAppUrl,
            _ => GeminiAppUrl
        };

    internal static string GetAppDisplayName(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => "NotebookLM",
            _ => "Gemini"
        };
}
