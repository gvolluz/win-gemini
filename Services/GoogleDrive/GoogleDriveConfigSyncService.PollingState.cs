using System.Text;
using System.Text.Json;
using Google.Apis.Drive.v3;

namespace WinGeminiWrapper;

internal static partial class GoogleDriveConfigSyncService
{
    private static readonly JsonSerializerOptions PollingStateJsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<GoogleDrivePollingStateListResult> ListPollingStatesAsync(
        AppState state,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDrivePollingStateListResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var listRequest = service.Files.List();
                    listRequest.Fields = "files(id, name, modifiedTime)";
                    listRequest.PageSize = 200;
                    listRequest.Q =
                        $"trashed = false and '{EscapeDriveQueryValue(appFolderId)}' in parents and name contains '{EscapeDriveQueryValue(AppConfig.GoogleDrivePollingStateFilePrefix)}'";

                    var response = await listRequest.ExecuteAsync(cancellationToken);
                    var files = response.Files ?? [];
                    var result = new List<GoogleDrivePollingStateFile>();
                    foreach (var file in files)
                    {
                        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Name))
                        {
                            continue;
                        }

                        if (!file.Name.StartsWith(AppConfig.GoogleDrivePollingStateFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                            !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var document = await DownloadPollingStateDocumentAsync(service, file.Id, cancellationToken);
                        if (document is null || string.IsNullOrWhiteSpace(document.InstanceId))
                        {
                            continue;
                        }

                        result.Add(new GoogleDrivePollingStateFile
                        {
                            FileId = file.Id,
                            FileName = file.Name,
                            ModifiedTimeUtc = file.ModifiedTimeDateTimeOffset,
                            Document = document
                        });
                    }

                    return GoogleDrivePollingStateListResult.SuccessResult(result);
                });
        }
        catch (Exception exception)
        {
            return GoogleDrivePollingStateListResult.Failure(exception.Message);
        }
    }

    internal static async Task<GoogleDrivePollingStateMetaListResult> ListPollingStateMetasAsync(
        AppState state,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDrivePollingStateMetaListResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var metas = await ListPollingStateFilesMetadataAsync(service, appFolderId, cancellationToken);
                    return GoogleDrivePollingStateMetaListResult.SuccessResult(metas);
                });
        }
        catch (Exception exception)
        {
            return GoogleDrivePollingStateMetaListResult.Failure(exception.Message);
        }
    }

    internal static async Task<GoogleDrivePollingStateUpsertResult> UpsertPollingStateAsync(
        AppState state,
        string fileName,
        GoogleDrivePollingStateDocument document,
        string? preferredFileId,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDrivePollingStateUpsertResult.Failure("Google Drive sync is not configured.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return GoogleDrivePollingStateUpsertResult.Failure("State file name is empty.");
        }

        if (string.IsNullOrWhiteSpace(document.InstanceId))
        {
            return GoogleDrivePollingStateUpsertResult.Failure("State instance ID is empty.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var target = await ResolvePollingStateFileAsync(service, appFolderId, fileName, preferredFileId, cancellationToken);
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, PollingStateJsonOptions));

                    if (target is null || string.IsNullOrWhiteSpace(target.Id))
                    {
                        using var createStream = new MemoryStream(payload);
                        var metadata = new Google.Apis.Drive.v3.Data.File
                        {
                            Name = fileName,
                            MimeType = ConfigMimeType,
                            Parents = [appFolderId]
                        };
                        var createRequest = service.Files.Create(metadata, createStream, ConfigMimeType);
                        createRequest.Fields = "id";
                        var upload = await createRequest.UploadAsync(cancellationToken);
                        if (upload.Status != Google.Apis.Upload.UploadStatus.Completed || createRequest.ResponseBody is null)
                        {
                            return GoogleDrivePollingStateUpsertResult.Failure(upload.Exception?.Message ?? "Upload did not complete.");
                        }

                        return GoogleDrivePollingStateUpsertResult.SuccessResult(createRequest.ResponseBody.Id);
                    }

                    using var updateStream = new MemoryStream(payload);
                    var updateMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = fileName,
                        MimeType = ConfigMimeType
                    };
                    var updateRequest = service.Files.Update(updateMetadata, target.Id, updateStream, ConfigMimeType);
                    updateRequest.Fields = "id";
                    var updated = await updateRequest.UploadAsync(cancellationToken);
                    if (updated.Status != Google.Apis.Upload.UploadStatus.Completed || updateRequest.ResponseBody is null)
                    {
                        return GoogleDrivePollingStateUpsertResult.Failure(updated.Exception?.Message ?? "Upload did not complete.");
                    }

                    return GoogleDrivePollingStateUpsertResult.SuccessResult(updateRequest.ResponseBody.Id);
                });
        }
        catch (Exception exception)
        {
            return GoogleDrivePollingStateUpsertResult.Failure(exception.Message);
        }
    }

    internal static async Task<GoogleDrivePollingStateDeleteResult> DeleteAllPollingStatesAsync(
        AppState state,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDrivePollingStateDeleteResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var metas = await ListPollingStateFilesMetadataAsync(service, appFolderId, cancellationToken);
                    var deletedCount = 0;
                    foreach (var meta in metas)
                    {
                        if (string.IsNullOrWhiteSpace(meta.FileId))
                        {
                            continue;
                        }

                        await service.Files.Delete(meta.FileId).ExecuteAsync(cancellationToken);
                        deletedCount++;
                    }

                    return GoogleDrivePollingStateDeleteResult.SuccessResult(deletedCount);
                });
        }
        catch (Exception exception)
        {
            return GoogleDrivePollingStateDeleteResult.Failure(exception.Message);
        }
    }

    private static async Task<Google.Apis.Drive.v3.Data.File?> ResolvePollingStateFileAsync(
        DriveService service,
        string parentFolderId,
        string fileName,
        string? preferredFileId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredFileId))
        {
            try
            {
                var getRequest = service.Files.Get(preferredFileId);
                getRequest.Fields = "id, name, parents";
                var preferred = await getRequest.ExecuteAsync(cancellationToken);
                if (preferred?.Parents?.Any(parent => string.Equals(parent, parentFolderId, StringComparison.Ordinal)) == true)
                {
                    return preferred;
                }
            }
            catch
            {
                // Fallback to name lookup below.
            }
        }

        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, modifiedTime)";
        listRequest.PageSize = 1;
        listRequest.OrderBy = "modifiedTime desc";
        listRequest.Q =
            $"name = '{EscapeDriveQueryValue(fileName)}' and trashed = false and '{EscapeDriveQueryValue(parentFolderId)}' in parents";

        var response = await listRequest.ExecuteAsync(cancellationToken);
        return response.Files?.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<GoogleDrivePollingStateMetaFile>> ListPollingStateFilesMetadataAsync(
        DriveService service,
        string appFolderId,
        CancellationToken cancellationToken)
    {
        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, modifiedTime)";
        listRequest.PageSize = 200;
        listRequest.Q =
            $"trashed = false and '{EscapeDriveQueryValue(appFolderId)}' in parents and name contains '{EscapeDriveQueryValue(AppConfig.GoogleDrivePollingStateFilePrefix)}'";

        var response = await listRequest.ExecuteAsync(cancellationToken);
        var files = response.Files ?? [];
        var metas = new List<GoogleDrivePollingStateMetaFile>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Name))
            {
                continue;
            }

            if (!file.Name.StartsWith(AppConfig.GoogleDrivePollingStateFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metas.Add(new GoogleDrivePollingStateMetaFile
            {
                FileId = file.Id,
                FileName = file.Name,
                ModifiedTimeUtc = file.ModifiedTimeDateTimeOffset
            });
        }

        return metas;
    }

    private static async Task<GoogleDrivePollingStateDocument?> DownloadPollingStateDocumentAsync(
        DriveService service,
        string fileId,
        CancellationToken cancellationToken)
    {
        using var contentStream = new MemoryStream();
        var getRequest = service.Files.Get(fileId);
        await getRequest.DownloadAsync(contentStream, cancellationToken);
        var json = Encoding.UTF8.GetString(contentStream.ToArray());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<GoogleDrivePollingStateDocument>(json, PollingStateJsonOptions);
        if (document is null)
        {
            return null;
        }

        document.InstanceId = (document.InstanceId ?? string.Empty).Trim();
        document.HostName = (document.HostName ?? string.Empty).Trim();
        document.DisplayName = string.IsNullOrWhiteSpace(document.DisplayName)
            ? document.HostName
            : document.DisplayName.Trim();

        return document;
    }
}
