using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace WinGemini;

internal static partial class GoogleDriveConfigSyncService
{
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
}

