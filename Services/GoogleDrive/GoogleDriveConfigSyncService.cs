using Google.Apis.Drive.v3;

namespace WinGemini;

internal static partial class GoogleDriveConfigSyncService
{
    private const string ConfigMimeType = "application/json";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string MarkdownMimeType = "text/markdown";
    private const string GoogleDocumentMimeType = "application/vnd.google-apps.document";
    private static readonly string TokenScopeVersionFilePath =
        Path.Combine(AppConfig.GoogleDriveTokenStorePath, "scope-version.txt");
    private static readonly string[] Scopes =
    [
        DriveService.Scope.DriveFile,
        DriveService.Scope.DriveMetadataReadonly,
        DriveService.Scope.DriveAppdata
    ];

    internal static bool IsConfigured(AppState state)
    {
        return state.GoogleDriveSyncEnabled &&
               !string.IsNullOrWhiteSpace(state.GoogleDriveClientId) &&
               !string.IsNullOrWhiteSpace(state.GoogleDriveClientSecret);
    }

    private static string EscapeDriveQueryValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}

