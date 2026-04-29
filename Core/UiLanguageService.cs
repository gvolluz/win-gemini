using System.Globalization;

namespace WinGemini;

internal static partial class UiLanguageService
{
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> KeyTranslations =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["App.Gemini"] = BuildTranslations("Gemini"),
            ["App.NotebookLm"] = BuildTranslations("NotebookLM"),
            ["App.GoogleDrive"] = BuildTranslations("Google Drive"),
            ["App.EvernoteExport"] = BuildTranslations("Evernote Export"),
            ["Common.Settings"] = BuildTranslations("Settings"),
            ["Common.Save"] = BuildTranslations("Save"),
            ["Common.Cancel"] = BuildTranslations("Cancel"),
            ["Main.TopBar.App"] = BuildTranslations("App:"),
            ["Main.TopBar.Refresh"] = BuildTranslations("Refresh"),
            ["Main.TopBar.LogOut"] = BuildTranslations("Log out"),
            ["Main.Tray.SwitchTo"] = BuildTranslations("Switch to"),
            ["Main.Tray.Exit"] = BuildTranslations("Exit"),
            ["Main.Tray.Balloon.Title"] = BuildTranslations("{0} is still running"),
            ["Main.Tray.Balloon.Text"] = BuildTranslations("Use the tray icon to reopen or exit."),
            ["Main.EvernoteMenu.Ignore"] = BuildTranslations("Ignore"),
            ["Main.EvernoteMenu.IgnoreKind"] = BuildTranslations("Ignore {0}"),
            ["Main.EvernoteMenu.UnignoreKind"] = BuildTranslations("Unignore {0}"),
            ["Main.EvernoteMenu.SetExportFileForKind"] = BuildTranslations("Set export file for {0}..."),
            ["Main.EvernoteMenu.ClearExportFileName"] = BuildTranslations("Clear export file name"),
            ["Main.EvernoteMenu.ClearExportFileWithName"] = BuildTranslations("Clear export file ({0})"),
            ["Main.Evernote.Kind.Stack"] = BuildTranslations("stack"),
            ["Main.Evernote.Kind.Notebook"] = BuildTranslations("notebook"),
            ["Evernote.Title"] = BuildTranslations("Evernote Export"),
            ["Evernote.Subtitle"] = BuildTranslations("Select the Evernote root folder, then export the checked notebooks."),
            ["Evernote.FolderButton"] = BuildTranslations("Evernote Folder..."),
            ["Evernote.Reload"] = BuildTranslations("Reload"),
            ["Evernote.Export"] = BuildTranslations("Export"),
            ["Evernote.PollFrequencyMinutes"] = BuildTranslations("Evernote polling frequency (minutes):"),
            ["Evernote.PauseAutomaticPolling"] = BuildTranslations("Pause automatic polling"),
            ["Evernote.LockNone"] = BuildTranslations("Lock: (none)"),
            ["Evernote.Force"] = BuildTranslations("Force"),
            ["Evernote.StatePollDefault"] = BuildTranslations("State poll: every 6s, next in --s"),
            ["Evernote.KeepLastX"] = BuildTranslations("Keep last X markdown exports:"),
            ["Evernote.KeepLastXHelp"] = BuildTranslations("Older files in ./markdown will be deleted automatically after each export."),
            ["Evernote.NoFolderSelected"] = BuildTranslations("No Evernote folder selected."),
            ["Evernote.ShowIgnored"] = BuildTranslations("Show ignored"),
            ["Evernote.RightClickHint"] = BuildTranslations("Right-click: ignore or set export file name."),
            ["Evernote.ChooseRootFolderDescription"] = BuildTranslations("Choose the root folder of the Evernote installation"),
            ["Evernote.DbDetected"] = BuildTranslations("DB detected: {0} | Stacks: {1} | Notebooks: {2}"),
            ["Evernote.DbError"] = BuildTranslations("DB error: {0}"),
            ["Evernote.UnableToReadDatabase"] = BuildTranslations("Unable to read Evernote database.{0}{0}{1}"),
            ["Evernote.Node.IgnoredSuffix"] = BuildTranslations(" [ignored]"),
            ["Evernote.Node.UpdatedSuffix"] = BuildTranslations(" | updated: {0}"),
            ["Evernote.Node.ExportSuffix"] = BuildTranslations(" | export: {0}.md"),
            ["Evernote.Node.Stack"] = BuildTranslations("{0} ({1} notebooks){2}{3}{4}"),
            ["Evernote.Node.Notebook"] = BuildTranslations("{0} ({1} notes){2}{3}{4}"),
            ["Evernote.None"] = BuildTranslations("(none)"),
            ["Evernote.LockOwner"] = BuildTranslations("Lock: {0}"),
            ["Evernote.AwaitingConfirmationFrom"] = BuildTranslations("awaiting confirmation from {0}"),
            ["Evernote.StatePoll"] = BuildTranslations("State poll: every {0}s, next in {1}s"),
            ["Evernote.ExportFileNameDialogTitle"] = BuildTranslations("Export File Name"),
            ["Evernote.ExportFileNameDialogLabel"] = BuildTranslations("Export file name for {0} (without .md):"),
            ["Evernote.ExportFileNameDialogHint"] = BuildTranslations("The same name lets you group multiple stacks/notebooks."),
            ["Evernote.InvalidExportFileName"] = BuildTranslations("Invalid name. Use at least one valid character."),
            ["Settings.LanguageLabel"] = BuildTranslations("Language"),
            ["Settings.LanguageHelp"] = BuildTranslations("Choose the UI language"),
            ["Settings.LanguageSystemDefault"] = BuildTranslations("System default"),
            ["Settings.CloseBehaviorLabel"] = BuildTranslations("Close button behavior:"),
            ["Settings.CloseBehavior.MinimizeToTray"] = BuildTranslations("Minimize to tray"),
            ["Settings.CloseBehavior.CloseApp"] = BuildTranslations("Close app"),
            ["Settings.CloseBehaviorHelp"] = BuildTranslations("Controls what happens when you click the window close button."),
            ["Settings.EnableDebugLogs"] = BuildTranslations("Enable debug logs"),
            ["Settings.LocalConfigFile"] = BuildTranslations("Local config file: {0}"),
            ["Settings.GoogleDriveSection"] = BuildTranslations("Google Drive config sync"),
            ["Settings.EnableGoogleDriveSync"] = BuildTranslations("Enable Google Drive sync"),
            ["Settings.AutoRestoreOnStartup"] = BuildTranslations("Auto-restore at startup"),
            ["Settings.GoogleOAuthHint"] = BuildTranslations("Click the button below to connect with Google OAuth."),
            ["Settings.ConnectWithGoogle"] = BuildTranslations("Connect with Google"),
            ["Settings.OAuthStatusConfigured"] = BuildTranslations("OAuth status: configured."),
            ["Settings.OAuthStatusNotConnected"] = BuildTranslations("OAuth status: not connected."),
            ["Settings.OAuthStatusConnecting"] = BuildTranslations("OAuth status: connecting..."),
            ["Settings.UnknownOAuthError"] = BuildTranslations("Unknown OAuth error."),
            ["Settings.OAuthStatusFailed"] = BuildTranslations("OAuth status: failed ({0})"),
            ["Settings.OAuthConnectionFailedMessage"] = BuildTranslations("OAuth connection failed.{0}{0}{1}"),
            ["Settings.GoogleOAuthTitle"] = BuildTranslations("Google Drive OAuth"),
            ["Settings.GoogleAccountConnectedFallback"] = BuildTranslations("Google account connected"),
            ["Settings.NotDetected"] = BuildTranslations("(not detected)"),
            ["Settings.OAuthClientSource"] = BuildTranslations("OAuth client source: {0}"),
            ["Settings.DriveFileIdOptional"] = BuildTranslations("Drive file ID (optional):"),
            ["Settings.ExportSettings"] = BuildTranslations("Export settings"),
            ["Settings.ImportSettings"] = BuildTranslations("Import settings"),
            ["Settings.SelectOAuthCredentialsJson"] = BuildTranslations("Select Google OAuth credentials JSON"),
            ["Settings.OAuthStatusCredentialsFileRequired"] = BuildTranslations("OAuth status: credentials file required."),
            ["Settings.OAuthStatusError"] = BuildTranslations("OAuth status: error ({0})"),
            ["Settings.UnableToReadOAuthCredentials"] = BuildTranslations("Unable to read OAuth credentials.{0}{0}{1}"),
            ["Settings.OAuthStatusConnected"] = BuildTranslations("OAuth status: connected ({0})."),
            ["Settings.ExportedTo"] = BuildTranslations("Settings exported to:{0}{1}"),
            ["Settings.UnableToExport"] = BuildTranslations("Unable to export settings.{0}{0}{1}"),
            ["Settings.ImportWillReplaceConfirm"] = BuildTranslations("Importing a settings file will replace your current local configuration. Continue?"),
            ["Settings.UnableToReadSelectedFile"] = BuildTranslations("Unable to read the selected file.{0}{0}{1}"),
            ["Settings.InvalidSettingsJson"] = BuildTranslations("The selected file does not contain valid settings JSON."),
            ["Settings.ImportedFrom"] = BuildTranslations("Settings imported from:{0}{1}")
        };

    private static CultureInfo _effectiveCulture = CultureInfo.CurrentUICulture;

    internal static string? CurrentLanguageCode { get; private set; }

    internal static void Apply(string? languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        CurrentLanguageCode = normalized;
        var culture = ResolveCulture(normalized);
        _effectiveCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    internal static bool IsRightToLeftCurrentLanguage()
    {
        return _effectiveCulture.TextInfo.IsRightToLeft;
    }

    internal static IReadOnlyList<UiLanguageOption> GetLanguageOptions()
    {
        var options = new List<UiLanguageOption>(UiLanguageCatalog.SupportedLanguageCodes.Length);
        foreach (var code in UiLanguageCatalog.SupportedLanguageCodes)
        {
            if (string.Equals(code, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                options.Add(new UiLanguageOption(code, T("Settings.LanguageSystemDefault")));
                continue;
            }

            var nativeName = GetNativeLanguageName(code);
            options.Add(new UiLanguageOption(code, nativeName));
        }

        return options;
    }

    internal static string T(string key)
    {
        if (!KeyTranslations.TryGetValue(key, out var translations))
        {
            return key;
        }

        var languageCode = _effectiveCulture.Name;
        if (TryGetTranslation(translations, languageCode, out var translated))
        {
            return translated;
        }

        var neutralLanguage = _effectiveCulture.TwoLetterISOLanguageName;
        if (TryGetTranslation(translations, neutralLanguage, out translated))
        {
            return translated;
        }

        if (TryGetTranslation(translations, "en", out translated))
        {
            return translated;
        }

        return key;
    }

    internal static string Tf(string key, params object[] args)
    {
        var format = T(key);
        return string.Format(_effectiveCulture, format, args);
    }

    private static bool TryGetTranslation(
        IReadOnlyDictionary<string, string> translations,
        string languageCode,
        out string translated)
    {
        translated = string.Empty;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        if (translations.TryGetValue(languageCode, out var candidate) &&
            !string.IsNullOrWhiteSpace(candidate))
        {
            translated = candidate;
            return true;
        }

        return false;
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        var trimmed = languageCode.Trim();
        if (string.Equals(trimmed, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return UiLanguageCatalog.SupportedLanguageCodes.Any(code =>
                string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : null;
    }

    private static CultureInfo ResolveCulture(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return CultureInfo.InstalledUICulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageCode);
        }
        catch
        {
            return CultureInfo.InstalledUICulture;
        }
    }

    private static string GetNativeLanguageName(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            return culture.NativeName;
        }
        catch
        {
            return languageCode;
        }
    }

    private static Dictionary<string, string> BuildTranslations(string english)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in UiLanguageCatalog.SupportedLanguageCodes)
        {
            if (!string.Equals(code, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                map[code] = english;
            }
        }

        foreach (var code in UiLanguageCatalog.SupportedLanguageCodes)
        {
            if (string.Equals(code, UiLanguageCatalog.AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[code] = Translate(code, english);
        }

        return map;
    }

    private static string Translate(string languageCode, string english)
    {
        return languageCode switch
        {
            "en" => TranslateEn(english),
            "fr" => TranslateFr(english),
            "de" => TranslateDe(english),
            "es" => TranslateEs(english),
            "it" => TranslateIt(english),
            "pt" => TranslatePt(english),
            "nl" => TranslateNl(english),
            "sv" => TranslateSv(english),
            "no" => TranslateNo(english),
            "da" => TranslateDa(english),
            "fi" => TranslateFi(english),
            "is" => TranslateIs(english),
            "ga" => TranslateGa(english),
            "cy" => TranslateCy(english),
            "eu" => TranslateEu(english),
            "ca" => TranslateCa(english),
            "gl" => TranslateGl(english),
            "pl" => TranslatePl(english),
            "cs" => TranslateCs(english),
            "sk" => TranslateSk(english),
            "sl" => TranslateSl(english),
            "hr" => TranslateHr(english),
            "sr-Latn" => TranslateSrLatn(english),
            "sr-Cyrl" => TranslateSrCyrl(english),
            "bs" => TranslateBs(english),
            "mk" => TranslateMk(english),
            "bg" => TranslateBg(english),
            "ro" => TranslateRo(english),
            "hu" => TranslateHu(english),
            "sq" => TranslateSq(english),
            "el" => TranslateEl(english),
            "tr" => TranslateTr(english),
            "lt" => TranslateLt(english),
            "lv" => TranslateLv(english),
            "et" => TranslateEt(english),
            "mt" => TranslateMt(english),
            "uk" => TranslateUk(english),
            "ru" => TranslateRu(english),
            "be" => TranslateBe(english),
            "ja" => TranslateJa(english),
            "zh-Hans" => TranslateZhHans(english),
            "zh-Hant" => TranslateZhHant(english),
            "ko" => TranslateKo(english),
            "ar" => TranslateAr(english),
            "fa" => TranslateFa(english),
            "hi" => TranslateHi(english),
            "id" => TranslateId(english),
            "bn" => TranslateBn(english),
            _ => english
        };
    }
}

internal sealed record UiLanguageOption(string Code, string NativeDisplayName);


