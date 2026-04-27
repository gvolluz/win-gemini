using System.Net;
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

            var body = ConvertEvernoteContentToMarkdown(note.Content);
            body = PromoteMarkdownHeadingLevels(body, 1);
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

    private static string ConvertEvernoteContentToMarkdown(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return string.Empty;
        }

        if (LooksLikeEnml(rawContent) && TryConvertEnmlToMarkdown(rawContent, out var markdown))
        {
            return markdown;
        }

        return ConvertLineBreaksToMarkdown(DecodeOfflineSearchContent(rawContent));
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

        if (TryGetHeadingLevel(node, out var headingLevel))
        {
            var headingText = FlattenText(node);
            return string.IsNullOrWhiteSpace(headingText)
                ? []
                : [$"{new string('#', headingLevel)} {headingText}"];
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
                    TryGetHeadingLevel(childElement, out _);
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

            builder.Append("  \n");
        }

        return builder.ToString();
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

    private static bool TryGetHeadingLevel(XElement node, out int headingLevel)
    {
        headingLevel = 0;
        var tag = GetTagName(node);

        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            headingLevel = Math.Clamp(tag[1] - '0', 1, 6);
            return true;
        }

        var style = GetAttributeValue(node, "style") ?? string.Empty;
        for (var level = 1; level <= 6; level++)
        {
            if (style.Contains($"--en-h{level}:true", StringComparison.OrdinalIgnoreCase) ||
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

internal sealed record EvernoteExportNoteRow(
    string StackId,
    string NotebookId,
    string NotebookName,
    string NoteGuid,
    string NoteTitle,
    long? CreatedMs,
    long? UpdatedMs,
    string Content);
