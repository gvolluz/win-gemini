namespace WinGeminiWrapper;

internal sealed class EvernoteLocalDbServiceAdapter : IEvernoteLocalDbService
{
    internal static EvernoteLocalDbServiceAdapter Instance { get; } = new();

    private EvernoteLocalDbServiceAdapter()
    {
    }

    public IReadOnlyList<EvernoteStackInfo> GetStacksAndNotebooks(string evernoteRootPath, out string resolvedDatabasePath) =>
        EvernoteLocalDbService.GetStacksAndNotebooks(evernoteRootPath, out resolvedDatabasePath);

    public EvernoteTrackingSnapshot GetTrackingSnapshot(string evernoteRootPath, out string resolvedDatabasePath) =>
        EvernoteLocalDbService.GetTrackingSnapshot(evernoteRootPath, out resolvedDatabasePath);

    public EvernoteMarkdownExportResult ExportNotebookGroupToMarkdown(
        string evernoteRootPath,
        IReadOnlyCollection<string> notebookIds,
        string exportFileBaseName,
        string exportRootDirectory,
        string exportBackupsDirectory,
        int maxBackupsToKeep) =>
        EvernoteLocalDbService.ExportNotebookGroupToMarkdown(
            evernoteRootPath,
            notebookIds,
            exportFileBaseName,
            exportRootDirectory,
            exportBackupsDirectory,
            maxBackupsToKeep);
}
