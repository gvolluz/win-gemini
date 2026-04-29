namespace WinGemini;

internal sealed partial class MainForm
{
    private sealed record SelectedEvernoteNotebookForExport(
        string NotebookId,
        string NotebookName,
        string StackId,
        string StackName,
        string ExportFileBaseName);

    private sealed record EvernoteExportGroupWorkItem(
        string ExportFileBaseName,
        IReadOnlyList<string> NotebookIds);

    private sealed record EvernoteExportProgressState(
        int CompletedSteps,
        string Message);

    private enum EvernoteTreeNodeKind
    {
        Stack,
        Notebook
    }

    private sealed record EvernoteTreeNodeTag(
        EvernoteTreeNodeKind Kind,
        string Id,
        string Name,
        int ItemCount = 0,
        long? LatestChangeMs = null);
}

