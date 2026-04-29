using System.Text;
using Google;
using Google.Apis.Drive.v3;

namespace WinGemini;

internal static partial class GoogleDriveConfigSyncService
{
    internal static async Task<GoogleDriveConfigDownloadResult> DownloadConfigAsync(
        AppState state,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDriveConfigDownloadResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var file = await ResolveTargetFileAsync(service, appFolderId, state.GoogleDriveConfigFileId, cancellationToken);
                    file ??= await ResolveLegacyAppDataFileAsync(service, state.GoogleDriveConfigFileId, cancellationToken);
                    if (file is null || string.IsNullOrWhiteSpace(file.Id))
                    {
                        return GoogleDriveConfigDownloadResult.NotFound();
                    }

                    using var contentStream = new MemoryStream();
                    var getRequest = service.Files.Get(file.Id);
                    await getRequest.DownloadAsync(contentStream, cancellationToken);

                    var json = Encoding.UTF8.GetString(contentStream.ToArray());
                    return GoogleDriveConfigDownloadResult.SuccessResult(
                        json,
                        file.Id,
                        file.ModifiedTimeDateTimeOffset);
                });
        }
        catch (Exception exception)
        {
            return GoogleDriveConfigDownloadResult.Failure(exception.Message);
        }
    }

    internal static async Task<GoogleDriveConfigUploadResult> UploadConfigAsync(
        AppState state,
        string configJson,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDriveConfigUploadResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var existingFile = await ResolveTargetFileAsync(service, appFolderId, state.GoogleDriveConfigFileId, cancellationToken);
                    var payload = Encoding.UTF8.GetBytes(configJson);

                    if (existingFile is null || string.IsNullOrWhiteSpace(existingFile.Id))
                    {
                        using var createStream = new MemoryStream(payload);
                        var metadata = new Google.Apis.Drive.v3.Data.File
                        {
                            Name = AppConfig.GoogleDriveConfigFileName,
                            MimeType = ConfigMimeType,
                            Parents = [appFolderId]
                        };

                        var createRequest = service.Files.Create(metadata, createStream, ConfigMimeType);
                        createRequest.Fields = "id, modifiedTime";
                        var created = await createRequest.UploadAsync(cancellationToken);
                        if (created.Status != Google.Apis.Upload.UploadStatus.Completed || createRequest.ResponseBody is null)
                        {
                            var reason = created.Exception?.Message ?? "Upload did not complete.";
                            return GoogleDriveConfigUploadResult.Failure(reason);
                        }

                        return GoogleDriveConfigUploadResult.SuccessResult(
                            createRequest.ResponseBody.Id,
                            createRequest.ResponseBody.ModifiedTimeDateTimeOffset);
                    }

                    using var updateStream = new MemoryStream(payload);
                    var updateMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = AppConfig.GoogleDriveConfigFileName,
                        MimeType = ConfigMimeType
                    };
                    var updateRequest = service.Files.Update(updateMetadata, existingFile.Id, updateStream, ConfigMimeType);
                    updateRequest.Fields = "id, modifiedTime";
                    var updated = await updateRequest.UploadAsync(cancellationToken);
                    if (updated.Status != Google.Apis.Upload.UploadStatus.Completed || updateRequest.ResponseBody is null)
                    {
                        var reason = updated.Exception?.Message ?? "Upload did not complete.";
                        return GoogleDriveConfigUploadResult.Failure(reason);
                    }

                    return GoogleDriveConfigUploadResult.SuccessResult(
                        updateRequest.ResponseBody.Id,
                        updateRequest.ResponseBody.ModifiedTimeDateTimeOffset);
                });
        }
        catch (Exception exception)
        {
            return GoogleDriveConfigUploadResult.Failure(exception.Message);
        }
    }

    private static async Task<Google.Apis.Drive.v3.Data.File?> ResolveTargetFileAsync(
        DriveService service,
        string parentFolderId,
        string? preferredFileId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredFileId))
        {
            try
            {
                var getRequest = service.Files.Get(preferredFileId);
                getRequest.Fields = "id, name, modifiedTime";
                var preferred = await getRequest.ExecuteAsync(cancellationToken);
                if (preferred?.Parents?.Any(parent => string.Equals(parent, parentFolderId, StringComparison.Ordinal)) == true)
                {
                    return preferred;
                }
            }
            catch (GoogleApiException exception) when (
                exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound ||
                exception.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Fallback to name lookup below.
            }
        }

        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, modifiedTime)";
        listRequest.PageSize = 1;
        listRequest.OrderBy = "modifiedTime desc";
        listRequest.Q =
            $"name = '{EscapeDriveQueryValue(AppConfig.GoogleDriveConfigFileName)}' and trashed = false and '{EscapeDriveQueryValue(parentFolderId)}' in parents";

        var response = await listRequest.ExecuteAsync(cancellationToken);
        return response.Files?.FirstOrDefault();
    }

    private static async Task<Google.Apis.Drive.v3.Data.File?> ResolveLegacyAppDataFileAsync(
        DriveService service,
        string? preferredFileId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredFileId))
        {
            try
            {
                var getRequest = service.Files.Get(preferredFileId);
                getRequest.Fields = "id, name, modifiedTime";
                var preferred = await getRequest.ExecuteAsync(cancellationToken);
                if (preferred is not null)
                {
                    return preferred;
                }
            }
            catch (GoogleApiException exception) when (
                exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound ||
                exception.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Fallback to legacy lookup below.
            }
        }

        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, modifiedTime)";
        listRequest.PageSize = 1;
        listRequest.OrderBy = "modifiedTime desc";
        listRequest.Q = $"name = '{EscapeDriveQueryValue(AppConfig.GoogleDriveConfigFileName)}' and trashed = false";
        listRequest.Spaces = "appDataFolder";

        var response = await listRequest.ExecuteAsync(cancellationToken);
        return response.Files?.FirstOrDefault();
    }

    private static async Task<string> EnsureVisibleAppFolderIdAsync(DriveService service, CancellationToken cancellationToken)
    {
        var appsRootId = await EnsureFolderAsync(
            service,
            parentId: "root",
            AppConfig.GoogleDriveVisibleRootFolderName,
            cancellationToken);

        var appFolderId = await EnsureFolderAsync(
            service,
            parentId: appsRootId,
            AppConfig.GoogleDriveVisibleAppFolderName,
            cancellationToken);

        return appFolderId;
    }

    private static async Task<string> EnsureFolderAsync(
        DriveService service,
        string parentId,
        string folderName,
        CancellationToken cancellationToken)
    {
        var escapedName = EscapeDriveQueryValue(folderName);
        var escapedParent = EscapeDriveQueryValue(parentId);

        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, createdTime)";
        listRequest.PageSize = 20;
        listRequest.OrderBy = "createdTime asc";
        listRequest.Q =
            $"mimeType = '{FolderMimeType}' and name = '{escapedName}' and trashed = false and '{escapedParent}' in parents";

        var existing = await listRequest.ExecuteAsync(cancellationToken);
        var folder = existing.Files?.FirstOrDefault();
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Id))
        {
            return folder.Id;
        }

        var metadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = folderName,
            MimeType = FolderMimeType,
            Parents = [parentId]
        };

        var createRequest = service.Files.Create(metadata);
        createRequest.Fields = "id";
        var created = await createRequest.ExecuteAsync(cancellationToken);
        if (created is null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidOperationException($"Impossible de creer le dossier Google Drive '{folderName}'.");
        }

        return created.Id;
    }
}

