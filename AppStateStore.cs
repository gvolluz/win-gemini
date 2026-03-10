using System.Text.Json;

namespace WinGeminiWrapper;

internal sealed class AppState
{
    internal string? LastGeminiUrl { get; set; }
    internal string? LastNotebookLmUrl { get; set; }
    internal WrappedApp LastSelectedApp { get; set; } = AppConfig.DefaultApp;
    internal CloseButtonBehavior CloseButtonBehavior { get; set; } = CloseButtonBehavior.MinimizeToTray;
    internal SavedWindowState LastWindowState { get; set; } = SavedWindowState.Normal;
    internal int? WindowX { get; set; }
    internal int? WindowY { get; set; }
    internal int? WindowWidth { get; set; }
    internal int? WindowHeight { get; set; }

    internal string? GetLastUrl(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => LastNotebookLmUrl,
            _ => LastGeminiUrl
        };

    internal void SetLastUrl(WrappedApp app, string? url)
    {
        if (app == WrappedApp.NotebookLm)
        {
            LastNotebookLmUrl = url;
            return;
        }

        LastGeminiUrl = url;
    }

    internal bool TryGetWindowBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!WindowX.HasValue || !WindowY.HasValue || !WindowWidth.HasValue || !WindowHeight.HasValue)
        {
            return false;
        }

        if (WindowWidth.Value <= 0 || WindowHeight.Value <= 0)
        {
            return false;
        }

        bounds = new Rectangle(WindowX.Value, WindowY.Value, WindowWidth.Value, WindowHeight.Value);
        return true;
    }

    internal void SetWindowBounds(Rectangle bounds)
    {
        WindowX = bounds.X;
        WindowY = bounds.Y;
        WindowWidth = bounds.Width;
        WindowHeight = bounds.Height;
    }
}

internal static class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static AppState Load()
    {
        try
        {
            if (!File.Exists(AppConfig.StateFilePath))
            {
                return new AppState();
            }

            var json = File.ReadAllText(AppConfig.StateFilePath);
            return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    internal static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.AppDataRootFolder);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(AppConfig.StateFilePath, json);
        }
        catch
        {
            // Best-effort state persistence only.
        }
    }
}
