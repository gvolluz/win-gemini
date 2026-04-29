using System.Reflection;

namespace WinGemini;

internal static class AppVersionProvider
{
    internal static readonly string DisplayVersion = ResolveDisplayVersion();
    internal static string ProductVersionLabel => $"{AppConfig.ProductName} v{DisplayVersion}";

    internal static string FormatWindowTitle(string title)
    {
        return $"{title} - {ProductVersionLabel}";
    }

    private static string ResolveDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            return plusIndex > 0
                ? informationalVersion[..plusIndex]
                : informationalVersion;
        }

        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "1.0.0";
        }

        if (version.Build >= 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"{version.Major}.{version.Minor}";
    }
}

