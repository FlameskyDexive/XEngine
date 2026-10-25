using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace XEngine.UnityImporter.Editor;

/// <summary>A parsed Unity YAML value: exactly one of scalar / map / list is set.</summary>
public sealed class UnityYamlNode
{
    public string? Scalar { get; init; }
    public Dictionary<string, UnityYamlNode>? Map { get; init; }
    public List<UnityYamlNode>? List { get; init; }

    public bool IsNull => Scalar == null && Map == null && List == null;

    public UnityYamlNode? this[string key] => Map != null && Map.TryGetValue(key, out UnityYamlNode? value) ? value : null;

    public string? ScalarOf(string key) => this[key]?.Scalar;

    public float FloatOf(string key, float fallback = 0f)
        => float.TryParse(this[key]?.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v
            : fallback;

    public long LongOf(string key, long fallback = 0)
        => long.TryParse(this[key]?.Scalar, out long v) ? v : fallback;

    /// <summary>Reads this node as a hex-encoded byte blob (m_IndexBuffer / _typelessdata).</summary>
    public byte[] HexBytes() => UnityYaml.HexToBytes(Scalar ?? string.Empty);
}

/// <summary>One `--- !u!CLASSID &amp;FILEID` document from a Unity serialized file.</summary>
public sealed class UnityYamlDocument
{
    public int ClassId { get; init; }
    public long FileId { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public UnityYamlNode Root { get; init; } = new();
}

/// <summary>
/// Minimal parser for the constrained YAML subset Unity writes for serialized assets:
/// block maps/lists by indentation, inline flow maps, plain scalars, and huge single-line
/// hex blobs. Not a general YAML parser.
/// </summary>
public static class UnityYaml
{
    public static List<UnityYamlDocument> ParseFile(string path) => Parse(File.ReadAllLines(path));

    /// <summary>Parses a headerless YAML stream (Unity .meta files: no `--- !u!` documents) into
    /// a single map node.</summary>
    public static UnityYamlNode ParsePlainFile(string path) => ParsePlain(File.ReadAllLines(path));

    public static UnityYamlNode ParsePlain(string[] lines)
    {
        var cursor = new LineCursor(new List<string>(lines));
        int? indent = FirstContentIndent(cursor);
        return indent.HasValue ? ParseMap(cursor, indent.Value) : new UnityYamlNode();
    }

    public static List<UnityYamlDocument> Parse(string[] lines)
    {
        var docs = new List<UnityYamlDocument>();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (!line.StartsWith("--- !u!", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Header shape: `--- !u!43 &4300000 Mesh:` (class name may be absent on older files).
            int classEnd = line.IndexOf(' ', 7);
            string classIdText = classEnd > 7 ? line[7..classEnd] : line[7..].TrimEnd();
            string rest = classEnd > 7 ? line[(classEnd + 1)..].Trim() : string.Empty;
            long fileId = 0;
            string className = string.Empty;
            if (rest.StartsWith('&'))
            {
                int idEnd = rest.IndexOf(' ');
                string idText = idEnd > 0 ? rest[1..idEnd] : rest[1..];
                long.TryParse(idText, out fileId);
                string tail = idEnd > 0 ? rest[(idEnd + 1)..].Trim() : string.Empty;
                int colon = tail.IndexOf(':');
                className = colon > 0 ? tail[..colon] : tail;
            }
            else
            {
                int colon = rest.IndexOf(':');
                className = colon > 0 ? rest[..colon] : rest;
            }

            var body = new List<string>();
            int j = i + 1;
            while (j < lines.Length && !lines[j].StartsWith("--- !u!", StringComparison.Ordinal))
            {
                body.Add(lines[j]);
                j++;
            }

            var cursor = new LineCursor(body);
            int? bodyIndent = FirstContentIndent(cursor);
            UnityYamlNode root = new UnityYamlNode();
            string rootClassName = className;
            if (bodyIndent == 0)
            {
                // Body shape: `ClassName:\n  <fields…>` — unwrap the class-name wrapper so
                // Root is the field map itself.
                UnityYamlNode parsed = ParseBlock(cursor, 0);
                if (parsed.Map is { Count: 1 })
                {
                    foreach (KeyValuePair<string, UnityYamlNode> wrapperEntry in parsed.Map)
                    {
                        rootClassName = wrapperEntry.Key;
                        root = wrapperEntry.Value;
                    }
                }
                else
                {
                    root = parsed;
                }
            }
            else if (bodyIndent.HasValue)
            {
                root = ParseBlock(cursor, bodyIndent.Value);
            }
            docs.Add(new UnityYamlDocument
            {
                ClassId = int.TryParse(classIdText, out int cid) ? cid : 0,
                FileId = fileId,
                ClassName = rootClassName,
                Root = root,
            });
            i = j;
        }
        return docs;
    }

    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static int? FirstContentIndent(LineCursor cursor)
    {
        string? line = cursor.Peek();
        return line == null ? null : IndentOf(line);
    }

    private sealed class LineCursor
    {
        private readonly List<string> _lines;
        public int Index;

        public LineCursor(List<string> lines) { _lines = lines; }

        /// <summary>Next meaningful (non-blank, non-comment) line without consuming it.</summary>
        public string? Peek()
        {
            while (Index < _lines.Count)
            {
                string l = _lines[Index];
                if (l.Length == 0 || l.TrimStart().StartsWith('#'))
                {
                    Index++;
                    continue;
                }
                return l;
            }
            return null;
        }
    }

    private static UnityYamlNode ParseBlock(LineCursor cursor, int indent)
    {
        string? first = cursor.Peek();
        if (first == null) return new UnityYamlNode();
        string content = first[indent..];
        if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-")
            return ParseList(cursor, indent);
        return ParseMap(cursor, indent);
    }

    private static UnityYamlNode ParseMap(LineCursor cursor, int indent)
    {
        var map = new Dictionary<string, UnityYamlNode>();
        while (cursor.Peek() is { } line)
        {
            int lineIndent = IndentOf(line);
            if (lineIndent != indent) break; // shallower ends this map; deeper is handled per-key

            string content = line[indent..];
            if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-") break;
            int colon = FindColon(content);
            if (colon <= 0) { cursor.Index++; continue; }

            string key = content[..colon].Trim();
            string rest = content[(colon + 1)..].Trim();
            cursor.Index++;

            if (rest.Length == 0)
            {
                string? next = cursor.Peek();
                if (next != null && IndentOf(next) > indent)
                    map[key] = ParseBlock(cursor, IndentOf(next));
                else if (next != null && IndentOf(next) == indent
                         && (next[indent..].StartsWith("- ", StringComparison.Ordinal) || next[indent..] == "-"))
                {
                    // Unity style: sequence items sit at the same indent as their key.
                    map[key] = ParseList(cursor, indent);
                }
                else
                    map[key] = new UnityYamlNode();
            }
            else
            {
                map[key] = ParseValue(rest);
            }
        }
        return new UnityYamlNode { Map = map };
    }

    private static UnityYamlNode ParseList(LineCursor cursor, int indent)
    {
        var list = new List<UnityYamlNode>();
        while (cursor.Peek() is { } line && IndentOf(line) == indent)
        {
            string content = line[indent..];
            if (!content.StartsWith("- ", StringComparison.Ordinal) && content != "-") break;
            cursor.Index++;

            if (content == "-")
            {
                string? next = cursor.Peek();
                list.Add(next != null && IndentOf(next) > indent
                    ? ParseBlock(cursor, IndentOf(next))
                    : new UnityYamlNode());
                continue;
            }

            string rest = content[2..];
            int itemIndent = indent + 2;
            if (FindColon(rest) > 0 && !rest.StartsWith('{') && !rest.StartsWith('[') && !rest.StartsWith('"'))
            {
                // `- key: value` starts a map item whose remaining keys sit at indent + 2.
                // Normalize the dash into two spaces and parse the collected lines as a map.
                var sub = new List<string> { new string(' ', itemIndent) + rest };
                while (cursor.Peek() is { } follow && IndentOf(follow) > indent)
                {
                    sub.Add(follow);
                    cursor.Index++;
                }
                list.Add(ParseMap(new LineCursor(sub), itemIndent));
            }
            else
            {
                list.Add(ParseValue(rest));
            }
        }
        return new UnityYamlNode { List = list };
    }

    private static UnityYamlNode ParseValue(string rest)
    {
        if (rest.StartsWith('{'))
            return ParseFlowMap(rest);
        if (rest.StartsWith('[') && rest.EndsWith(']'))
            return new UnityYamlNode { List = new List<UnityYamlNode>() }; // inline (empty) list
        if (rest.Length >= 2 && rest.StartsWith('"') && rest.EndsWith('"'))
            return new UnityYamlNode { Scalar = rest[1..^1] };
        return new UnityYamlNode { Scalar = rest };
    }

    private static UnityYamlNode ParseFlowMap(string text)
    {
        var map = new Dictionary<string, UnityYamlNode>();
        int i = 1; // skip '{'
        while (i < text.Length && text[i] != '}')
        {
            int colon = text.IndexOf(':', i);
            if (colon < 0) break;
            string key = text[i..colon].Trim();
            i = colon + 1;
            while (i < text.Length && text[i] == ' ') i++;

            if (i < text.Length && text[i] == '{')
            {
                int depth = 0, j = i;
                while (j < text.Length)
                {
                    if (text[j] == '{') depth++;
                    else if (text[j] == '}')
                    {
                        depth--;
                        if (depth == 0) { j++; break; }
                    }
                    j++;
                }
                map[key] = ParseFlowMap(text[i..j]);
                i = j;
            }
            else
            {
                int j = i;
                while (j < text.Length && text[j] != ',' && text[j] != '}') j++;
                string value = text[i..j].Trim();
                map[key] = value.Length > 0 ? new UnityYamlNode { Scalar = value } : new UnityYamlNode();
                i = j;
            }

            while (i < text.Length && (text[i] == ',' || text[i] == ' ')) i++;
        }
        return new UnityYamlNode { Map = map };
    }

    private static int FindColon(string text)
    {
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == ':' && !inQuote && (i + 1 >= text.Length || text[i + 1] == ' '))
                return i;
        }
        return -1;
    }

    /// <summary>Decodes a hex string (no separators) into bytes; returns empty on malformed input.</summary>
    public static byte[] HexToBytes(string hex)
    {
        if (hex.Length == 0) return Array.Empty<byte>();
        if (hex.Length % 2 != 0) hex = hex[..^1];
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = HexVal(hex[i * 2]);
            int lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return Array.Empty<byte>();
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
