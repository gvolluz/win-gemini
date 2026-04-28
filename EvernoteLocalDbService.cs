using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinGeminiWrapper;

internal static partial class EvernoteLocalDbService
{
    private const string MissingStackLabel = "(Sans stack)";
    private static readonly Regex MultiBreakRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeadingRegex = new(
        @"^(?<indent>[ ]{0,3})(?<hashes>#{1,6})(?<tail>\s+.*)?$",
        RegexOptions.Compiled);
    private static readonly Regex MarkdownInlineLinkRegex = new(
        @"\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\s]+)\)",
        RegexOptions.Compiled);
    private static readonly Regex MarkdownBareUrlRegex = new(
        @"https?://[^\s<>()\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainTextBulletRegex = new(
        "^(?<indent>[ \\t]*)(?<marker>[\\u2022\\u25E6\\u25AA\\u25CF\\u25CB\\u25A0\\u25C9\\u2023\\u2219\\u00B7])\\s+(?<tail>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex PlainTextOrderedListRegex = new(
        @"^(?<indent>[ \t]*)(?<number>\d{1,3})[.)]\s+(?<tail>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex PlainTextCheckboxRegex = new(
        "^(?<indent>[ \\t]*)(?<box>[\\u2610\\u2611\\u2612])\\s*(?<tail>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex MarkdownListMarkerRegex = new(
        @"^[ \t]{0,3}(?:[-+*]|\d+\.)\s+",
        RegexOptions.Compiled);
    private static readonly Regex RteDocHeadingStyleRegex = new(
        @"h(?<lvl>[1-6])\s+(?<txt>[^!\r\n]{2,180})!\s*style",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RteDocHeadingParenRegex = new(
        @"h(?<lvl>[1-6])\s+(?<txt>[^\(\r\n]{2,180})\(\s*style",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonWordCollapseRegex = new(
        @"[^\p{L}\p{Nd}]+",
        RegexOptions.Compiled);
    private static readonly Regex UuidRegex = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);
    private static readonly HashSet<string> EnmlBlockTags =
    [
        "en-note",
        "div",
        "p",
        "ul",
        "ol",
        "li",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "pre",
        "table",
        "tr",
        "td",
        "th",
        "hr",
        "blockquote"
    ];
    private static readonly HashSet<string> EnmlSkipTags = ["en-media", "img"];
    private static readonly HashSet<string> RteDocListStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "span", "style", "meta", "font", "fontfamilyw", "fontsizew", "colorw",
        "textdecorationw", "inherit", "content", "en-note", "href", "rel", "rev",
        "en_rl_none", "true", "false"
    };
    private static readonly HashSet<string> RteDocHeadingStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "span", "style", "meta", "font", "fontfamilyw", "fontsizew", "colorw",
        "fontweightw", "fontstylew", "textdecorationw", "inherit", "content", "en-note",
        "href", "rel", "rev", "type", "ul", "ol", "li", "true", "false"
    };

    internal static string ResolveLatestDatabasePath(string evernoteRootPath)
    {
        if (string.IsNullOrWhiteSpace(evernoteRootPath))
        {
            throw new InvalidOperationException("Le dossier racine Evernote est vide.");
        }

        var normalizedRoot = Path.GetFullPath(evernoteRootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Dossier introuvable: {normalizedRoot}");
        }

        var candidates = new List<string>();
        var canonicalRemoteGraphRoot = Path.Combine(
            normalizedRoot,
            "conduit-storage",
            "https%3A%2F%2Fwww.evernote.com");

        if (Directory.Exists(canonicalRemoteGraphRoot))
        {
            candidates.AddRange(Directory.EnumerateFiles(canonicalRemoteGraphRoot, "UDB-User*+RemoteGraph.sql"));
        }

        if (candidates.Count == 0)
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(
                    normalizedRoot,
                    "UDB-User*+RemoteGraph.sql",
                    SearchOption.AllDirectories));
            }
            catch (UnauthorizedAccessException)
            {
                // Keep any candidates already found.
            }
            catch (DirectoryNotFoundException)
            {
                // Keep any candidates already found.
            }
        }

        var best = candidates
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();

        if (best is null)
        {
            throw new FileNotFoundException(
                $"Aucun fichier UDB-User...+RemoteGraph.sql trouve sous: {normalizedRoot}");
        }

        return best;
    }

    internal static IReadOnlyList<EvernoteStackInfo> GetStacksAndNotebooks(string evernoteRootPath, out string resolvedDatabasePath)
    {
        resolvedDatabasePath = ResolveLatestDatabasePath(evernoteRootPath);
        using var connection = OpenReadOnlyConnection(resolvedDatabasePath);
        connection.Open();

        const string sql = """
                           SELECT
                               COALESCE(nb.personal_Stack_id, '(Sans stack)') AS stack_id,
                               nb.id AS notebook_id,
                               COALESCE(nb.label, 'Sans carnet') AS notebook_label,
                               COUNT(n.id) AS note_count,
                               MAX(COALESCE(n.updated, n.created)) AS latest_note_change
                           FROM Nodes_Notebook nb
                           LEFT JOIN Nodes_Note n
                               ON n.parent_Notebook_id = nb.id
                              AND n.deleted IS NULL
                           GROUP BY COALESCE(nb.personal_Stack_id, '(Sans stack)'), nb.id, COALESCE(nb.label, 'Sans carnet')
                           ORDER BY stack_id COLLATE NOCASE, notebook_label COLLATE NOCASE
                           """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var stackLookup = new Dictionary<string, List<EvernoteNotebookInfo>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var stackId = ReadRequiredString(reader, "stack_id");
            var notebookId = ReadRequiredString(reader, "notebook_id");
            var notebookLabel = ReadRequiredString(reader, "notebook_label");
            var noteCount = ReadInt32(reader, "note_count");
            var latestNoteChangeMs = ReadNullableInt64(reader, "latest_note_change");

            if (!stackLookup.TryGetValue(stackId, out var notebooks))
            {
                notebooks = [];
                stackLookup[stackId] = notebooks;
            }

            notebooks.Add(new EvernoteNotebookInfo(notebookId, notebookLabel, noteCount, latestNoteChangeMs));
        }

        var result = stackLookup
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var notebooks = entry.Value
                    .OrderBy(notebook => notebook.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var latestStackChange = notebooks
                    .Where(notebook => notebook.LatestChangeMs.HasValue)
                    .Select(notebook => notebook.LatestChangeMs!.Value)
                    .DefaultIfEmpty()
                    .Max();

                return new EvernoteStackInfo(
                    entry.Key,
                    CleanStackLabel(entry.Key),
                    notebooks,
                    latestStackChange > 0 ? latestStackChange : null);
            })
            .ToArray();

        return result;
    }

    internal static EvernoteTrackingSnapshot GetTrackingSnapshot(string evernoteRootPath, out string resolvedDatabasePath)
    {
        resolvedDatabasePath = ResolveLatestDatabasePath(evernoteRootPath);
        using var connection = OpenReadOnlyConnection(resolvedDatabasePath);
        connection.Open();

        const string sql = """
                           SELECT
                               COALESCE(nb.personal_Stack_id, '(Sans stack)') AS stack_id,
                               nb.id AS notebook_id,
                               n.id AS note_id,
                               n.created AS note_created,
                               n.updated AS note_updated
                           FROM Nodes_Note n
                           JOIN Nodes_Notebook nb ON nb.id = n.parent_Notebook_id
                           WHERE n.deleted IS NULL
                           ORDER BY stack_id COLLATE NOCASE, notebook_id COLLATE NOCASE, note_id COLLATE NOCASE
                           """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var stackLookup = new Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>>(StringComparer.OrdinalIgnoreCase);
        var notebookLookup = new Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            var stackId = ReadRequiredString(reader, "stack_id");
            var notebookId = ReadRequiredString(reader, "notebook_id");
            var noteId = ReadRequiredString(reader, "note_id");
            if (string.IsNullOrWhiteSpace(noteId))
            {
                continue;
            }

            var snapshotNote = new EvernoteNoteSnapshotState
            {
                NoteId = noteId,
                CreatedMs = ReadNullableInt64(reader, "note_created"),
                UpdatedMs = ReadNullableInt64(reader, "note_updated")
            };

            AddSnapshotNote(stackLookup, stackId, snapshotNote);
            AddSnapshotNote(notebookLookup, notebookId, snapshotNote);
        }

        return new EvernoteTrackingSnapshot(
            ConvertLookupToContainers(stackLookup),
            ConvertLookupToContainers(notebookLookup));
    }

    internal static EvernoteMarkdownExportResult ExportNotebookGroupToMarkdown(
        string evernoteRootPath,
        IReadOnlyCollection<string> selectedNotebookIds,
        string exportFileBaseName,
        string outputDirectory,
        string backupDirectory,
        int maxBackupsToKeep)
    {
        if (selectedNotebookIds.Count == 0)
        {
            throw new InvalidOperationException("Aucun notebook selectionne.");
        }

        var normalizedNotebookIds = selectedNotebookIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedNotebookIds.Length == 0)
        {
            throw new InvalidOperationException("Aucun notebook selectionne.");
        }

        var normalizedExportFileBaseName = SanitizeExportFileBaseName(exportFileBaseName);
        if (string.IsNullOrWhiteSpace(normalizedExportFileBaseName))
        {
            throw new InvalidOperationException("Nom de fichier d'export invalide.");
        }

        var resolvedDatabasePath = ResolveLatestDatabasePath(evernoteRootPath);
        using var connection = OpenReadOnlyConnection(resolvedDatabasePath);
        connection.Open();

        using var command = connection.CreateCommand();
        var placeholderNames = new List<string>(normalizedNotebookIds.Length);
        for (var index = 0; index < normalizedNotebookIds.Length; index++)
        {
            var parameterName = $"$nb{index}";
            placeholderNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, normalizedNotebookIds[index]);
        }

        var contentSqlExpression = BuildNoteContentSqlExpression(connection);
        command.CommandText = $"""
                              SELECT
                                  n.id AS note_guid,
                                  COALESCE(n.label, 'Sans titre') AS note_title,
                                  n.created AS note_created,
                                  n.updated AS note_updated,
                                  nb.id AS notebook_id,
                                  COALESCE(nb.label, 'Sans carnet') AS notebook_label,
                                  COALESCE(nb.personal_Stack_id, '{MissingStackLabel}') AS stack_id,
                                  {contentSqlExpression} AS content
                              FROM Nodes_Note n
                              JOIN Nodes_Notebook nb ON nb.id = n.parent_Notebook_id
                              LEFT JOIN Offline_Search_Note_Content c ON c.id = n.id
                              WHERE n.deleted IS NULL
                                AND nb.id IN ({string.Join(", ", placeholderNames)})
                              ORDER BY stack_id COLLATE NOCASE, notebook_label COLLATE NOCASE, n.updated DESC
                              """;

        var rows = new List<EvernoteExportNoteRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new EvernoteExportNoteRow(
                    ReadRequiredString(reader, "stack_id"),
                    ReadRequiredString(reader, "notebook_id"),
                    ReadRequiredString(reader, "notebook_label"),
                    ReadRequiredString(reader, "note_guid"),
                    ReadRequiredString(reader, "note_title"),
                    ReadNullableInt64(reader, "note_created"),
                    ReadNullableInt64(reader, "note_updated"),
                    ReadRequiredString(reader, "content")));
            }
        }

        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(backupDirectory);

        var outputFilePath = Path.Combine(outputDirectory, $"{normalizedExportFileBaseName}.md");
        string? backupFilePath = null;
        if (File.Exists(outputFilePath))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            backupFilePath = Path.Combine(backupDirectory, $"{normalizedExportFileBaseName}_{timestamp}.md");
            File.Copy(outputFilePath, backupFilePath, overwrite: true);
        }

        var builder = new StringBuilder();
        var firstNote = true;
        foreach (var note in rows)
        {
            if (!firstNote)
            {
                builder.AppendLine("---");
                builder.AppendLine();
            }

            firstNote = false;
            var noteTitle = NormalizeNoteTitleForHeading(note.NoteTitle);
            builder.AppendLine($"# {noteTitle}");
            builder.AppendLine();
            builder.AppendLine(
                $"{note.NoteGuid} | created: {FormatMsTimestamp(note.CreatedMs)} | updated: {FormatMsTimestamp(note.UpdatedMs)}");
            builder.AppendLine();

            var body = ConvertEvernoteContentToMarkdown(note.Content, evernoteRootPath, note.NoteGuid);
            body = NormalizeEvernoteInternalLinks(body);
            body = PromoteMarkdownHeadingLevels(body, levelIncrement: 1);
            builder.AppendLine(string.IsNullOrWhiteSpace(body) ? "_(contenu hors cache local)_" : body);
            builder.AppendLine();
        }

        File.WriteAllText(outputFilePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var deletedFiles = PruneOldBackups(backupDirectory, normalizedExportFileBaseName, maxBackupsToKeep);
        return new EvernoteMarkdownExportResult(
            normalizedExportFileBaseName,
            outputFilePath,
            backupFilePath,
            rows.Count,
            resolvedDatabasePath,
            deletedFiles);
    }

    internal static EvernoteSingleNoteDiagnosticResult ExportSingleNoteDiagnostic(
        string evernoteRootPath,
        string noteGuid,
        string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(noteGuid))
        {
            throw new InvalidOperationException("Identifiant de note invalide.");
        }

        var normalizedNoteGuid = noteGuid.Trim();
        var resolvedDatabasePath = ResolveLatestDatabasePath(evernoteRootPath);

        using var connection = OpenReadOnlyConnection(resolvedDatabasePath);
        connection.Open();

        using var command = connection.CreateCommand();
        var contentSqlExpression = BuildNoteContentSqlExpression(connection);
        command.CommandText = $"""
                              SELECT
                                  n.id AS note_guid,
                                  COALESCE(n.label, 'Sans titre') AS note_title,
                                  n.created AS note_created,
                                  n.updated AS note_updated,
                                  nb.id AS notebook_id,
                                  COALESCE(nb.label, 'Sans carnet') AS notebook_label,
                                  COALESCE(nb.personal_Stack_id, '{MissingStackLabel}') AS stack_id,
                                  {contentSqlExpression} AS content
                              FROM Nodes_Note n
                              JOIN Nodes_Notebook nb ON nb.id = n.parent_Notebook_id
                              LEFT JOIN Offline_Search_Note_Content c ON c.id = n.id
                              WHERE n.deleted IS NULL
                                AND LOWER(n.id) = LOWER($noteGuid)
                              LIMIT 1
                              """;
        command.Parameters.AddWithValue("$noteGuid", normalizedNoteGuid);

        EvernoteExportNoteRow? note = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                note = new EvernoteExportNoteRow(
                    ReadRequiredString(reader, "stack_id"),
                    ReadRequiredString(reader, "notebook_id"),
                    ReadRequiredString(reader, "notebook_label"),
                    ReadRequiredString(reader, "note_guid"),
                    ReadRequiredString(reader, "note_title"),
                    ReadNullableInt64(reader, "note_created"),
                    ReadNullableInt64(reader, "note_updated"),
                    ReadRequiredString(reader, "content"));
            }
        }

        if (note is null)
        {
            throw new InvalidOperationException($"Note introuvable pour l'id: {normalizedNoteGuid}");
        }

        Directory.CreateDirectory(outputDirectory);
        var safeTitle = SanitizeExportFileBaseName(note.NoteTitle);
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "note";
        }

        var safeGuid = SanitizeExportFileBaseName(note.NoteGuid);
        var fileBaseName = $"{safeTitle}_{safeGuid}_diag";

        var rawEnmlPath = Path.Combine(outputDirectory, $"{fileBaseName}.enml.txt");
        var markdownPath = Path.Combine(outputDirectory, $"{fileBaseName}.md");
        var structurePath = Path.Combine(outputDirectory, $"{fileBaseName}.structure.txt");

        File.WriteAllText(rawEnmlPath, note.Content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var noteTitle = NormalizeNoteTitleForHeading(note.NoteTitle);
        var rawContent = note.Content ?? string.Empty;
        var body = ConvertEvernoteContentToMarkdown(rawContent, evernoteRootPath, note.NoteGuid);
        body = NormalizeEvernoteInternalLinks(body);
        body = PromoteMarkdownHeadingLevels(body, levelIncrement: 1);

        var markdownBuilder = new StringBuilder();
        markdownBuilder.AppendLine($"# {noteTitle}");
        markdownBuilder.AppendLine();
        markdownBuilder.AppendLine(
            $"{note.NoteGuid} | created: {FormatMsTimestamp(note.CreatedMs)} | updated: {FormatMsTimestamp(note.UpdatedMs)}");
        markdownBuilder.AppendLine();
        markdownBuilder.AppendLine(string.IsNullOrWhiteSpace(body) ? "_(contenu hors cache local)_" : body);
        markdownBuilder.AppendLine();
        File.WriteAllText(markdownPath, markdownBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var structureDump = BuildEnmlStructureDump(rawContent);
        File.WriteAllText(structurePath, structureDump, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new EvernoteSingleNoteDiagnosticResult(
            note.NoteGuid,
            note.NoteTitle,
            note.NotebookId,
            note.NotebookName,
            note.StackId,
            resolvedDatabasePath,
            rawEnmlPath,
            markdownPath,
            structurePath);
    }

}

internal sealed record EvernoteStackInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<EvernoteNotebookInfo> Notebooks,
    long? LatestChangeMs);

internal sealed record EvernoteNotebookInfo(
    string Id,
    string Name,
    int NoteCount,
    long? LatestChangeMs);
internal sealed record EvernoteTrackingSnapshot(
    IReadOnlyList<EvernoteContainerNoteSnapshotState> StackSnapshots,
    IReadOnlyList<EvernoteContainerNoteSnapshotState> NotebookSnapshots);

internal sealed record EvernoteMarkdownExportResult(
    string ExportFileBaseName,
    string OutputFilePath,
    string? BackupFilePath,
    int ExportedNotes,
    string DatabasePath,
    int DeletedBackupFiles);

internal sealed record EvernoteSingleNoteDiagnosticResult(
    string NoteGuid,
    string NoteTitle,
    string NotebookId,
    string NotebookName,
    string StackId,
    string DatabasePath,
    string RawEnmlPath,
    string MarkdownPath,
    string StructureDumpPath);

internal sealed record EvernoteExportNoteRow(
    string StackId,
    string NotebookId,
    string NotebookName,
    string NoteGuid,
    string NoteTitle,
    long? CreatedMs,
    long? UpdatedMs,
    string Content);

internal sealed record RteHeadingHint(
    int Level,
    string Text,
    string NormalizedText);

internal sealed record RteListHint(
    bool IsOrdered,
    string Text,
    string NormalizedText);

