using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinGeminiWrapper;

internal static partial class EvernoteLocalDbService
{
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

}
