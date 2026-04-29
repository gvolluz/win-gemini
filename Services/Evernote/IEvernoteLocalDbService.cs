namespace WinGemini;

internal interface IEvernoteLocalDbService
{
    IReadOnlyList<EvernoteStackInfo> GetStacksAndNotebooks(string evernoteRootPath, out string resolvedDatabasePath);
    EvernoteTrackingSnapshot GetTrackingSnapshot(string evernoteRootPath, out string resolvedDatabasePath);
    EvernoteMarkdownExportResult ExportNotebookGroupToMarkdown(
        string evernoteRootPath,
        IReadOnlyCollection<string> notebookIds,
        string exportFileBaseName,
        string exportRootDirectory,
        string exportBackupsDirectory,
        int maxBackupsToKeep);
}

