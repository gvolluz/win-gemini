namespace WinGeminiWrapper;

internal static class AppConfig
{
    internal const string ProductName = "Win Gemini";

    internal static readonly string AppDataRootFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGeminiWrapper");

    internal const string GeminiAppUrl = "https://gemini.google.com/app";
    internal const string NotebookLmAppUrl = "https://notebooklm.google.com/";
    internal const string GoogleDriveAppUrl = "https://drive.google.com/drive/my-drive";
    internal const WrappedApp DefaultApp = WrappedApp.Gemini;

    internal static readonly string WebViewUserDataFolder = Path.Combine(AppDataRootFolder, "WebView2");
    internal static readonly string LocalConfigFilePath = Path.Combine(AppDataRootFolder, "local-config.json");
    internal static readonly string LegacyStateFilePath = Path.Combine(AppDataRootFolder, "app-state.json");
    internal static readonly string StateFilePath = LocalConfigFilePath;
    internal static readonly string GoogleDriveOAuthClientJsonPath = Path.Combine(AppDataRootFolder, "google-oauth-client.json");
    internal static readonly string GoogleDriveTokenStorePath = Path.Combine(AppDataRootFolder, "GoogleDriveTokens");
    internal const string GoogleDriveTokenScopeVersion = "drivefile+drivemetadatareadonly+driveappdata-v1";
    internal const string GoogleDriveVisibleRootFolderName = "Apps";
    internal const string GoogleDriveVisibleAppFolderName = "WinGemini";
    internal const string GoogleDriveConfigFileName = "WinGeminiWrapper.config.json";
    internal const string GoogleDrivePollingStateFilePrefix = "state_";
    internal const string EvernoteExportRootFolderName = "ExportEvernote";
    internal const string EvernoteExportBackupsFolderName = "backups";

    internal static string GetAppUrl(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => NotebookLmAppUrl,
            WrappedApp.GoogleDrive => GoogleDriveAppUrl,
            WrappedApp.EvernoteExport => "about:blank",
            _ => GeminiAppUrl
        };

    internal static string GetAppDisplayName(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => "NotebookLM",
            WrappedApp.GoogleDrive => "Google Drive",
            WrappedApp.EvernoteExport => "Evernote Export",
            _ => "Gemini"
        };
}
