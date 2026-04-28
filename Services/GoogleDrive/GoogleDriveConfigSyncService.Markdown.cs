using Google.Apis.Drive.v3;

namespace WinGeminiWrapper;

internal static partial class GoogleDriveConfigSyncService
{
    internal static async Task<GoogleDriveMarkdownSyncResult> SyncEvernoteMarkdownFilesAsync(
        AppState state,
        IReadOnlyCollection<EvernoteDriveFileUploadItem> files,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(state))
        {
            return GoogleDriveMarkdownSyncResult.Failure("Google Drive sync is not configured.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                state,
                cancellationToken,
                async service =>
                {
                    var exportFolderId = await EnsureFolderAsync(
                        service,
                        parentId: "root",
                        AppConfig.EvernoteExportRootFolderName,
                        cancellationToken);
                    var backupsFolderId = await EnsureFolderAsync(
                        service,
                        parentId: exportFolderId,
                        AppConfig.EvernoteExportBackupsFolderName,
                        cancellationToken);

                    var uploadedCount = 0;
                    var convertedDocCount = 0;
                    foreach (var file in files ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(file.LocalFilePath) || !File.Exists(file.LocalFilePath))
                        {
                            continue;
                        }

                        var targetParentFolderId = file.IsBackup ? backupsFolderId : exportFolderId;
                        await UploadOrUpdateFileAsync(service, targetParentFolderId, file.LocalFilePath, cancellationToken);
                        uploadedCount++;

                        if (file.ConvertToGoogleDoc)
                        {
                            await UploadOrUpdateGoogleDocumentFromMarkdownAsync(
                                service,
                                targetParentFolderId,
                                file.LocalFilePath,
                                cancellationToken);
                            convertedDocCount++;
                        }
                    }

                    return GoogleDriveMarkdownSyncResult.SuccessResult(uploadedCount, convertedDocCount);
                });
        }
        catch (Exception exception)
        {
            return GoogleDriveMarkdownSyncResult.Failure(exception.Message);
        }
    }

    private static async Task UploadOrUpdateFileAsync(
        DriveService service,
        string parentFolderId,
        string localFilePath,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(localFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var escapedName = EscapeDriveQueryValue(fileName);
        var escapedParent = EscapeDriveQueryValue(parentFolderId);

        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, name, createdTime)";
        listRequest.PageSize = 1;
        listRequest.OrderBy = "createdTime asc";
        listRequest.Q = $"name = '{escapedName}' and trashed = false and '{escapedParent}' in parents";
        var existing = await listRequest.ExecuteAsync(cancellationToken);
        var existingFile = existing.Files?.FirstOrDefault();

        await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (existingFile is null || string.IsNullOrWhiteSpace(existingFile.Id))
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = fileName,
                Parents = [parentFolderId]
            };

            var createRequest = service.Files.Create(metadata, stream, MarkdownMimeType);
            createRequest.Fields = "id, modifiedTime";
            var createResult = await createRequest.UploadAsync(cancellationToken);
            if (createResult.Status != Google.Apis.Upload.UploadStatus.Completed)
            {
                throw createResult.Exception ?? new InvalidOperationException("Google Drive markdown create failed.");
            }

            return;
        }

        var updateMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName
        };
        var updateRequest = service.Files.Update(updateMetadata, existingFile.Id, stream, MarkdownMimeType);
        updateRequest.Fields = "id, modifiedTime";
        var updateResult = await updateRequest.UploadAsync(cancellationToken);
        if (updateResult.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw updateResult.Exception ?? new InvalidOperationException("Google Drive markdown update failed.");
        }
    }

    private static async Task UploadOrUpdateGoogleDocumentFromMarkdownAsync(
        DriveService service,
        string parentFolderId,
        string localFilePath,
        CancellationToken cancellationToken)
    {
        var googleDocName = Path.GetFileNameWithoutExtension(localFilePath);
        if (string.IsNullOrWhiteSpace(googleDocName))
        {
            return;
        }

        var escapedName = EscapeDriveQueryValue(googleDocName);
        var escapedParent = EscapeDriveQueryValue(parentFolderId);
        var listRequest = service.Files.List();
        listRequest.Fields = "files(id, createdTime)";
        listRequest.PageSize = 20;
        listRequest.OrderBy = "createdTime asc";
        listRequest.Q =
            $"mimeType = '{GoogleDocumentMimeType}' and name = '{escapedName}' and trashed = false and '{escapedParent}' in parents";

        var existing = await listRequest.ExecuteAsync(cancellationToken);
        var existingDoc = existing.Files?.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Id));

        await using var markdownStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (existingDoc is not null && !string.IsNullOrWhiteSpace(existingDoc.Id))
        {
            var updateMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = googleDocName
            };
            var updateRequest = service.Files.Update(updateMetadata, existingDoc.Id, markdownStream, MarkdownMimeType);
            updateRequest.Fields = "id, modifiedTime";
            var updateResult = await updateRequest.UploadAsync(cancellationToken);
            if (updateResult.Status != Google.Apis.Upload.UploadStatus.Completed)
            {
                throw updateResult.Exception ?? new InvalidOperationException("Google Doc update failed.");
            }

            return;
        }

        var createMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = googleDocName,
            MimeType = GoogleDocumentMimeType,
            Parents = [parentFolderId]
        };

        var createRequest = service.Files.Create(createMetadata, markdownStream, MarkdownMimeType);
        createRequest.Fields = "id, modifiedTime";
        var createResult = await createRequest.UploadAsync(cancellationToken);
        if (createResult.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw createResult.Exception ?? new InvalidOperationException("Google Doc conversion failed.");
        }
    }
}
