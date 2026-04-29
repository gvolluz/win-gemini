using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinGemini;

internal static partial class EvernoteLocalDbService
{
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

