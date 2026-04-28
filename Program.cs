namespace WinGeminiWrapper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppLogger.Info("Application startup initiated.");
        RegisterGlobalExceptionLogging();

        try
        {
            ApplicationConfiguration.Initialize();
            AppLogger.Info("ApplicationConfiguration initialized.");

            using var loginForm = new LoginForm();
            AppLogger.Info("LoginForm opened.");
            if (loginForm.ShowDialog() != DialogResult.OK)
            {
                AppLogger.Info("Login canceled. Exiting.");
                return;
            }

            AppLogger.Info("Login succeeded. Launching MainForm.");
            Application.Run(new MainForm());
            AppLogger.Info("Application.Run returned. Exiting.");
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
