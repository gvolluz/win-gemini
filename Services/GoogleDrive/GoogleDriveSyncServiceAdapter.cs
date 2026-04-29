namespace WinGeminiWrapper;

internal sealed class GoogleDriveSyncServiceAdapter : IGoogleDriveSyncService
{
    internal static GoogleDriveSyncServiceAdapter Instance { get; } = new();

    private GoogleDriveSyncServiceAdapter()
    {
    }

    public bool IsConfigured(AppState state) => GoogleDriveConfigSyncService.IsConfigured(state);

    public bool TryExtractClientSecretsFromFile(
        string credentialsJsonPath,
        out string clientId,
        out string clientSecret,
        out string error) =>
        GoogleDriveConfigSyncService.TryExtractClientSecretsFromFile(
            credentialsJsonPath,
            out clientId,
            out clientSecret,
            out error);

    public bool TryLoadClientSecretsFromDefaultLocations(
        out string clientId,
        out string clientSecret,
        out string sourcePath,
        out string error) =>
        GoogleDriveConfigSyncService.TryLoadClientSecretsFromDefaultLocations(
            out clientId,
            out clientSecret,
            out sourcePath,
            out error);

    public Task<GoogleDriveAuthorizationResult> AuthorizeInteractiveAsync(
        string clientId,
        string clientSecret,
        string? preferredConfigFileId,
        CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.AuthorizeInteractiveAsync(
            clientId,
            clientSecret,
            preferredConfigFileId,
            cancellationToken);

    public Task<GoogleDriveConfigDownloadResult> DownloadConfigAsync(AppState state, CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.DownloadConfigAsync(state, cancellationToken);

    public Task<GoogleDriveConfigUploadResult> UploadConfigAsync(
        AppState state,
        string configJson,
        CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.UploadConfigAsync(state, configJson, cancellationToken);

    public Task<GoogleDriveMarkdownSyncResult> SyncEvernoteMarkdownFilesAsync(
        AppState state,
        IReadOnlyCollection<EvernoteDriveFileUploadItem> uploads,
        CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.SyncEvernoteMarkdownFilesAsync(state, uploads, cancellationToken);

    public Task<GoogleDrivePollingStateListResult> ListPollingStatesAsync(AppState state, CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.ListPollingStatesAsync(state, cancellationToken);

    public Task<GoogleDrivePollingStateMetaListResult> ListPollingStateMetasAsync(AppState state, CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.ListPollingStateMetasAsync(state, cancellationToken);

    public Task<GoogleDrivePollingStateUpsertResult> UpsertPollingStateAsync(
        AppState state,
        string fileName,
        GoogleDrivePollingStateDocument document,
        string? existingFileId,
        CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.UpsertPollingStateAsync(
            state,
            fileName,
            document,
            existingFileId,
            cancellationToken);

    public Task<GoogleDrivePollingStateDeleteResult> DeleteAllPollingStatesAsync(AppState state, CancellationToken cancellationToken) =>
        GoogleDriveConfigSyncService.DeleteAllPollingStatesAsync(state, cancellationToken);
}
