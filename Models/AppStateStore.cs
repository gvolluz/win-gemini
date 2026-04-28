using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinGeminiWrapper;

internal sealed class AppState
{
    internal const int DefaultEvernotePollingIntervalMinutes = 1;
    internal const int DefaultMaxMarkdownFilesToKeep = 20;

    [JsonInclude]
    internal string? LastGeminiUrl { get; set; }

    [JsonInclude]
    internal string? LastNotebookLmUrl { get; set; }

    [JsonInclude]
    internal string? LastGoogleDriveUrl { get; set; }

    [JsonInclude]
    internal string? EvernoteLocalDbPath { get; set; }

    [JsonInclude]
    internal List<string> SelectedEvernoteStackIds { get; set; } = [];

    [JsonInclude]
    internal List<string> SelectedEvernoteNotebookIds { get; set; } = [];

    [JsonInclude]
    internal List<string> IgnoredEvernoteStackIds { get; set; } = [];

    [JsonInclude]
    internal List<string> IgnoredEvernoteNotebookIds { get; set; } = [];

    [JsonInclude]
    internal List<EvernoteExportFileAssignmentState> EvernoteStackExportFileAssignments { get; set; } = [];

    [JsonInclude]
    internal List<EvernoteExportFileAssignmentState> EvernoteNotebookExportFileAssignments { get; set; } = [];

    [JsonInclude]
    internal List<EvernoteContainerNoteSnapshotState> EvernoteStackNoteSnapshots { get; set; } = [];

    [JsonInclude]
    internal List<EvernoteContainerNoteSnapshotState> EvernoteNotebookNoteSnapshots { get; set; } = [];

    [JsonInclude]
    internal bool EvernoteSnapshotInitialized { get; set; }

    [JsonInclude]
    internal bool EvernotePollingPaused { get; set; }

    [JsonInclude]
    internal int EvernotePollingIntervalMinutes { get; set; } = DefaultEvernotePollingIntervalMinutes;

    [JsonInclude]
    internal int MaxMarkdownFilesToKeep { get; set; } = DefaultMaxMarkdownFilesToKeep;

    [JsonInclude]
    internal bool EvernoteShowIgnoredItems { get; set; }

    [JsonInclude]
    internal bool GoogleDriveSyncEnabled { get; set; }

    [JsonInclude]
    internal bool GoogleDriveAutoRestoreOnStartup { get; set; }

    [JsonInclude]
    internal string? GoogleDriveClientId { get; set; }

    [JsonInclude]
    internal string? GoogleDriveClientSecret { get; set; }

    [JsonInclude]
    internal string? GoogleDriveConfigFileId { get; set; }

    [JsonInclude]
    internal WrappedApp LastSelectedApp { get; set; } = AppConfig.DefaultApp;

    [JsonInclude]
    internal CloseButtonBehavior CloseButtonBehavior { get; set; } = CloseButtonBehavior.MinimizeToTray;

    [JsonInclude]
    internal SavedWindowState LastWindowState { get; set; } = SavedWindowState.Normal;

    [JsonInclude]
    internal int? WindowX { get; set; }

    [JsonInclude]
    internal int? WindowY { get; set; }

    [JsonInclude]
    internal int? WindowWidth { get; set; }

    [JsonInclude]
    internal int? WindowHeight { get; set; }

    internal string? GetLastUrl(WrappedApp app) =>
        app switch
        {
            WrappedApp.NotebookLm => LastNotebookLmUrl,
            WrappedApp.GoogleDrive => LastGoogleDriveUrl,
            WrappedApp.EvernoteExport => null,
            _ => LastGeminiUrl
        };

    internal void SetLastUrl(WrappedApp app, string? url)
    {
        if (app == WrappedApp.NotebookLm)
        {
            LastNotebookLmUrl = url;
            return;
        }

        if (app == WrappedApp.GoogleDrive)
        {
            LastGoogleDriveUrl = url;
            return;
        }

        if (app == WrappedApp.EvernoteExport)
        {
            return;
        }

        LastGeminiUrl = url;
    }

    internal bool IsEvernoteStackSelected(string stackId)
    {
        SelectedEvernoteStackIds ??= [];
        return SelectedEvernoteStackIds.Any(id => string.Equals(id, stackId, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetEvernoteStackSelection(string stackId, bool isSelected)
    {
        SelectedEvernoteStackIds ??= [];
        UpdateEvernoteSelectionList(SelectedEvernoteStackIds, stackId, isSelected);
    }

    internal bool IsEvernoteNotebookSelected(string notebookId)
    {
        SelectedEvernoteNotebookIds ??= [];
        return SelectedEvernoteNotebookIds.Any(id => string.Equals(id, notebookId, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetEvernoteNotebookSelection(string notebookId, bool isSelected)
    {
        SelectedEvernoteNotebookIds ??= [];
        UpdateEvernoteSelectionList(SelectedEvernoteNotebookIds, notebookId, isSelected);
    }

    internal bool IsEvernoteStackIgnored(string stackId)
    {
        IgnoredEvernoteStackIds ??= [];
        return IgnoredEvernoteStackIds.Any(id => string.Equals(id, stackId, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetEvernoteStackIgnored(string stackId, bool ignored)
    {
        IgnoredEvernoteStackIds ??= [];
        UpdateEvernoteSelectionList(IgnoredEvernoteStackIds, stackId, ignored);
    }

    internal bool IsEvernoteNotebookIgnored(string notebookId)
    {
        IgnoredEvernoteNotebookIds ??= [];
        return IgnoredEvernoteNotebookIds.Any(id => string.Equals(id, notebookId, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetEvernoteNotebookIgnored(string notebookId, bool ignored)
    {
        IgnoredEvernoteNotebookIds ??= [];
        UpdateEvernoteSelectionList(IgnoredEvernoteNotebookIds, notebookId, ignored);
    }

    internal string? GetEvernoteStackExportFileName(string stackId)
    {
        EvernoteStackExportFileAssignments ??= [];
        return GetExportAssignment(EvernoteStackExportFileAssignments, stackId);
    }

    internal void SetEvernoteStackExportFileName(string stackId, string? exportFileName)
    {
        EvernoteStackExportFileAssignments ??= [];
        UpdateExportAssignment(EvernoteStackExportFileAssignments, stackId, exportFileName);
    }

    internal string? GetEvernoteNotebookExportFileName(string notebookId)
    {
        EvernoteNotebookExportFileAssignments ??= [];
        return GetExportAssignment(EvernoteNotebookExportFileAssignments, notebookId);
    }

    internal void SetEvernoteNotebookExportFileName(string notebookId, string? exportFileName)
    {
        EvernoteNotebookExportFileAssignments ??= [];
        UpdateExportAssignment(EvernoteNotebookExportFileAssignments, notebookId, exportFileName);
    }

    internal void SetEvernoteTrackingSnapshot(
        IReadOnlyCollection<EvernoteContainerNoteSnapshotState> stackSnapshots,
        IReadOnlyCollection<EvernoteContainerNoteSnapshotState> notebookSnapshots)
    {
        EvernoteStackNoteSnapshots = CloneContainerSnapshots(stackSnapshots);
        EvernoteNotebookNoteSnapshots = CloneContainerSnapshots(notebookSnapshots);
        EvernoteSnapshotInitialized = true;
    }

    internal Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> GetEvernoteNotebookSnapshotMap()
    {
        EvernoteNotebookNoteSnapshots ??= [];
        return BuildNoteSnapshotMap(EvernoteNotebookNoteSnapshots);
    }

    internal void Normalize()
    {
        SelectedEvernoteStackIds ??= [];
        SelectedEvernoteNotebookIds ??= [];
        IgnoredEvernoteStackIds ??= [];
        IgnoredEvernoteNotebookIds ??= [];
        EvernoteStackExportFileAssignments ??= [];
        EvernoteNotebookExportFileAssignments ??= [];
        EvernoteStackNoteSnapshots ??= [];
        EvernoteNotebookNoteSnapshots ??= [];

        EvernoteStackExportFileAssignments = NormalizeExportAssignmentList(EvernoteStackExportFileAssignments);
        EvernoteNotebookExportFileAssignments = NormalizeExportAssignmentList(EvernoteNotebookExportFileAssignments);

        if (EvernotePollingIntervalMinutes <= 0)
        {
            EvernotePollingIntervalMinutes = DefaultEvernotePollingIntervalMinutes;
        }

        if (MaxMarkdownFilesToKeep <= 0)
        {
            MaxMarkdownFilesToKeep = DefaultMaxMarkdownFilesToKeep;
        }

        GoogleDriveClientId = NormalizeOptionalString(GoogleDriveClientId);
        GoogleDriveClientSecret = NormalizeOptionalString(GoogleDriveClientSecret);
        GoogleDriveConfigFileId = NormalizeOptionalString(GoogleDriveConfigFileId);
    }

    private static void UpdateEvernoteSelectionList(List<string> target, string id, bool isSelected)
    {
        var existingIndex = target.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (isSelected)
        {
            if (existingIndex < 0)
            {
                target.Add(id);
            }

            return;
        }

        if (existingIndex >= 0)
        {
            target.RemoveAt(existingIndex);
        }
    }

    private static string? GetExportAssignment(
        List<EvernoteExportFileAssignmentState> assignments,
        string containerId)
    {
        var assignment = assignments.FirstOrDefault(entry =>
            string.Equals(entry.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));
        return NormalizeOptionalString(assignment?.ExportFileName);
    }

    private static void UpdateExportAssignment(
        List<EvernoteExportFileAssignmentState> assignments,
        string containerId,
        string? exportFileName)
    {
        var normalizedId = NormalizeOptionalString(containerId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        var normalizedFileName = NormalizeOptionalString(exportFileName);
        var existingIndex = assignments.FindIndex(entry =>
            string.Equals(entry.ContainerId, normalizedId, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            if (existingIndex >= 0)
            {
                assignments.RemoveAt(existingIndex);
            }

            return;
        }

        if (existingIndex >= 0)
        {
            assignments[existingIndex].ContainerId = normalizedId;
            assignments[existingIndex].ExportFileName = normalizedFileName;
            return;
        }

        assignments.Add(new EvernoteExportFileAssignmentState
        {
            ContainerId = normalizedId,
            ExportFileName = normalizedFileName
        });
    }

    private static List<EvernoteExportFileAssignmentState> NormalizeExportAssignmentList(
        IEnumerable<EvernoteExportFileAssignmentState> source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source ?? [])
        {
            var containerId = NormalizeOptionalString(item.ContainerId);
            var exportFileName = NormalizeOptionalString(item.ExportFileName);
            if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(exportFileName))
            {
                continue;
            }

            normalized[containerId] = exportFileName;
        }

        return normalized
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new EvernoteExportFileAssignmentState
            {
                ContainerId = entry.Key,
                ExportFileName = entry.Value
            })
            .ToList();
    }

    private static List<EvernoteContainerNoteSnapshotState> CloneContainerSnapshots(
        IReadOnlyCollection<EvernoteContainerNoteSnapshotState> source)
    {
        return source
            .Select(container => new EvernoteContainerNoteSnapshotState
            {
                ContainerId = container.ContainerId,
                Notes = (container.Notes ?? [])
                    .Select(note => new EvernoteNoteSnapshotState
                    {
                        NoteId = note.NoteId,
                        CreatedMs = note.CreatedMs,
                        UpdatedMs = note.UpdatedMs
                    })
                    .ToList()
            })
            .ToList();
    }

    private static Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> BuildNoteSnapshotMap(
        IEnumerable<EvernoteContainerNoteSnapshotState> containers)
    {
        var outerMap = new Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var containerId = container.ContainerId ?? string.Empty;
            if (!outerMap.TryGetValue(containerId, out var noteMap))
            {
                noteMap = new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
                outerMap[containerId] = noteMap;
            }

            foreach (var note in container.Notes ?? [])
            {
                if (string.IsNullOrWhiteSpace(note.NoteId))
                {
                    continue;
                }

                noteMap[note.NoteId] = new EvernoteNoteSnapshotState
                {
                    NoteId = note.NoteId,
                    CreatedMs = note.CreatedMs,
                    UpdatedMs = note.UpdatedMs
                };
            }
        }

        return outerMap;
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

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
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
        foreach (var path in GetLoadPaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var state = Deserialize(json);

                if (!string.Equals(path, AppConfig.LocalConfigFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    Save(state);
                }

                return state;
            }
            catch
            {
                // Continue with fallback files.
            }
        }

        return CreateDefaultState();
    }

    internal static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.AppDataRootFolder);
            var json = Serialize(state);
            File.WriteAllText(AppConfig.LocalConfigFilePath, json);
            if (!string.Equals(AppConfig.LocalConfigFilePath, AppConfig.LegacyStateFilePath, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(AppConfig.LegacyStateFilePath, json);
            }
        }
        catch
        {
            // Best-effort state persistence only.
        }
    }

    internal static string Serialize(AppState state)
    {
        state.Normalize();
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    internal static AppState Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        state.Normalize();
        return state;
    }

    internal static bool TryDeserialize(string json, out AppState state)
    {
        try
        {
            state = Deserialize(json);
            return true;
        }
        catch
        {
            state = CreateDefaultState();
            return false;
        }
    }

    private static IEnumerable<string> GetLoadPaths()
    {
        yield return AppConfig.LocalConfigFilePath;
        if (!string.Equals(AppConfig.LocalConfigFilePath, AppConfig.LegacyStateFilePath, StringComparison.OrdinalIgnoreCase))
        {
            yield return AppConfig.LegacyStateFilePath;
        }
    }

    private static AppState CreateDefaultState()
    {
        var state = new AppState();
        state.Normalize();
        return state;
    }
}

internal sealed class EvernoteNoteSnapshotState
{
    [JsonInclude]
    internal string NoteId { get; set; } = string.Empty;

    [JsonInclude]
    internal long? CreatedMs { get; set; }

    [JsonInclude]
    internal long? UpdatedMs { get; set; }
}

internal sealed class EvernoteContainerNoteSnapshotState
{
    [JsonInclude]
    internal string ContainerId { get; set; } = string.Empty;

    [JsonInclude]
    internal List<EvernoteNoteSnapshotState> Notes { get; set; } = [];
}

internal sealed class EvernoteExportFileAssignmentState
{
    [JsonInclude]
    internal string ContainerId { get; set; } = string.Empty;

    [JsonInclude]
    internal string ExportFileName { get; set; } = string.Empty;
}
