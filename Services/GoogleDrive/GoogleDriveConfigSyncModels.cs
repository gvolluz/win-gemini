namespace WinGeminiWrapper;

internal sealed record GoogleDriveConfigDownloadResult(
    bool IsSuccess,
    bool IsNotFound,
    string? ConfigJson,
    string? FileId,
    DateTimeOffset? ModifiedTimeUtc,
    string? Error)
{
    internal static GoogleDriveConfigDownloadResult SuccessResult(
        string configJson,
        string? fileId,
        DateTimeOffset? modifiedTimeUtc) =>
        new(true, false, configJson, fileId, modifiedTimeUtc, null);

    internal static GoogleDriveConfigDownloadResult NotFound() =>
        new(false, true, null, null, null, null);

    internal static GoogleDriveConfigDownloadResult Failure(string error) =>
        new(false, false, null, null, null, error);
}

internal sealed record GoogleDriveConfigUploadResult(
    bool IsSuccess,
    string? FileId,
    DateTimeOffset? ModifiedTimeUtc,
    string? Error)
{
    internal static GoogleDriveConfigUploadResult SuccessResult(string? fileId, DateTimeOffset? modifiedTimeUtc) =>
        new(true, fileId, modifiedTimeUtc, null);

    internal static GoogleDriveConfigUploadResult Failure(string error) =>
        new(false, null, null, error);
}

internal sealed record GoogleDriveAuthorizationResult(
    bool IsSuccess,
    string? UserDisplayName,
    string? UserEmail,
    string? ExistingConfigFileId,
    string? Error)
{
    internal static GoogleDriveAuthorizationResult SuccessResult(
        string? userDisplayName,
        string? userEmail,
        string? existingConfigFileId) =>
        new(true, userDisplayName, userEmail, existingConfigFileId, null);

    internal static GoogleDriveAuthorizationResult Failure(string error) =>
        new(false, null, null, null, error);
}

internal sealed record EvernoteDriveFileUploadItem(string LocalFilePath, bool IsBackup, bool ConvertToGoogleDoc);

internal sealed record GoogleDriveMarkdownSyncResult(
    bool IsSuccess,
    int UploadedFiles,
    int ConvertedGoogleDocs,
    string? Error)
{
    internal static GoogleDriveMarkdownSyncResult SuccessResult(int uploadedFiles, int convertedGoogleDocs) =>
        new(true, uploadedFiles, convertedGoogleDocs, null);

    internal static GoogleDriveMarkdownSyncResult Failure(string error) =>
        new(false, 0, 0, error);
}

internal sealed record GoogleDrivePollingStateListResult(
    bool IsSuccess,
    IReadOnlyList<GoogleDrivePollingStateFile> States,
    string? Error)
{
    internal static GoogleDrivePollingStateListResult SuccessResult(IReadOnlyList<GoogleDrivePollingStateFile> states) =>
        new(true, states, null);

    internal static GoogleDrivePollingStateListResult Failure(string error) =>
        new(false, [], error);
}

internal sealed record GoogleDrivePollingStateMetaListResult(
    bool IsSuccess,
    IReadOnlyList<GoogleDrivePollingStateMetaFile> States,
    string? Error)
{
    internal static GoogleDrivePollingStateMetaListResult SuccessResult(IReadOnlyList<GoogleDrivePollingStateMetaFile> states) =>
        new(true, states, null);

    internal static GoogleDrivePollingStateMetaListResult Failure(string error) =>
        new(false, [], error);
}

internal sealed record GoogleDrivePollingStateUpsertResult(
    bool IsSuccess,
    string? FileId,
    string? Error)
{
    internal static GoogleDrivePollingStateUpsertResult SuccessResult(string? fileId) =>
        new(true, fileId, null);

    internal static GoogleDrivePollingStateUpsertResult Failure(string error) =>
        new(false, null, error);
}

internal sealed record GoogleDrivePollingStateDeleteResult(
    bool IsSuccess,
    int DeletedFiles,
    string? Error)
{
    internal static GoogleDrivePollingStateDeleteResult SuccessResult(int deletedFiles) =>
        new(true, deletedFiles, null);

    internal static GoogleDrivePollingStateDeleteResult Failure(string error) =>
        new(false, 0, error);
}

internal sealed class GoogleDrivePollingStateMetaFile
{
    public string FileId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTimeOffset? ModifiedTimeUtc { get; init; }
}

internal sealed class GoogleDrivePollingStateFile
{
    public string FileId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTimeOffset? ModifiedTimeUtc { get; init; }
    public GoogleDrivePollingStateDocument Document { get; init; } = new();
}

internal sealed class GoogleDrivePollingStateDocument
{
    public string InstanceId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool PauseAutomaticPolling { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public GoogleDrivePollingTakeoverRequest? PendingTakeoverRequest { get; set; }
}

internal sealed class GoogleDrivePollingTakeoverRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestedByInstanceId { get; set; } = string.Empty;
    public string RequestedByDisplayName { get; set; } = string.Empty;
    public string RequestedToInstanceId { get; set; } = string.Empty;
    public string RequestedToDisplayName { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; }
}
