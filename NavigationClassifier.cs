namespace WinGeminiWrapper;

internal static class NavigationClassifier
{
    private static readonly string[] LoginHosts =
    {
        "accounts.google.com",
        "myaccount.google.com",
        "ogs.google.com"
    };

    internal static bool IsGeminiApp(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        if (!uri.Host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.Equals("/app", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.StartsWith("/app/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNotebookLmApp(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        return uri.Host.Equals("notebooklm.google.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSupportedApp(Uri? uri) => IsGeminiApp(uri) || IsNotebookLmApp(uri);

    internal static bool IsUriForApp(Uri? uri, WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => IsNotebookLmApp(uri),
            _ => IsGeminiApp(uri)
        };

    internal static bool RequiresSignIn(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        foreach (var loginHost in LoginHosts)
        {
            if (uri.Host.Equals(loginHost, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith($".{loginHost}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var absoluteUri = uri.AbsoluteUri;
        return absoluteUri.Contains("ServiceLogin", StringComparison.OrdinalIgnoreCase) ||
               absoluteUri.Contains("signin", StringComparison.OrdinalIgnoreCase) ||
               absoluteUri.Contains("oauth", StringComparison.OrdinalIgnoreCase);
    }
}
