namespace WinGeminiWrapper;

internal interface IGoogleDriveSyncService
{
    bool IsConfigured(AppState state);
    bool TryExtractClientSecretsFromFile(
        string credentialsJsonPath,
        out string clientId,
        out string clientSecret,
        out string error);
    bool TryLoadClientSecretsFromDefaultLocations(
        out string clientId,
        out string clientSecret,
        out string sourcePath,
        out string error);
    Task<GoogleDriveAuthorizationResult> AuthorizeInteractiveAsync(
        string clientId,
        string clientSecret,
        string? preferredConfigFileId,
        CancellationToken cancellationToken);
    Task<GoogleDriveConfigDownloadResult> DownloadConfigAsync(AppState state, CancellationToken cancellationToken);
    Task<GoogleDriveConfigUploadResult> UploadConfigAsync(AppState state, string configJson, CancellationToken cancellationToken);
    Task<GoogleDriveMarkdownSyncResult> SyncEvernoteMarkdownFilesAsync(
        AppState state,
        IReadOnlyCollection<EvernoteDriveFileUploadItem> uploads,
        CancellationToken cancellationToken);
    Task<GoogleDrivePollingStateListResult> ListPollingStatesAsync(AppState state, CancellationToken cancellationToken);
    Task<GoogleDrivePollingStateMetaListResult> ListPollingStateMetasAsync(AppState state, CancellationToken cancellationToken);
    Task<GoogleDrivePollingStateUpsertResult> UpsertPollingStateAsync(
        AppState state,
        string fileName,
        GoogleDrivePollingStateDocument document,
        string? existingFileId,
        CancellationToken cancellationToken);
    Task<GoogleDrivePollingStateDeleteResult> DeleteAllPollingStatesAsync(AppState state, CancellationToken cancellationToken);
}
