using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinGeminiWrapper;

internal static partial class EvernoteLocalDbService
{
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

}
