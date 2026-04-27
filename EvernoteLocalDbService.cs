using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinGeminiWrapper;

internal static class EvernoteLocalDbService
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

    private static SqliteConnection OpenReadOnlyConnection(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(connectionString.ToString());
    }

    private static string CleanStackLabel(string stackId)
    {
        if (string.IsNullOrWhiteSpace(stackId))
        {
            return MissingStackLabel;
        }

        var trimmed = stackId.Trim();
        if (trimmed.StartsWith("stack:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Split(':', 2)[1].Trim();
        }

        return trimmed;
    }

    private static string DecodeOfflineSearchContent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var decoded = value.Replace("/n", "\n").Replace("\\n", "\n");
        decoded = WebUtility.HtmlDecode(decoded);
        decoded = decoded.Replace("\r", string.Empty);
        decoded = MultiBreakRegex.Replace(decoded, "\n\n");
        return decoded.Trim();
    }

    private static string ConvertEvernoteContentToMarkdown(
        string rawContent,
        string? evernoteRootPath = null,
        string? noteGuid = null)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return string.Empty;
        }

        if (LooksLikeEnml(rawContent) && TryConvertEnmlToMarkdown(rawContent, out var markdown))
        {
            return NormalizeMarkdownStructure(markdown);
        }

        var decoded = DecodeOfflineSearchContent(rawContent);
        decoded = NormalizeBulletLikeLines(decoded);
        var plainMarkdown = NormalizeMarkdownStructure(ConvertLineBreaksToMarkdown(decoded));
        return ApplyRteDocHeadingHints(plainMarkdown, evernoteRootPath, noteGuid);
    }

    private static string ApplyRteDocHeadingHints(string markdown, string? evernoteRootPath, string? noteGuid)
    {
        if (string.IsNullOrWhiteSpace(markdown) ||
            string.IsNullOrWhiteSpace(evernoteRootPath) ||
            string.IsNullOrWhiteSpace(noteGuid))
        {
            return markdown;
        }

        if (!TryReadRteDocRawText(evernoteRootPath, noteGuid, out var rteDocRawText))
        {
            return markdown;
        }

        var headingHints = ExtractHeadingHintsFromRteDoc(rteDocRawText);
        var listHints = ExtractListHintsFromRteDoc(rteDocRawText);
        if (headingHints.Count == 0 && listHints.Count == 0)
        {
            return markdown;
        }

        var headingLookup = BuildHeadingLevelLookup(headingHints);
        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (MarkdownHeadingRegex.IsMatch(line) ||
                MarkdownListMarkerRegex.IsMatch(line) ||
                trimmed.StartsWith("> ", StringComparison.Ordinal) ||
                trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                continue;
            }

            var normalizedLine = NormalizeComparableText(trimmed);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                continue;
            }

            if (!TryResolveHeadingLevelFromLookup(headingLookup, normalizedLine, out var level))
            {
                continue;
            }

            lines[i] = $"{new string('#', level)} {trimmed}";
        }

        var withHeadingHints = string.Join("\n", lines).Trim('\r', '\n');
        return ApplyRteDocListHints(withHeadingHints, listHints);
    }

    private static bool TryReadRteDocRawText(string evernoteRootPath, string noteGuid, out string rawText)
    {
        rawText = string.Empty;
        if (!TryResolveRteDocFilePath(evernoteRootPath, noteGuid, out var rteDocPath))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(rteDocPath);
            rawText = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrWhiteSpace(rawText);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveRteDocFilePath(string evernoteRootPath, string noteGuid, out string rteDocPath)
    {
        rteDocPath = string.Empty;
        if (string.IsNullOrWhiteSpace(evernoteRootPath) || string.IsNullOrWhiteSpace(noteGuid))
        {
            return false;
        }

        var normalizedGuid = noteGuid.Trim().ToLowerInvariant();
        if (!UuidRegex.IsMatch(normalizedGuid))
        {
            return false;
        }

        var firstPart = normalizedGuid[..3];
        var lastPart = normalizedGuid[^3..];

        if (TryResolveRteDocFromDirectPathHint(evernoteRootPath, normalizedGuid, firstPart, lastPart, out rteDocPath))
        {
            return true;
        }

        var remoteRoot = FindConduitFsRemoteRoot(evernoteRootPath);
        if (string.IsNullOrWhiteSpace(remoteRoot))
        {
            return false;
        }

        foreach (var userDirectory in Directory.EnumerateDirectories(remoteRoot))
        {
            var candidate = Path.Combine(
                userDirectory,
                "rte",
                "Note",
                "internal_rteDoc",
                firstPart,
                lastPart,
                $"{normalizedGuid}.dat");
            if (File.Exists(candidate))
            {
                rteDocPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveRteDocFromDirectPathHint(
        string pathHint,
        string normalizedGuid,
        string firstPart,
        string lastPart,
        out string rteDocPath)
    {
        rteDocPath = string.Empty;
        if (string.IsNullOrWhiteSpace(pathHint))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(pathHint);
        }
        catch
        {
            return false;
        }

        if (File.Exists(fullPath))
        {
            var expectedFileName = $"{normalizedGuid}.dat";
            if (string.Equals(Path.GetFileName(fullPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                rteDocPath = fullPath;
                return true;
            }

            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        var normalizedDirectory = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}internal_rteDoc";
        var markerIndex = normalizedDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var internalRteDocRoot = normalizedDirectory[..(markerIndex + marker.Length)];
        var candidate = Path.Combine(internalRteDocRoot, firstPart, lastPart, $"{normalizedGuid}.dat");
        if (!File.Exists(candidate))
        {
            return false;
        }

        rteDocPath = candidate;
        return true;
    }

    private static string? FindConduitFsRemoteRoot(string evernoteRootPath)
    {
        string? currentPath;
        try
        {
            currentPath = Directory.Exists(evernoteRootPath)
                ? Path.GetFullPath(evernoteRootPath)
                : Path.GetDirectoryName(Path.GetFullPath(evernoteRootPath));
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < 16 && !string.IsNullOrWhiteSpace(currentPath); i++)
        {
            if (LooksLikeConduitFsRemoteRoot(currentPath))
            {
                return currentPath;
            }

            var candidate = Path.Combine(currentPath, "conduit-fs", "https%3A%2F%2Fwww.evernote.com");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }

        return null;
    }

    private static bool LooksLikeConduitFsRemoteRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var directory = new DirectoryInfo(path);
        if (!string.Equals(directory.Name, "https%3A%2F%2Fwww.evernote.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(directory.Parent?.Name, "conduit-fs", StringComparison.OrdinalIgnoreCase);
    }

    private static List<RteHeadingHint> ExtractHeadingHintsFromRteDoc(string rawRteDoc)
    {
        if (string.IsNullOrWhiteSpace(rawRteDoc))
        {
            return [];
        }

        var clean = SanitizeRteDocRawText(rawRteDoc);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return [];
        }

        var hints = new List<RteHeadingHint>();
        AddDeterministicHeadingHints(clean, hints);
        return hints;
    }

    private static Dictionary<string, Queue<int>> BuildHeadingLevelLookup(IReadOnlyList<RteHeadingHint> headingHints)
    {
        var lookup = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        foreach (var hint in headingHints)
        {
            if (string.IsNullOrWhiteSpace(hint.NormalizedText))
            {
                continue;
            }

            if (!lookup.TryGetValue(hint.NormalizedText, out var levels))
            {
                levels = new Queue<int>();
                lookup[hint.NormalizedText] = levels;
            }

            levels.Enqueue(Math.Clamp(hint.Level, 1, 6));
        }

        return lookup;
    }

    private static bool TryResolveHeadingLevelFromLookup(
        IReadOnlyDictionary<string, Queue<int>> headingLookup,
        string normalizedLine,
        out int resolvedLevel)
    {
        resolvedLevel = 0;
        if (headingLookup.Count == 0 || string.IsNullOrWhiteSpace(normalizedLine))
        {
            return false;
        }

        if (!headingLookup.TryGetValue(normalizedLine, out var levels) || levels.Count == 0)
        {
            return false;
        }

        resolvedLevel = levels.Dequeue();
        return true;
    }

    private static void AddDeterministicHeadingHints(string cleanRteDoc, List<RteHeadingHint> target)
    {
        var tokens = cleanRteDoc
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!TryParseHeadingToken(tokens[i], out var level))
            {
                continue;
            }

            var words = new List<string>();
            for (var j = i + 1; j < tokens.Length && j <= i + 60; j++)
            {
                var candidate = CleanupRteHeadingText(tokens[j]);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (TryParseHeadingToken(candidate, out _))
                {
                    break;
                }

                var normalizedCandidate = candidate.ToLowerInvariant();
                if (normalizedCandidate.Contains("--en-nodeid", StringComparison.Ordinal) ||
                    normalizedCandidate.Contains("--en-iscollapsed", StringComparison.Ordinal))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (RteDocHeadingStopTokens.Contains(normalizedCandidate))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (UuidRegex.IsMatch(candidate) || candidate.Length > 140)
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (!candidate.Any(char.IsLetterOrDigit))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                words.Add(candidate);
                if (words.Count >= 24)
                {
                    break;
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            var headingText = CleanupRteHeadingText(string.Join(" ", words));
            if (string.IsNullOrWhiteSpace(headingText) || !headingText.Any(char.IsLetter))
            {
                continue;
            }

            var normalizedText = NormalizeComparableText(headingText);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                continue;
            }

            var dedupeKey = $"{Math.Clamp(level, 1, 6)}|{normalizedText}";
            if (seen.Add(dedupeKey))
            {
                target.Add(new RteHeadingHint(level, headingText, normalizedText));
            }
        }
    }

    private static List<RteListHint> ExtractListHintsFromRteDoc(string rawRteDoc)
    {
        if (string.IsNullOrWhiteSpace(rawRteDoc))
        {
            return [];
        }

        var clean = SanitizeRteDocRawText(rawRteDoc);
        var hints = new List<RteListHint>();
        AddTokenListHints(clean, hints);

        if (hints.Count <= 1)
        {
            return hints;
        }

        var compact = new List<RteListHint>(hints.Count);
        RteListHint? previous = null;
        foreach (var hint in hints)
        {
            if (previous is not null &&
                previous.IsOrdered == hint.IsOrdered &&
                string.Equals(previous.NormalizedText, hint.NormalizedText, StringComparison.Ordinal))
            {
                continue;
            }

            compact.Add(hint);
            previous = hint;
        }

        return compact;
    }

    private static void AddRegexHeadingHints(string cleanRteDoc, Regex regex, List<RteHeadingHint> target)
    {
        foreach (Match match in regex.Matches(cleanRteDoc))
        {
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["lvl"].Value, out var level))
            {
                continue;
            }

            var headingText = CleanupRteHeadingText(match.Groups["txt"].Value);
            if (!IsHeadingLikePlainTextLine(headingText))
            {
                continue;
            }

            var normalizedText = NormalizeComparableText(headingText);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                continue;
            }

            target.Add(new RteHeadingHint(level, headingText, normalizedText));
        }
    }

    private static void AddTokenHeadingHints(string cleanRteDoc, List<RteHeadingHint> target)
    {
        var tokens = cleanRteDoc
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "style", "span", "div", "ul", "ol", "li", "meta", "content", "en-note",
            "font", "fontfamilyw", "fontsizew", "colorw", "textdecorationw", "inherit"
        };

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!TryParseHeadingToken(tokens[i], out var level))
            {
                continue;
            }

            var words = new List<string>();
            for (var j = i + 1; j < tokens.Length && j <= i + 14; j++)
            {
                var token = tokens[j];
                if (TryParseHeadingToken(token, out _) || stopWords.Contains(token))
                {
                    break;
                }

                var cleanedToken = CleanupRteHeadingText(token);
                if (string.IsNullOrWhiteSpace(cleanedToken))
                {
                    continue;
                }

                if (!cleanedToken.Any(char.IsLetter))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                words.Add(cleanedToken);
            }

            if (words.Count == 0)
            {
                continue;
            }

            var headingText = CleanupRteHeadingText(string.Join(" ", words));
            if (!IsHeadingLikePlainTextLine(headingText))
            {
                continue;
            }

            var normalizedText = NormalizeComparableText(headingText);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                continue;
            }

            target.Add(new RteHeadingHint(level, headingText, normalizedText));
        }
    }

    private static string ApplyRteDocListHints(string markdown, IReadOnlyList<RteListHint> listHints)
    {
        if (string.IsNullOrWhiteSpace(markdown) || listHints.Count == 0)
        {
            return markdown;
        }

        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var usedHintIndexes = new HashSet<int>();
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (MarkdownHeadingRegex.IsMatch(line) ||
                MarkdownListMarkerRegex.IsMatch(line) ||
                trimmed.StartsWith("> ", StringComparison.Ordinal) ||
                trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                continue;
            }

            var normalizedLine = NormalizeComparableText(trimmed);
            if (string.IsNullOrWhiteSpace(normalizedLine) || normalizedLine.Length < 4)
            {
                continue;
            }

            var matchedHintIndex = -1;
            var bestLengthDelta = int.MaxValue;

            for (var j = 0; j < listHints.Count; j++)
            {
                if (usedHintIndexes.Contains(j))
                {
                    continue;
                }

                if (IsComparableHeadingMatch(normalizedLine, listHints[j].NormalizedText))
                {
                    var delta = Math.Abs(listHints[j].NormalizedText.Length - normalizedLine.Length);
                    if (delta < bestLengthDelta)
                    {
                        bestLengthDelta = delta;
                        matchedHintIndex = j;
                    }
                }
            }

            if (matchedHintIndex < 0)
            {
                continue;
            }

            var hint = listHints[matchedHintIndex];
            var marker = hint.IsOrdered ? "1. " : "- ";
            lines[i] = $"{marker}{trimmed}";
            usedHintIndexes.Add(matchedHintIndex);
        }

        FillMissingBulletLines(lines);
        return string.Join("\n", lines).Trim('\r', '\n');
    }

    private static void FillMissingBulletLines(string[] lines)
    {
        if (lines.Length == 0)
        {
            return;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var intro = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(intro) ||
                !intro.EndsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (MarkdownHeadingRegex.IsMatch(lines[i]) || MarkdownListMarkerRegex.IsMatch(lines[i]))
            {
                continue;
            }

            var blockStart = i + 1;
            var blockEnd = blockStart;
            var bulletCount = 0;

            while (blockEnd < lines.Length && blockEnd <= i + 20)
            {
                var candidate = lines[blockEnd];
                var trimmedCandidate = candidate.Trim();
                if (string.IsNullOrWhiteSpace(trimmedCandidate))
                {
                    break;
                }

                if (trimmedCandidate.StartsWith("```", StringComparison.Ordinal) ||
                    trimmedCandidate.StartsWith("> ", StringComparison.Ordinal) ||
                    trimmedCandidate.StartsWith("|", StringComparison.Ordinal) ||
                    MarkdownHeadingRegex.IsMatch(candidate))
                {
                    break;
                }

                if (MarkdownListMarkerRegex.IsMatch(candidate))
                {
                    bulletCount++;
                }

                blockEnd++;
            }

            if (bulletCount < 2)
            {
                continue;
            }

            for (var j = blockStart; j < blockEnd; j++)
            {
                var candidate = lines[j];
                var trimmedCandidate = candidate.Trim();
                if (string.IsNullOrWhiteSpace(trimmedCandidate) ||
                    MarkdownListMarkerRegex.IsMatch(candidate))
                {
                    continue;
                }

                if (!IsLikelyStandaloneBulletCandidate(trimmedCandidate))
                {
                    continue;
                }

                lines[j] = $"- {trimmedCandidate}";
            }
        }
    }

    private static bool IsLikelyStandaloneBulletCandidate(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 260)
        {
            return false;
        }

        var firstChar = line[0];
        if (!char.IsLetterOrDigit(firstChar) && firstChar is not '"' and not '\'' and not '(')
        {
            return false;
        }

        if (line.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length >= 4;
    }

    private static void AddTokenListHints(string cleanRteDoc, List<RteListHint> target)
    {
        var tokens = cleanRteDoc
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        string? currentListType = null;
        var listContextWindow = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            var cleanedToken = CleanupRteHeadingText(tokens[i]);
            var normalizedToken = cleanedToken.ToLowerInvariant();
            if (normalizedToken is "ul" or "ol")
            {
                currentListType = normalizedToken;
                listContextWindow = 1200;
                continue;
            }

            if (listContextWindow > 0)
            {
                listContextWindow--;
            }
            else
            {
                currentListType = null;
            }

            if (normalizedToken != "li" || string.IsNullOrWhiteSpace(currentListType))
            {
                continue;
            }

            var words = new List<string>();
            for (var j = i + 1; j < tokens.Length && j <= i + 90; j++)
            {
                var candidate = CleanupRteHeadingText(tokens[j]);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var normalizedCandidate = candidate.ToLowerInvariant();
                if (normalizedCandidate is "li" or "ul" or "ol")
                {
                    break;
                }

                if (TryParseHeadingToken(normalizedCandidate, out _))
                {
                    break;
                }

                if (RteDocListStopTokens.Contains(normalizedCandidate))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (!candidate.Any(char.IsLetterOrDigit))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (UuidRegex.IsMatch(candidate))
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                words.Add(candidate);
                if (words.Count >= 48)
                {
                    break;
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            var listText = CleanupRteHeadingText(string.Join(" ", words));
            var normalizedListText = NormalizeComparableText(listText);
            if (string.IsNullOrWhiteSpace(normalizedListText) || normalizedListText.Length < 4)
            {
                continue;
            }

            target.Add(new RteListHint(
                string.Equals(currentListType, "ol", StringComparison.OrdinalIgnoreCase),
                listText,
                normalizedListText));
        }
    }

    private static bool TryParseHeadingToken(string token, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Trim().Trim('!', '(', ')', '[', ']', '{', '}', ';', ':', ',', '.');
        if (normalized.Length == 2 &&
            normalized[0] == 'h' &&
            char.IsDigit(normalized[1]))
        {
            level = Math.Clamp(normalized[1] - '0', 1, 6);
            return true;
        }

        return false;
    }

    private static string SanitizeRteDocRawText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var code = (int)character;
            var isAsciiPrintable = code is >= 32 and <= 126;
            var isLatinExtended = code is >= 160 and <= 0x024F;
            if (isAsciiPrintable || isLatinExtended || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static string CleanupRteHeadingText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeInline(value).Trim();
        normalized = normalized.Trim('!', '(', ')', '[', ']', '{', '}', ';', ':', ',', '.', '|');
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string NormalizeComparableText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var filtered = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                filtered.Append(character);
            }
        }

        var lowered = filtered.ToString().ToLowerInvariant();
        lowered = NonWordCollapseRegex.Replace(lowered, " ");
        lowered = Regex.Replace(lowered, @"\s+", " ").Trim();
        return lowered;
    }

    private static bool IsComparableHeadingMatch(string normalizedLine, string normalizedHint)
    {
        if (string.IsNullOrWhiteSpace(normalizedLine) || string.IsNullOrWhiteSpace(normalizedHint))
        {
            return false;
        }

        if (string.Equals(normalizedLine, normalizedHint, StringComparison.Ordinal))
        {
            return true;
        }

        var compactLine = normalizedLine.Replace(" ", string.Empty, StringComparison.Ordinal);
        var compactHint = normalizedHint.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.Equals(compactLine, compactHint, StringComparison.Ordinal))
        {
            return true;
        }

        var looseLine = BuildLooseComparableText(normalizedLine);
        var looseHint = BuildLooseComparableText(normalizedHint);
        if (!string.IsNullOrWhiteSpace(looseLine) &&
            !string.IsNullOrWhiteSpace(looseHint) &&
            string.Equals(looseLine, looseHint, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalizedHint.Length >= 7 &&
            normalizedLine.StartsWith(normalizedHint, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalizedLine.Length >= 7 &&
            normalizedHint.StartsWith(normalizedLine, StringComparison.Ordinal))
        {
            return true;
        }

        if (compactHint.Length >= 7 &&
            compactLine.StartsWith(compactHint, StringComparison.Ordinal))
        {
            return true;
        }

        if (compactLine.Length >= 7 &&
            compactHint.StartsWith(compactLine, StringComparison.Ordinal))
        {
            return true;
        }

        if (looseHint.Length >= 7 &&
            looseLine.StartsWith(looseHint, StringComparison.Ordinal))
        {
            return true;
        }

        if (looseLine.Length >= 7 &&
            looseHint.StartsWith(looseLine, StringComparison.Ordinal))
        {
            return true;
        }

        if (HasStrongTokenOverlap(looseLine, looseHint))
        {
            return true;
        }

        return normalizedHint.Length >= 12 &&
               (normalizedLine.Contains(normalizedHint, StringComparison.Ordinal) ||
                compactLine.Contains(compactHint, StringComparison.Ordinal));
    }

    private static string BuildLooseComparableText(string normalizedValue)
    {
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return string.Empty;
        }

        var parts = normalizedValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var token = part;
            if (token.Length > 1 &&
                char.IsDigit(token[0]) &&
                token.Skip(1).Any(char.IsLetter))
            {
                token = token.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            }

            if (token.Length <= 2)
            {
                continue;
            }

            filtered.Add(token);
        }

        return filtered.Count == 0
            ? string.Empty
            : string.Join(" ", filtered);
    }

    private static bool HasStrongTokenOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length < 4 || rightTokens.Length < 4)
        {
            return false;
        }

        var shorter = leftTokens.Length <= rightTokens.Length ? leftTokens : rightTokens;
        var longer = leftTokens.Length <= rightTokens.Length ? rightTokens : leftTokens;
        var longerSet = new HashSet<string>(longer, StringComparer.Ordinal);

        var matchCount = 0;
        foreach (var token in shorter)
        {
            if (longerSet.Contains(token))
            {
                matchCount++;
            }
        }

        var requiredMatches = Math.Max(4, (int)Math.Ceiling(shorter.Length * 0.7));
        return matchCount >= requiredMatches;
    }

    private static bool LooksLikeEnml(string rawContent)
    {
        var value = rawContent.TrimStart();
        return value.Contains("<en-note", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertEnmlToMarkdown(string enml, out string markdown)
    {
        markdown = string.Empty;
        var cleaned = CleanupEnml(enml);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        try
        {
            var root = XElement.Parse(cleaned, LoadOptions.PreserveWhitespace);
            var blocks = RenderBlockNode(root, 0);
            markdown = string.Join(
                "\n\n",
                blocks.Where(block => !string.IsNullOrWhiteSpace(block)).Select(block => block.Trim('\r', '\n')));
            markdown = MultiBreakRegex.Replace(markdown, "\n\n").Trim('\r', '\n');
            return !string.IsNullOrWhiteSpace(markdown);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildEnmlStructureDump(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return "Contenu vide.";
        }

        if (!LooksLikeEnml(rawContent))
        {
            return BuildPlainTextStructureDump(rawContent);
        }

        var cleaned = CleanupEnml(rawContent);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "ENML vide apres nettoyage.";
        }

        try
        {
            var root = XElement.Parse(cleaned, LoadOptions.PreserveWhitespace);
            var builder = new StringBuilder();
            builder.AppendLine("ENML structure dump");
            builder.AppendLine("===================");
            AppendEnmlElementDump(root, builder, 0, $"/{GetTagName(root)}[1]");
            return builder.ToString().TrimEnd('\r', '\n');
        }
        catch (Exception exception)
        {
            return $"Impossible d'analyser l'ENML: {exception.Message}";
        }
    }

    private static string BuildPlainTextStructureDump(string rawContent)
    {
        var normalized = DecodeOfflineSearchContent(rawContent);
        var lines = normalized.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        builder.AppendLine("Contenu non ENML (texte brut)");
        builder.AppendLine("============================");
        builder.AppendLine("Format: line | heading? | lvl | texte");

        var lastHeadingLevel = 0;
        var seenParagraph = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var isHeadingCandidate = IsHeadingLikePlainTextLine(line);
            var suggestedLevel = 0;
            if (isHeadingCandidate)
            {
                suggestedLevel = SuggestHeadingLevelForPlainText(
                    lines,
                    i,
                    lastHeadingLevel,
                    seenParagraph);
                lastHeadingLevel = suggestedLevel;
            }
            else if (LooksLikeParagraphLine(line))
            {
                seenParagraph = true;
            }

            builder.Append($"{i + 1:0000} | ");
            builder.Append(isHeadingCandidate ? "yes" : "no ");
            builder.Append(" | ");
            builder.Append(suggestedLevel == 0 ? "-" : suggestedLevel);
            builder.Append(" | ");
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static int SuggestHeadingLevelForPlainText(
        string[] lines,
        int index,
        int lastHeadingLevel,
        bool seenParagraph)
    {
        var previousNonEmpty = FindPreviousNonEmptyLine(lines, index);
        var nextNonEmpty = FindNextNonEmptyLine(lines, index);
        var previousIsHeadingLike = !string.IsNullOrWhiteSpace(previousNonEmpty) &&
                                    IsHeadingLikePlainTextLine(previousNonEmpty);
        var nextIsHeadingLike = !string.IsNullOrWhiteSpace(nextNonEmpty) &&
                                IsHeadingLikePlainTextLine(nextNonEmpty);

        if (!seenParagraph)
        {
            return lastHeadingLevel <= 0 ? 1 : Math.Min(3, lastHeadingLevel + 1);
        }

        if (previousIsHeadingLike)
        {
            return lastHeadingLevel <= 0 ? 2 : Math.Min(3, lastHeadingLevel + 1);
        }

        if (nextIsHeadingLike)
        {
            return lastHeadingLevel <= 0 ? 2 : lastHeadingLevel;
        }

        if (lastHeadingLevel >= 2)
        {
            return lastHeadingLevel;
        }

        return 2;
    }

    private static string FindPreviousNonEmptyLine(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var candidate = lines[i].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string FindNextNonEmptyLine(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
        {
            var candidate = lines[i].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsHeadingLikePlainTextLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = line.Trim();
        if (normalized.Length > 90)
        {
            return false;
        }

        if (normalized.StartsWith("#", StringComparison.Ordinal) ||
            normalized.StartsWith("-", StringComparison.Ordinal) ||
            normalized.StartsWith("*", StringComparison.Ordinal) ||
            normalized.StartsWith("[", StringComparison.Ordinal) ||
            normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Regex.IsMatch(normalized, @"^\d+[.)]\s+"))
        {
            return false;
        }

        if (!normalized.Any(char.IsLetter))
        {
            return false;
        }

        var hasStrongParagraphPunctuation = normalized.Contains('.', StringComparison.Ordinal) ||
                                            normalized.Contains(';', StringComparison.Ordinal) ||
                                            normalized.Contains('?', StringComparison.Ordinal) ||
                                            normalized.Contains('!', StringComparison.Ordinal);
        if (hasStrongParagraphPunctuation && !normalized.EndsWith(":", StringComparison.Ordinal))
        {
            return false;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is >= 1 and <= 14;
    }

    private static bool LooksLikeParagraphLine(string line)
    {
        var normalized = line.Trim();
        if (normalized.Length > 110)
        {
            return true;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 16)
        {
            return true;
        }

        return normalized.Contains(',', StringComparison.Ordinal) ||
               normalized.Contains('.', StringComparison.Ordinal) ||
               normalized.Contains(';', StringComparison.Ordinal);
    }

    private static void AppendEnmlElementDump(XElement element, StringBuilder builder, int depth, string path)
    {
        var tag = GetTagName(element);
        var indent = new string(' ', depth * 2);
        var visibility = IsHidden(element) ? "hidden" : "visible";
        var classAttr = BuildAttributeDump(element, "class", 140);
        var styleAttr = BuildAttributeDump(element, "style", 220);
        var dataHeadingAttr = BuildAttributeDump(element, "data-en-heading-level", 20);
        var dataLevelAttr = BuildAttributeDump(element, "data-heading-level", 20);
        var headingDiagnostic = BuildHeadingDiagnostic(element);
        var directText = TruncateForDebug(
            CleanParagraph(string.Concat(element.Nodes().OfType<XText>().Select(textNode => textNode.Value))),
            140);
        var flattenedText = TruncateForDebug(FlattenText(element), 180);

        var lineBuilder = new StringBuilder();
        lineBuilder.Append(indent)
            .Append(path)
            .Append(" <")
            .Append(tag)
            .Append("> ")
            .Append(visibility)
            .Append(" ")
            .Append(headingDiagnostic);

        if (!string.IsNullOrWhiteSpace(classAttr))
        {
            lineBuilder.Append(" class=\"").Append(classAttr).Append("\"");
        }

        if (!string.IsNullOrWhiteSpace(styleAttr))
        {
            lineBuilder.Append(" style=\"").Append(styleAttr).Append("\"");
        }

        if (!string.IsNullOrWhiteSpace(dataHeadingAttr))
        {
            lineBuilder.Append(" data-en-heading-level=\"").Append(dataHeadingAttr).Append("\"");
        }

        if (!string.IsNullOrWhiteSpace(dataLevelAttr))
        {
            lineBuilder.Append(" data-heading-level=\"").Append(dataLevelAttr).Append("\"");
        }

        if (!string.IsNullOrWhiteSpace(directText))
        {
            lineBuilder.Append(" directText=\"").Append(directText).Append("\"");
        }

        if (!string.IsNullOrWhiteSpace(flattenedText))
        {
            lineBuilder.Append(" text=\"").Append(flattenedText).Append("\"");
        }

        builder.AppendLine(lineBuilder.ToString());

        var childIndex = 0;
        foreach (var child in element.Elements())
        {
            childIndex++;
            AppendEnmlElementDump(
                child,
                builder,
                depth + 1,
                $"{path}/{GetTagName(child)}[{childIndex}]");
        }
    }

    private static string BuildHeadingDiagnostic(XElement element)
    {
        var directHeading = TryGetHeadingLevel(element, out var directLevel)
            ? $"direct-h{directLevel}"
            : "direct-none";
        var extractedHeading = TryExtractHeading(element, out var extractedLevel, out var extractedText)
            ? $"extract-h{extractedLevel}({TruncateForDebug(extractedText, 80)})"
            : "extract-none";
        return $"{directHeading} {extractedHeading}";
    }

    private static string BuildAttributeDump(XElement element, string attributeName, int maxLength)
    {
        var value = GetAttributeValue(element, attributeName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return TruncateForDebug(value, maxLength);
    }

    private static string TruncateForDebug(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeInline(value).Replace("\n", "\\n", StringComparison.Ordinal);
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength]}...";
    }

    private static string CleanupEnml(string enml)
    {
        var cleaned = enml.Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*<\?xml[^>]*\?>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<!DOCTYPE[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("&nbsp;", "&#160;", StringComparison.OrdinalIgnoreCase);
        return cleaned.Trim();
    }

    private static List<string> RenderBlockNode(XElement node, int depth)
    {
        var tag = GetTagName(node);
        if (EnmlSkipTags.Contains(tag) || IsHidden(node))
        {
            return [];
        }

        if (TryExtractHeading(node, out var headingLevel, out var headingText))
        {
            return [$"{new string('#', headingLevel)} {headingText}"];
        }

        if (tag == "en-note" || tag == "div" || tag == "p" || tag == "blockquote")
        {
            return RenderParagraphBlock(node, depth);
        }

        if (tag == "ul")
        {
            var list = RenderListBlock(node, depth, ordered: false);
            return string.IsNullOrWhiteSpace(list) ? [] : [list];
        }

        if (tag == "ol")
        {
            var list = RenderListBlock(node, depth, ordered: true);
            return string.IsNullOrWhiteSpace(list) ? [] : [list];
        }

        if (tag == "table")
        {
            return RenderTableBlock(node);
        }

        if (tag == "pre")
        {
            var raw = string.Concat(node.DescendantNodesAndSelf().OfType<XText>().Select(text => text.Value));
            raw = WebUtility.HtmlDecode(raw).Replace("\r", string.Empty).Trim('\n');
            return string.IsNullOrWhiteSpace(raw) ? [] : [$"```\n{raw}\n```"];
        }

        if (tag == "hr")
        {
            return ["---"];
        }

        var fallback = FlattenText(node);
        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    private static List<string> RenderParagraphBlock(XElement node, int depth)
    {
        var blocks = new List<string>();
        var inlineBuffer = new StringBuilder();

        foreach (var childNode in node.Nodes())
        {
            if (childNode is XElement childElement)
            {
                var childTag = GetTagName(childElement);
                if (EnmlSkipTags.Contains(childTag) || IsHidden(childElement))
                {
                    continue;
                }

                var isNestedBlock =
                    childTag is "ul" or "ol" or "table" or "pre" or "blockquote" or "hr" ||
                    TryExtractHeading(childElement, out _, out _);
                if (isNestedBlock)
                {
                    var paragraph = CleanParagraph(inlineBuffer.ToString());
                    if (!string.IsNullOrWhiteSpace(paragraph))
                    {
                        blocks.Add(paragraph);
                    }

                    inlineBuffer.Clear();
                    blocks.AddRange(RenderBlockNode(childElement, depth));
                    continue;
                }
            }

            inlineBuffer.Append(RenderInlineNode(childNode));
        }

        var finalParagraph = CleanParagraph(inlineBuffer.ToString());
        if (!string.IsNullOrWhiteSpace(finalParagraph))
        {
            blocks.Add(finalParagraph);
        }

        return blocks;
    }

    private static string RenderListBlock(XElement listElement, int depth, bool ordered)
    {
        var lines = new List<string>();
        var index = 1;

        foreach (var child in listElement.Elements())
        {
            var tag = GetTagName(child);
            if (EnmlSkipTags.Contains(tag) || IsHidden(child))
            {
                continue;
            }

            if (tag != "li")
            {
                if (tag == "ul" || tag == "ol")
                {
                    var nestedBlock = RenderListBlock(child, depth + 1, tag == "ol");
                    if (!string.IsNullOrWhiteSpace(nestedBlock))
                    {
                        lines.AddRange(nestedBlock.Split('\n'));
                    }
                }

                continue;
            }

            var marker = ordered ? $"{index}. " : "- ";
            var itemTextParts = new StringBuilder();
            var nestedBlocks = new List<string>();

            foreach (var liNode in child.Nodes())
            {
                if (liNode is XElement liElement)
                {
                    var liTag = GetTagName(liElement);
                    if (liTag == "ul" || liTag == "ol")
                    {
                        var nestedList = RenderListBlock(liElement, depth + 1, liTag == "ol");
                        if (!string.IsNullOrWhiteSpace(nestedList))
                        {
                            nestedBlocks.Add(nestedList);
                        }

                        continue;
                    }

                    if (liTag is "div" or "p")
                    {
                        var paragraphs = RenderParagraphBlock(liElement, depth + 1);
                        if (paragraphs.Count > 0)
                        {
                            if (itemTextParts.Length > 0)
                            {
                                itemTextParts.Append(' ');
                            }

                            itemTextParts.Append(string.Join(" ", paragraphs));
                        }

                        continue;
                    }
                }

                itemTextParts.Append(RenderInlineNode(liNode));
            }

            AppendListItem(lines, depth, marker, itemTextParts.ToString());
            foreach (var nested in nestedBlocks)
            {
                lines.AddRange(nested.Split('\n'));
            }

            if (ordered)
            {
                index++;
            }
        }

        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void AppendListItem(List<string> lines, int depth, string marker, string text)
    {
        var content = CleanParagraph(text);
        var indent = new string(' ', depth * 2);

        if (string.IsNullOrWhiteSpace(content))
        {
            lines.Add($"{indent}{marker}".TrimEnd());
            return;
        }

        var parts = content.Split('\n');
        lines.Add($"{indent}{marker}{parts[0]}");
        var continuationIndent = indent + new string(' ', marker.Length);
        for (var i = 1; i < parts.Length; i++)
        {
            lines.Add($"{continuationIndent}{parts[i]}");
        }
    }

    private static List<string> RenderTableBlock(XElement table)
    {
        var rows = new List<List<string>>();
        foreach (var tr in table.Descendants().Where(element => GetTagName(element) == "tr"))
        {
            var row = tr.Elements()
                .Where(cell =>
                {
                    var tag = GetTagName(cell);
                    return tag is "td" or "th";
                })
                .Select(FlattenText)
                .ToList();
            if (row.Count > 0)
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            return [];
        }

        var columnCount = rows.Max(row => row.Count);
        foreach (var row in rows)
        {
            while (row.Count < columnCount)
            {
                row.Add(string.Empty);
            }
        }

        static string RowToMarkdown(IReadOnlyList<string> columns) =>
            $"| {string.Join(" | ", columns.Select(column => column.Replace("|", "\\|", StringComparison.Ordinal)))} |";

        var tableLines = new List<string>
        {
            RowToMarkdown(rows[0]),
            RowToMarkdown(Enumerable.Repeat("---", columnCount).ToArray())
        };
        tableLines.AddRange(rows.Skip(1).Select(RowToMarkdown));
        return [string.Join("\n", tableLines)];
    }

    private static string RenderInlineNode(XNode node)
    {
        if (node is XText textNode)
        {
            return textNode.Value;
        }

        if (node is not XElement element)
        {
            return string.Empty;
        }

        var tag = GetTagName(element);
        if (EnmlSkipTags.Contains(tag) || IsHidden(element))
        {
            return string.Empty;
        }

        if (tag == "br")
        {
            return "\n";
        }

        if (tag == "en-todo")
        {
            var isChecked = string.Equals(
                GetAttributeValue(element, "checked"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            return isChecked ? "[x] " : "[ ] ";
        }

        var inner = CleanParagraph(string.Concat(element.Nodes().Select(RenderInlineNode)));
        return tag switch
        {
            "b" or "strong" when !string.IsNullOrWhiteSpace(inner) => $"**{inner}**",
            "i" or "em" when !string.IsNullOrWhiteSpace(inner) => $"*{inner}*",
            "code" when !string.IsNullOrWhiteSpace(inner) => $"`{inner}`",
            "s" or "strike" or "del" when !string.IsNullOrWhiteSpace(inner) => $"~~{inner}~~",
            "a" => RenderLink(element, inner),
            _ => inner
        };
    }

    private static string RenderLink(XElement anchorElement, string inner)
    {
        var href = GetAttributeValue(anchorElement, "href")?.Trim() ?? string.Empty;
        var label = string.IsNullOrWhiteSpace(inner) ? href : inner;
        if (string.IsNullOrWhiteSpace(href))
        {
            return label;
        }

        var noteGuid = TryExtractEvernoteNoteGuid(href);
        if (!string.IsNullOrWhiteSpace(noteGuid))
        {
            return $"[{NormalizeInternalLinkLabel(label, href)}|{noteGuid}]";
        }

        return $"[{label}]({href})";
    }

    private static string FlattenText(XElement node)
    {
        return CleanParagraph(string.Concat(node.Nodes().Select(RenderInlineNode)));
    }

    private static bool IsHidden(XElement node)
    {
        var style = GetAttributeValue(node, "style") ?? string.Empty;
        return style.Contains("display:none", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanParagraph(string value)
    {
        var text = NormalizeInline(value);
        text = MultiBreakRegex.Replace(text, "\n\n");
        text = ConvertLineBreaksToMarkdown(text);
        return text.Trim();
    }

    private static string NormalizeInline(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(value);
        text = text.Replace("\u00A0", " ", StringComparison.Ordinal);
        text = text.Replace("\r", string.Empty, StringComparison.Ordinal);
        text = Regex.Replace(text, @"[\t ]+", " ");
        text = Regex.Replace(text, @" ?\n ?", "\n");
        return text;
    }

    private static string ConvertLineBreaksToMarkdown(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length <= 1)
        {
            return normalized;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            var currentLine = lines[index].TrimEnd();
            builder.Append(currentLine);

            if (index >= lines.Length - 1)
            {
                continue;
            }

            var nextLine = lines[index + 1];
            if (string.IsNullOrWhiteSpace(currentLine) || string.IsNullOrWhiteSpace(nextLine))
            {
                builder.Append('\n');
                continue;
            }

            if (IsMarkdownStructuralLine(currentLine) || IsMarkdownStructuralLine(nextLine))
            {
                builder.Append('\n');
                continue;
            }

            builder.Append("  \n");
        }

        return builder.ToString();
    }

    private static string NormalizeBulletLikeLines(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = NormalizeBulletLikeLine(lines[i]);
        }

        return string.Join("\n", lines).Trim('\r', '\n');
    }

    private static string NormalizeMarkdownStructure(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var normalized = markdown.Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                lines[i] = line;
                continue;
            }

            if (inCodeFence)
            {
                lines[i] = line;
                continue;
            }

            lines[i] = NormalizeBulletLikeLine(line);
        }

        return string.Join("\n", lines).Trim('\r', '\n');
    }

    private static string NormalizeBulletLikeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line.TrimEnd();
        }

        var checkboxMatch = PlainTextCheckboxRegex.Match(line);
        if (checkboxMatch.Success)
        {
            var indent = checkboxMatch.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal);
            var tail = checkboxMatch.Groups["tail"].Value.Trim();
            var box = checkboxMatch.Groups["box"].Value;
            var marker = box == "\u2610" ? "- [ ] " : "- [x] ";
            return $"{indent}{marker}{tail}".TrimEnd();
        }

        var bulletMatch = PlainTextBulletRegex.Match(line);
        if (bulletMatch.Success)
        {
            var indent = bulletMatch.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal);
            var tail = bulletMatch.Groups["tail"].Value.Trim();
            return $"{indent}- {tail}".TrimEnd();
        }

        var orderedMatch = PlainTextOrderedListRegex.Match(line);
        if (orderedMatch.Success)
        {
            var indent = orderedMatch.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal);
            var number = orderedMatch.Groups["number"].Value;
            var tail = orderedMatch.Groups["tail"].Value.Trim();
            return $"{indent}{number}. {tail}".TrimEnd();
        }

        return line.TrimEnd();
    }

    private static bool IsMarkdownStructuralLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("```", StringComparison.Ordinal) ||
            trimmed.StartsWith("> ", StringComparison.Ordinal) ||
            trimmed.StartsWith("|", StringComparison.Ordinal) ||
            trimmed.StartsWith("---", StringComparison.Ordinal) ||
            trimmed.StartsWith("***", StringComparison.Ordinal))
        {
            return true;
        }

        return MarkdownListMarkerRegex.IsMatch(line);
    }

    private static string PromoteMarkdownHeadingLevels(string markdown, int levelIncrement)
    {
        if (string.IsNullOrWhiteSpace(markdown) || levelIncrement <= 0)
        {
            return markdown;
        }

        var normalized = markdown.Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                continue;
            }

            var match = MarkdownHeadingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var currentLevel = match.Groups["hashes"].Value.Length;
            var newLevel = Math.Clamp(currentLevel + levelIncrement, 1, 6);
            var tail = match.Groups["tail"].Success ? match.Groups["tail"].Value : " ";
            lines[i] = $"{match.Groups["indent"].Value}{new string('#', newLevel)}{tail}";
        }

        return string.Join("\n", lines).Trim('\r', '\n');
    }

    private static bool TryExtractHeading(XElement node, out int headingLevel, out string headingText)
    {
        headingLevel = 0;
        headingText = string.Empty;

        if (EnmlSkipTags.Contains(GetTagName(node)) || IsHidden(node))
        {
            return false;
        }

        if (TryGetHeadingLevel(node, out headingLevel))
        {
            headingText = FlattenText(node);
            if (!string.IsNullOrWhiteSpace(headingText))
            {
                return true;
            }
        }

        var hasOwnVisibleText = node.Nodes()
            .OfType<XText>()
            .Any(textNode => !string.IsNullOrWhiteSpace(NormalizeInline(textNode.Value)));
        if (hasOwnVisibleText)
        {
            headingLevel = 0;
            headingText = string.Empty;
            return false;
        }

        var visibleChildren = node.Elements()
            .Where(element => !EnmlSkipTags.Contains(GetTagName(element)) && !IsHidden(element))
            .ToArray();
        if (visibleChildren.Length != 1)
        {
            headingLevel = 0;
            headingText = string.Empty;
            return false;
        }

        return TryExtractHeading(visibleChildren[0], out headingLevel, out headingText);
    }

    private static bool TryGetHeadingLevel(XElement node, out int headingLevel)
    {
        headingLevel = 0;
        var tag = GetTagName(node);

        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            headingLevel = Math.Clamp(tag[1] - '0', 1, 6);
            return true;
        }

        var dataLevel = GetAttributeValue(node, "data-en-heading-level") ??
                        GetAttributeValue(node, "data-heading-level");
        if (int.TryParse(dataLevel, out var parsedLevel))
        {
            headingLevel = Math.Clamp(parsedLevel, 1, 6);
            return true;
        }

        var style = GetAttributeValue(node, "style") ?? string.Empty;
        for (var level = 1; level <= 6; level++)
        {
            if (style.Contains($"-en-h{level}:true", StringComparison.OrdinalIgnoreCase) ||
                style.Contains($"-en-h{level}: true", StringComparison.OrdinalIgnoreCase) ||
                style.Contains($"--en-h{level}:true", StringComparison.OrdinalIgnoreCase) ||
                style.Contains($"--en-h{level}: true", StringComparison.OrdinalIgnoreCase) ||
                style.Contains($"--en-heading-level:{level}", StringComparison.OrdinalIgnoreCase))
            {
                headingLevel = level;
                return true;
            }
        }

        var className = GetAttributeValue(node, "class") ?? string.Empty;
        for (var level = 1; level <= 6; level++)
        {
            if (className.Contains($"en-h{level}", StringComparison.OrdinalIgnoreCase) ||
                className.Contains($"-en-h{level}", StringComparison.OrdinalIgnoreCase) ||
                className.Contains($"heading-{level}", StringComparison.OrdinalIgnoreCase))
            {
                headingLevel = level;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeEvernoteInternalLinks(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var normalized = markdown.Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            line = MarkdownInlineLinkRegex.Replace(line, match =>
            {
                var label = match.Groups["label"].Value;
                var url = match.Groups["url"].Value;
                var noteGuid = TryExtractEvernoteNoteGuid(url);
                if (string.IsNullOrWhiteSpace(noteGuid))
                {
                    return match.Value;
                }

                return $"[{NormalizeInternalLinkLabel(label, url)}|{noteGuid}]";
            });

            line = MarkdownBareUrlRegex.Replace(line, match =>
            {
                var url = match.Value;
                var noteGuid = TryExtractEvernoteNoteGuid(url);
                if (string.IsNullOrWhiteSpace(noteGuid))
                {
                    return url;
                }

                return $"[{url}|{noteGuid}]";
            });

            lines[i] = line;
        }

        return string.Join("\n", lines).Trim('\r', '\n');
    }

    private static string NormalizeNoteTitleForHeading(string noteTitle)
    {
        var normalized = NormalizeInline(noteTitle ?? string.Empty).Trim();
        normalized = normalized.Replace("\n", " ", StringComparison.Ordinal);
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Sans titre";
        }

        return normalized;
    }

    private static string NormalizeInternalLinkLabel(string label, string fallback)
    {
        var normalized = NormalizeInline(string.IsNullOrWhiteSpace(label) ? fallback : label).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback;
        }

        normalized = normalized.Replace("|", "/", StringComparison.Ordinal);
        normalized = normalized.Replace("[", "(", StringComparison.Ordinal);
        normalized = normalized.Replace("]", ")", StringComparison.Ordinal);
        return normalized.Trim();
    }

    private static string? TryExtractEvernoteNoteGuid(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var looksLikeEvernoteLink =
            url.Contains("evernote", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("evernote:", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeEvernoteLink)
        {
            return null;
        }

        var match = UuidRegex.Match(url);
        if (!match.Success)
        {
            return null;
        }

        return match.Value.ToLowerInvariant();
    }

    private static string GetTagName(XElement element)
    {
        return element.Name.LocalName.ToLowerInvariant();
    }

    private static string? GetAttributeValue(XElement element, string attributeName)
    {
        return element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string SanitizeExportFileBaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '_');
        }

        normalized = normalized.Replace(".md", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
    }

    private static string FormatMsTimestamp(long? rawMs)
    {
        if (rawMs is null)
        {
            return string.Empty;
        }

        try
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(rawMs.Value).UtcDateTime;
            return date.ToString("yyyy-MM-dd HH:mm:ss UTC");
        }
        catch
        {
            return rawMs.Value.ToString();
        }
    }

    private static string ReadRequiredString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        var value = reader.GetValue(ordinal);
        return Convert.ToString(value) ?? string.Empty;
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            _ => long.TryParse(Convert.ToString(value), out var parsed) ? parsed : null
        };
    }

    private static int ReadInt32(SqliteDataReader reader, string columnName)
    {
        var value = ReadNullableInt64(reader, columnName);
        if (value is null)
        {
            return 0;
        }

        try
        {
            return checked((int)value.Value);
        }
        catch
        {
            return int.MaxValue;
        }
    }

    private static string BuildNoteContentSqlExpression(SqliteConnection connection)
    {
        var noteContentCandidate = GetFirstExistingColumn(
            connection,
            "Nodes_Note",
            "content",
            "body");
        var cacheContentCandidate = GetFirstExistingColumn(
            connection,
            "Offline_Search_Note_Content",
            "content",
            "plain_text");

        if (!string.IsNullOrWhiteSpace(noteContentCandidate) && !string.IsNullOrWhiteSpace(cacheContentCandidate))
        {
            return $"COALESCE(n.{noteContentCandidate}, c.{cacheContentCandidate}, '')";
        }

        if (!string.IsNullOrWhiteSpace(noteContentCandidate))
        {
            return $"COALESCE(n.{noteContentCandidate}, '')";
        }

        if (!string.IsNullOrWhiteSpace(cacheContentCandidate))
        {
            return $"COALESCE(c.{cacheContentCandidate}, '')";
        }

        return "''";
    }

    private static string? GetFirstExistingColumn(
        SqliteConnection connection,
        string tableName,
        params string[] candidateColumnNames)
    {
        if (candidateColumnNames.Length == 0)
        {
            return null;
        }

        var existingColumns = GetTableColumns(connection, tableName);
        if (existingColumns.Count == 0)
        {
            return null;
        }

        foreach (var candidateColumnName in candidateColumnNames)
        {
            if (existingColumns.Contains(candidateColumnName))
            {
                return candidateColumnName;
            }
        }

        return null;
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}')";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var columnName = ReadRequiredString(reader, "name");
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                columns.Add(columnName);
            }
        }

        return columns;
    }

    private static void AddSnapshotNote(
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> lookup,
        string containerId,
        EvernoteNoteSnapshotState note)
    {
        var normalizedContainerId = containerId ?? string.Empty;
        if (!lookup.TryGetValue(normalizedContainerId, out var noteMap))
        {
            noteMap = new Dictionary<string, EvernoteNoteSnapshotState>(StringComparer.OrdinalIgnoreCase);
            lookup[normalizedContainerId] = noteMap;
        }

        noteMap[note.NoteId] = new EvernoteNoteSnapshotState
        {
            NoteId = note.NoteId,
            CreatedMs = note.CreatedMs,
            UpdatedMs = note.UpdatedMs
        };
    }

    private static IReadOnlyList<EvernoteContainerNoteSnapshotState> ConvertLookupToContainers(
        Dictionary<string, Dictionary<string, EvernoteNoteSnapshotState>> lookup)
    {
        return lookup
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new EvernoteContainerNoteSnapshotState
            {
                ContainerId = entry.Key,
                Notes = entry.Value.Values
                    .OrderBy(note => note.NoteId, StringComparer.OrdinalIgnoreCase)
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

    private static int PruneOldBackups(string backupDirectory, string exportFileBaseName, int maxBackupsToKeep)
    {
        var keepCount = Math.Max(1, maxBackupsToKeep);
        if (!Directory.Exists(backupDirectory))
        {
            return 0;
        }

        var backupFiles = Directory.EnumerateFiles(
                backupDirectory,
                $"{exportFileBaseName}_*.md",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var deletedCount = 0;
        foreach (var oldFile in backupFiles.Skip(keepCount))
        {
            try
            {
                File.Delete(oldFile.FullName);
                deletedCount++;
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        return deletedCount;
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

