namespace WinGeminiWrapper;

internal sealed class EvernoteChangeDetectionService : IEvernoteChangeDetectionService
{
    internal static EvernoteChangeDetectionService Instance { get; } = new();

    private EvernoteChangeDetectionService()
    {
    }

    public EvernoteNotebookChangeSummary AnalyzeChangedNotes(
        IReadOnlyCollection<string> monitoredNotebookIds,
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> previousMap,
        IReadOnlyCollection<EvernoteContainerNoteSnapshotState> currentNotebookSnapshots)
    {
        var currentMap = BuildNotebookSnapshotMap(currentNotebookSnapshots);
        var changeCount = 0;
        var changedNotebookIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var notebookId in monitoredNotebookIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            previousMap.TryGetValue(notebookId, out var previousNotes);
            currentMap.TryGetValue(notebookId, out var currentNotes);

            previousNotes ??= new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
            currentNotes ??= new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);

            var noteIds = new HashSet<string>(previousNotes.Keys, StringComparer.OrdinalIgnoreCase);
            noteIds.UnionWith(currentNotes.Keys);

            foreach (var noteId in noteIds)
            {
                var existedBefore = previousNotes.TryGetValue(noteId, out var oldNote);
                var existsNow = currentNotes.TryGetValue(noteId, out var newNote);

                if (!existedBefore || !existsNow)
                {
                    changeCount++;
                    changedNotebookIds.Add(notebookId);
                    continue;
                }

                if (oldNote?.CreatedMs != newNote?.CreatedMs || oldNote?.UpdatedMs != newNote?.UpdatedMs)
                {
                    changeCount++;
                    changedNotebookIds.Add(notebookId);
                }
            }
        }

        return new EvernoteNotebookChangeSummary(changeCount, changedNotebookIds);
    }

    private static Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> BuildNotebookSnapshotMap(
        IEnumerable<EvernoteContainerNoteSnapshotState> containers)
    {
        var map = new Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var notebookId = container.ContainerId ?? string.Empty;
            if (!map.TryGetValue(notebookId, out var noteMap))
            {
                noteMap = new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
                map[notebookId] = noteMap;
            }

            foreach (var note in container.Notes ?? [])
            {
                if (string.IsNullOrWhiteSpace(note.NoteId))
                {
                    continue;
                }

                noteMap[note.NoteId] = note;
            }
        }

        return map;
    }
}
