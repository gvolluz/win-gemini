namespace WinGemini;

internal interface IEvernoteChangeDetectionService
{
    EvernoteNotebookChangeSummary AnalyzeChangedNotes(
        IReadOnlyCollection<string> monitoredNotebookIds,
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> previousMap,
        IReadOnlyCollection<EvernoteContainerNoteSnapshotState> currentNotebookSnapshots);
}

internal sealed record EvernoteNotebookChangeSummary(
    int NoteChangeCount,
    IReadOnlySet<string> ChangedNotebookIds);

