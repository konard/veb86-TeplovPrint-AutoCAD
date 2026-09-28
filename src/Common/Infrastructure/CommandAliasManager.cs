using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#if ZWCAD
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>




/// </summary>
internal static class CommandAliasManager
{

    internal static readonly IReadOnlyList<AliasableCommand> AliasableCommands = new[]
    {
        new AliasableCommand("Добавить рамку", "ZBP_ADD_TITLE_BLOCK"),
        new AliasableCommand("Библиотека рамок", "ZBP_MANAGE_LIBRARY"),
        new AliasableCommand("Пакетная печать по блокам", "ZBP_SHOW_PANEL"),
        new AliasableCommand("Универсальная пакетная печать", "ZBP_RECTANGLE_BATCH_PLOT"),
        new AliasableCommand("Печать одного листа", "ZBP_SINGLE_PLOT"),
        new AliasableCommand("Настройки", "ZBP_SETTINGS"),
    };


    private const string BlockBegin = ";>>> ZBP COMMAND ALIASES >>>";
    private const string BlockEnd = ";<<< ZBP COMMAND ALIASES <<<";

    private static readonly Regex AliasPattern = new(
        "^[A-Za-z][A-Za-z0-9]{0,15}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);


    private static readonly Regex PgpAliasLinePattern = new(
        @"^\s*([A-Za-z0-9]+)\s*,\s*\*\s*([^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);


    private static readonly Encoding ByteRoundTripEncoding = Encoding.GetEncoding("ISO-8859-1");


    private static readonly byte[] AsciiBlockBeginBytes = Encoding.ASCII.GetBytes(BlockBegin);
    private static readonly byte[] AsciiBlockEndBytes = Encoding.ASCII.GetBytes(BlockEnd);

    internal sealed class AliasableCommand
    {
        internal AliasableCommand(string displayName, string commandName)
        {
            DisplayName = displayName;
            CommandName = commandName;
        }

        internal string DisplayName { get; }
        internal string CommandName { get; }
    }

    private sealed class AliasEntry
    {
        internal AliasEntry(string alias, string commandName)
        {
            Alias = alias;
            CommandName = commandName;
        }

        internal string Alias { get; }
        internal string CommandName { get; }
    }


    internal static bool IsValidAlias(string? alias)
    {
        return alias != null && AliasPattern.IsMatch(alias);
    }

    /// <summary>


    /// </summary>
    internal static Dictionary<string, string> NormalizeAliases(Dictionary<string, string>? aliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aliases == null)
        {
            return result;
        }

        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in AliasableCommands)
        {
            var alias = "";
            foreach (var pair in aliases)
            {
                if (string.Equals(pair.Key, command.CommandName, StringComparison.OrdinalIgnoreCase))
                {
                    alias = (pair.Value ?? "").Trim();
                    break;
                }
            }

            if (alias.Length == 0 || !IsValidAlias(alias) || !usedAliases.Add(alias))
            {
                continue;
            }

            result[command.CommandName] = alias;
        }

        return result;
    }

    /// <summary>


    /// </summary>
    internal static bool Apply(IReadOnlyDictionary<string, string> aliases, out string message)
    {
        var pgpPath = LocatePgpFile();
        if (pgpPath == null)
        {
            message = "Настройки сохранены, но файл параметров PGP (acad.pgp / ZWCAD.pgp) не найден; сокращённые команды не будут работать.";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(pgpPath);

            bytes = RemoveLegacyAsciiBlock(bytes);


            var encoding = DetectPgpEncoding(bytes, out var bomLength);
            var content = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);


            var backupPath = pgpPath + ".zbpbak";
            if (!File.Exists(backupPath))
            {
                File.WriteAllBytes(backupPath, bytes);
            }

            content = RemoveManagedBlock(content);

            var entries = new List<AliasEntry>();
            foreach (var command in AliasableCommands)
            {
                if (aliases.TryGetValue(command.CommandName, out var raw))
                {
                    var alias = (raw ?? "").Trim();
                    if (IsValidAlias(alias))
                    {
                        entries.Add(new AliasEntry(alias.ToUpperInvariant(), command.CommandName));
                    }
                }
            }

            var conflicts = FindConflicts(content, entries);

            if (entries.Count > 0)
            {
                var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
                builder.Append("\r\n\r\n");
                builder.Append(BlockBegin).Append("\r\n");
                foreach (var entry in entries)
                {
                    builder.Append((entry.Alias + ",").PadRight(16)).Append('*').Append(entry.CommandName).Append("\r\n");
                }

                builder.Append(BlockEnd).Append("\r\n");
                content = builder.ToString();
            }


            var body = encoding.GetBytes(content);
            var output = new byte[bomLength + body.Length];
            Array.Copy(bytes, 0, output, 0, bomLength);
            Array.Copy(body, 0, output, bomLength, body.Length);
            File.WriteAllBytes(pgpPath, output);

            message = entries.Count > 0
                ? $"В файл PGP записано сокращённых команд: {entries.Count}.\n{pgpPath}\n\nВыполните REINIT и отметьте файл PGP либо перезапустите CAD."
                : "Сокращённые команды удалены из файла PGP.\n\nВыполните REINIT и отметьте файл PGP либо перезапустите CAD.";
            if (conflicts.Count > 0)
            {
                message += "\n\nВнимание: следующие команды совпадают с существующими псевдонимами PGP и будут заменены текущими настройками:\n" + string.Join(", ", conflicts);
            }

            return true;
        }
        catch (Exception ex)
        {
            message = "Настройки сохранены, но записать файл PGP не удалось:\n" + ex.Message;
            return false;
        }
    }

    /// <summary>


    /// </summary>
    private static Encoding DetectPgpEncoding(byte[] bytes, out int bomLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bomLength = 3;
            return Encoding.UTF8;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bomLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }


        var sample = Math.Min(bytes.Length, 2000);
        if (sample >= 4)
        {
            var evenZeros = 0;
            var oddZeros = 0;
            for (var i = 0; i < sample; i++)
            {
                if (bytes[i] != 0)
                {
                    continue;
                }

                if (i % 2 == 0)
                {
                    evenZeros++;
                }
                else
                {
                    oddZeros++;
                }
            }

            if (oddZeros > sample / 4 && evenZeros == 0)
            {
                bomLength = 0;
                return Encoding.Unicode;
            }

            if (evenZeros > sample / 4 && oddZeros == 0)
            {
                bomLength = 0;
                return Encoding.BigEndianUnicode;
            }
        }

        bomLength = 0;
        return ByteRoundTripEncoding;
    }

    /// <summary>


    /// </summary>
    private static byte[] RemoveLegacyAsciiBlock(byte[] bytes)
    {
        var beginIndex = IndexOf(bytes, AsciiBlockBeginBytes, 0);
        if (beginIndex < 0)
        {
            return bytes;
        }

        var removeStart = beginIndex;
        while (removeStart > 0 && bytes[removeStart - 1] != (byte)'\n')
        {
            removeStart--;
        }

        int removeEnd;
        var endIndex = IndexOf(bytes, AsciiBlockEndBytes, beginIndex);
        if (endIndex < 0)
        {
            removeEnd = bytes.Length;
        }
        else
        {
            removeEnd = endIndex + AsciiBlockEndBytes.Length;
            while (removeEnd < bytes.Length && bytes[removeEnd] != (byte)'\n')
            {
                removeEnd++;
            }

            if (removeEnd < bytes.Length)
            {
                removeEnd++;
            }
        }

        var result = new byte[bytes.Length - (removeEnd - removeStart)];
        Array.Copy(bytes, 0, result, 0, removeStart);
        Array.Copy(bytes, removeEnd, result, removeStart, bytes.Length - removeEnd);
        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        for (var i = startIndex; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }


    private static string? LocatePgpFile()
    {
        try
        {
            var prefix = Convert.ToString(CadApp.GetSystemVariable("ACADPREFIX")) ?? "";
            foreach (var directory in prefix.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = FindPgpInDirectory(directory.Trim());
                if (path != null)
                {
                    return path;
                }
            }
        }
        catch
        {
        }



        try
        {
            var roamableRoot = Convert.ToString(CadApp.GetSystemVariable("ROAMABLEROOTPREFIX")) ?? "";
            foreach (var directory in roamableRoot.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = FindPgpInDirectory(directory.Trim());
                if (path != null)
                {
                    return path;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? FindPgpInDirectory(string directory)
    {
        try
        {
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.pgp", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "acad.pgp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "ZWCAD.pgp", StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch
        {
        }

        return null;
    }


    private static string RemoveManagedBlock(string content)
    {
        var beginIndex = content.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (beginIndex < 0)
        {
            return content;
        }


        var removeStart = beginIndex == 0 ? 0 : content.LastIndexOf('\n', beginIndex - 1) + 1;
        var endIndex = content.IndexOf(BlockEnd, beginIndex, StringComparison.Ordinal);
        int removeEnd;
        if (endIndex < 0)
        {
            removeEnd = content.Length;
        }
        else
        {
            var lineEnd = content.IndexOf('\n', endIndex);
            removeEnd = lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        return content.Remove(removeStart, removeEnd - removeStart);
    }


    private static List<string> FindConflicts(string contentWithoutBlock, IReadOnlyList<AliasEntry> entries)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PgpAliasLinePattern.Matches(contentWithoutBlock))
        {
            var alias = match.Groups[1].Value;
            if (!existing.ContainsKey(alias))
            {
                existing[alias] = match.Groups[2].Value;
            }
        }

        var conflicts = new List<string>();
        foreach (var entry in entries)
        {
            if (existing.TryGetValue(entry.Alias, out var mappedCommand)
                && !string.Equals(mappedCommand, entry.CommandName, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(entry.Alias);
            }
        }

        return conflicts;
    }
}
