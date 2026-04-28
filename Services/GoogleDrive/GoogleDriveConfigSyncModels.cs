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
