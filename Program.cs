namespace WinGemini;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var startupState = AppStateStore.Load();
        UiLanguageService.Apply(startupState.UiLanguageCode);
        AppLogger.SetDebugLoggingEnabled(startupState.EnableDebugLogs);
        AppLogger.Debug("Application startup initiated.");
        RegisterGlobalExceptionLogging();

        try
        {
            ApplicationConfiguration.Initialize();
            AppLogger.Debug("ApplicationConfiguration initialized.");

            using var loginForm = new LoginForm();
            AppLogger.Debug("LoginForm opened.");
            if (loginForm.ShowDialog() != DialogResult.OK)
            {
                AppLogger.Debug("Login canceled. Exiting.");
                return;
            }

            AppLogger.Debug("Login succeeded. Launching MainForm.");
            Application.Run(new MainForm());
            AppLogger.Debug("Application.Run returned. Exiting.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Fatal exception in Program.Main.", exception);
            throw;
        }
    }

    private static void RegisterGlobalExceptionLogging()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
            AppLogger.Error("Unhandled UI thread exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Error(
                $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}",
                args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }
}
