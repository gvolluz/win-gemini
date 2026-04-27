using System.Net;
using System.Text;
using System.Text.Json;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace WinGeminiWrapper;

internal static class GoogleDriveConfigSyncService
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

    internal static bool TryExtractClientSecretsFromFile(
        string credentialsJsonPath,
        out string clientId,
        out string clientSecret,
        out string error)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(credentialsJsonPath))
        {
            error = "Le chemin du fichier de credentials est vide.";
            return false;
        }

        if (!File.Exists(credentialsJsonPath))
        {
            error = $"Fichier introuvable: {credentialsJsonPath}";
            return false;
        }

        try
        {
            var json = File.ReadAllText(credentialsJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var candidate = root;
            if (root.TryGetProperty("installed", out var installed))
            {
                candidate = installed;
            }
            else if (root.TryGetProperty("web", out var web))
            {
                candidate = web;
            }

            if (!candidate.TryGetProperty("client_id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                error = "client_id introuvable dans le fichier de credentials.";
                return false;
            }

            if (!candidate.TryGetProperty("client_secret", out var secretProp) || secretProp.ValueKind != JsonValueKind.String)
            {
                error = "client_secret introuvable dans le fichier de credentials.";
                return false;
            }

            clientId = idProp.GetString() ?? string.Empty;
            clientSecret = secretProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                error = "client_id/client_secret invalides dans le fichier de credentials.";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static bool TryLoadClientSecretsFromDefaultLocations(
        out string clientId,
        out string clientSecret,
        out string sourcePath,
        out string error)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;
        sourcePath = string.Empty;
        error = string.Empty;

        var candidates = new[]
        {
            AppConfig.GoogleDriveOAuthClientJsonPath,
            Path.Combine(AppContext.BaseDirectory, "google-oauth-client.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "google-oauth-client.json"),
            Path.Combine(AppContext.BaseDirectory, "credentials.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "credentials.json")
        };

        foreach (var candidate in candidates
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => Path.GetFullPath(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (TryExtractClientSecretsFromFile(candidate, out clientId, out clientSecret, out error))
            {
                sourcePath = candidate;
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            error = "Aucun fichier credentials OAuth trouve automatiquement.";
        }

        return false;
    }

    internal static async Task<GoogleDriveAuthorizationResult> AuthorizeInteractiveAsync(
        string clientId,
        string clientSecret,
        string? preferredConfigFileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return GoogleDriveAuthorizationResult.Failure("Les credentials OAuth sont incomplets.");
        }

        try
        {
            return await ExecuteWithServiceRetryAsync(
                clientId,
                clientSecret,
                cancellationToken,
                async service =>
                {
                    var aboutRequest = service.About.Get();
                    aboutRequest.Fields = "user(displayName,emailAddress)";
                    var about = await aboutRequest.ExecuteAsync(cancellationToken);

                    var appFolderId = await EnsureVisibleAppFolderIdAsync(service, cancellationToken);
                    var existingConfig = await ResolveTargetFileAsync(service, appFolderId, preferredConfigFileId, cancellationToken);
                    return GoogleDriveAuthorizationResult.SuccessResult(
                        about.User?.DisplayName,
                        about.User?.EmailAddress,
                        existingConfig?.Id);
                });
        }
        catch (Exception exception)
        {
            return GoogleDriveAuthorizationResult.Failure(exception.Message);
        }
    }

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
                            await CreateOrReplaceGoogleDocumentFromMarkdownAsync(
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

    private static async Task<DriveService> CreateDriveServiceAsync(AppState state, CancellationToken cancellationToken)
    {
        return await CreateDriveServiceAsync(
            state.GoogleDriveClientId ?? string.Empty,
            state.GoogleDriveClientSecret ?? string.Empty,
            cancellationToken,
            forceReauthorize: false);
    }

    private static async Task<DriveService> CreateDriveServiceAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken,
        bool forceReauthorize)
    {
        EnsureTokenStoreMatchesCurrentScope(forceReauthorize);

        if (forceReauthorize)
        {
            ClearCachedGoogleDriveTokenStore();
            EnsureTokenStoreMatchesCurrentScope(forceReauthorize: false);
        }

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
            Scopes,
            "wingeminiwrapper-user",
            cancellationToken,
            new FileDataStore(AppConfig.GoogleDriveTokenStorePath, true));

        WriteCurrentScopeVersionMarker();

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = AppConfig.ProductName
        });
    }

    private static async Task<T> ExecuteWithServiceRetryAsync<T>(
        AppState state,
        CancellationToken cancellationToken,
        Func<DriveService, Task<T>> action)
    {
        return await ExecuteWithServiceRetryAsync(
            state.GoogleDriveClientId ?? string.Empty,
            state.GoogleDriveClientSecret ?? string.Empty,
            cancellationToken,
            action);
    }

    private static async Task<T> ExecuteWithServiceRetryAsync<T>(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken,
        Func<DriveService, Task<T>> action)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var forceReauthorize = attempt > 0;
            DriveService? service = null;
            try
            {
                service = await CreateDriveServiceAsync(clientId, clientSecret, cancellationToken, forceReauthorize);
                return await action(service);
            }
            catch (Exception exception) when (!forceReauthorize && ShouldRetryWithTokenReset(exception))
            {
                lastException = exception;
                continue;
            }
            finally
            {
                service?.Dispose();
            }
        }

        throw lastException ?? new InvalidOperationException("Google Drive request failed.");
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
                exception.HttpStatusCode == HttpStatusCode.NotFound ||
                exception.HttpStatusCode == HttpStatusCode.Forbidden)
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
                exception.HttpStatusCode == HttpStatusCode.NotFound ||
                exception.HttpStatusCode == HttpStatusCode.Forbidden)
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

        var mimeType = MarkdownMimeType;
        await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (existingFile is null || string.IsNullOrWhiteSpace(existingFile.Id))
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = fileName,
                Parents = [parentFolderId]
            };

            var createRequest = service.Files.Create(metadata, stream, mimeType);
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
        var updateRequest = service.Files.Update(updateMetadata, existingFile.Id, stream, mimeType);
        updateRequest.Fields = "id, modifiedTime";
        var updateResult = await updateRequest.UploadAsync(cancellationToken);
        if (updateResult.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw updateResult.Exception ?? new InvalidOperationException("Google Drive markdown update failed.");
        }
    }

    private static async Task CreateOrReplaceGoogleDocumentFromMarkdownAsync(
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
        listRequest.Fields = "files(id)";
        listRequest.PageSize = 100;
        listRequest.Q =
            $"mimeType = '{GoogleDocumentMimeType}' and name = '{escapedName}' and trashed = false and '{escapedParent}' in parents";

        var existing = await listRequest.ExecuteAsync(cancellationToken);
        foreach (var existingDoc in existing.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(existingDoc.Id))
            {
                continue;
            }

            try
            {
                await service.Files.Delete(existingDoc.Id).ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Already removed; continue.
            }
        }

        await using var markdownStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = googleDocName,
            MimeType = GoogleDocumentMimeType,
            Parents = [parentFolderId]
        };

        var createRequest = service.Files.Create(metadata, markdownStream, MarkdownMimeType);
        createRequest.Fields = "id, modifiedTime";
        var createResult = await createRequest.UploadAsync(cancellationToken);
        if (createResult.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw createResult.Exception ?? new InvalidOperationException("Google Doc conversion failed.");
        }
    }

    private static void ClearCachedGoogleDriveTokenStore()
    {
        try
        {
            if (Directory.Exists(AppConfig.GoogleDriveTokenStorePath))
            {
                Directory.Delete(AppConfig.GoogleDriveTokenStorePath, recursive: true);
            }
        }
        catch
        {
            // Best effort reset only.
        }
    }

    private static bool ShouldRetryWithTokenReset(Exception exception)
    {
        if (exception is TokenResponseException)
        {
            return true;
        }

        if (exception is GoogleApiException apiException)
        {
            if (apiException.HttpStatusCode == HttpStatusCode.Unauthorized ||
                apiException.HttpStatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            var reasons = apiException.Error?.Errors ?? [];
            foreach (var reason in reasons)
            {
                var value = reason?.Reason ?? string.Empty;
                if (value.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("auth", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void EnsureTokenStoreMatchesCurrentScope(bool forceReauthorize)
    {
        Directory.CreateDirectory(AppConfig.GoogleDriveTokenStorePath);
        if (forceReauthorize)
        {
            return;
        }

        try
        {
            if (File.Exists(TokenScopeVersionFilePath))
            {
                var savedVersion = File.ReadAllText(TokenScopeVersionFilePath).Trim();
                if (string.Equals(savedVersion, AppConfig.GoogleDriveTokenScopeVersion, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
        catch
        {
            // If marker is unreadable, reset token store.
        }

        ClearCachedGoogleDriveTokenStore();
        Directory.CreateDirectory(AppConfig.GoogleDriveTokenStorePath);
        WriteCurrentScopeVersionMarker();
    }

    private static void WriteCurrentScopeVersionMarker()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.GoogleDriveTokenStorePath);
            File.WriteAllText(TokenScopeVersionFilePath, AppConfig.GoogleDriveTokenScopeVersion);
        }
        catch
        {
            // Marker is best effort only.
        }
    }

    private static string EscapeDriveQueryValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}

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
