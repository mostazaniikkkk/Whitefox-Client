// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WhiteFox.Core.Util
{
    /// <summary>
    /// A TOML table that keeps the order of its keys. Values are string, long, double, bool, List&lt;object&gt; (arrays),
    /// TomlTable (tables) or List&lt;TomlTable&gt; (arrays of tables).
    /// </summary>
    public sealed class TomlTable
    {
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, object> values = new Dictionary<string, object>();

        public IEnumerable<string> Keys => order;
        public bool ContainsKey(string key) => values.ContainsKey(key);

        public object this[string key]
        {
            get => values.TryGetValue(key, out var value) ? value : null;
            set
            {
                if (value == null)
                {
                    if (values.Remove(key)) order.Remove(key);
                    return;
                }
                if (!values.ContainsKey(key)) order.Add(key);
                values[key] = value;
            }
        }

        public string GetString(string key, string fallback = null) => this[key] as string ?? fallback;
        public bool GetBool(string key, bool fallback) => this[key] is bool value ? value : fallback;

        public long GetLong(string key, long fallback)
        {
            switch (this[key])
            {
                case long l: return l;
                case double d: return (long)d;
                default: return fallback;
            }
        }

        public int GetInt(string key, int fallback) => (int)GetLong(key, fallback);

        public double GetDouble(string key, double fallback)
        {
            switch (this[key])
            {
                case double d: return d;
                case long l: return l;
                default: return fallback;
            }
        }

        public float GetFloat(string key, float fallback) => (float)GetDouble(key, fallback);

        /// <summary>The sub table of a key, created when it does not exist.</summary>
        public TomlTable Table(string key)
        {
            if (this[key] is TomlTable table) return table;
            table = new TomlTable();
            this[key] = table;
            return table;
        }

        /// <summary>The array of tables of a key (empty when missing).</summary>
        public List<TomlTable> Tables(string key) => this[key] as List<TomlTable> ?? new List<TomlTable>();

        public List<string> GetStrings(string key)
        {
            var result = new List<string>();
            if (this[key] is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is string text) result.Add(text);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Reader and writer for the TOML the client stores its configuration in: tables, arrays of tables, basic and
    /// literal strings, integers, floats, booleans and arrays (no dates, inline tables or multi-line strings).
    /// </summary>
    public static class Toml
    {
        public static TomlTable Parse(string text)
        {
            var root = new TomlTable();
            var current = root;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var number = 0; number < lines.Length; number++)
            {
                var line = lines[number].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                try
                {
                    if (line.StartsWith("[["))
                    {
                        var end = line.IndexOf("]]", StringComparison.Ordinal);
                        var path = SplitKey(line.Substring(2, end - 2));
                        var parent = Walk(root, path, path.Count - 1);
                        var last = path[path.Count - 1];
                        var list = parent[last] as List<TomlTable>;
                        if (list == null) parent[last] = list = new List<TomlTable>();
                        current = new TomlTable();
                        list.Add(current);
                        continue;
                    }
                    if (line[0] == '[')
                    {
                        var end = line.IndexOf(']');
                        var path = SplitKey(line.Substring(1, end - 1));
                        current = Walk(root, path, path.Count);
                        continue;
                    }
                    // key = value; arrays may continue on the next lines
                    var position = 0;
                    var keyPath = ReadKey(line, ref position);
                    SkipSpaces(line, ref position);
                    if (position >= line.Length || line[position] != '=') throw new FormatException("expected '='");
                    position++;
                    var valueText = line.Substring(position);
                    while (valueText.TrimStart().StartsWith("[") && !Balanced(valueText) && number + 1 < lines.Length) valueText += "\n" + lines[++number];
                    var valuePosition = 0;
                    var value = ReadValue(valueText, ref valuePosition);
                    var target = Walk(current, keyPath, keyPath.Count - 1);
                    target[keyPath[keyPath.Count - 1]] = value;
                }
                catch (Exception exception)
                {
                    throw new FormatException("TOML line " + (number + 1) + ": " + exception.Message);
                }
            }
            return root;
        }

        private static TomlTable Walk(TomlTable table, List<string> path, int count)
        {
            for (var i = 0; i < count; i++)
            {
                switch (table[path[i]])
                {
                    case TomlTable child:
                        table = child;
                        break;
                    case List<TomlTable> list when list.Count > 0:
                        table = list[list.Count - 1];
                        break;
                    default:
                        table = table.Table(path[i]);
                        break;
                }
            }
            return table;
        }

        private static bool Balanced(string text)
        {
            var depth = 0;
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '#') while (i < text.Length && text[i] != '\n') i++;
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '[') depth++;
                else if (c == ']') depth--;
            }
            return depth <= 0;
        }

        private static List<string> SplitKey(string text)
        {
            var position = 0;
            return ReadKey(text.Trim(), ref position);
        }

        private static List<string> ReadKey(string text, ref int position)
        {
            var parts = new List<string>();
            while (true)
            {
                SkipSpaces(text, ref position);
                if (position >= text.Length) break;
                var c = text[position];
                if (c == '"' || c == '\'')
                {
                    parts.Add(ReadString(text, ref position));
                }
                else
                {
                    var start = position;
                    while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_' || text[position] == '-')) position++;
                    if (position == start) throw new FormatException("invalid key");
                    parts.Add(text.Substring(start, position - start));
                }
                SkipSpaces(text, ref position);
                if (position < text.Length && text[position] == '.')
                {
                    position++;
                    continue;
                }
                break;
            }
            if (parts.Count == 0) throw new FormatException("empty key");
            return parts;
        }

        private static void SkipSpaces(string text, ref int position)
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
        }

        private static void SkipBlank(string text, ref int position)
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') position++;
                else if (c == '#') while (position < text.Length && text[position] != '\n') position++;
                else break;
            }
        }

        private static object ReadValue(string text, ref int position)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length) throw new FormatException("missing value");
            var c = text[position];
            if (c == '"' || c == '\'') return ReadString(text, ref position);
            if (c == '[')
            {
                position++;
                var list = new List<object>();
                while (true)
                {
                    SkipBlank(text, ref position);
                    if (position >= text.Length) throw new FormatException("unterminated array");
                    if (text[position] == ']')
                    {
                        position++;
                        return list;
                    }
                    list.Add(ReadValue(text, ref position));
                    SkipBlank(text, ref position);
                    if (position < text.Length && text[position] == ',') position++;
                }
            }
            var start = position;
            while (position < text.Length && text[position] != ',' && text[position] != ']' && text[position] != '#' && text[position] != '\n') position++;
            var token = text.Substring(start, position - start).Trim();
            if (token == "true") return true;
            if (token == "false") return false;
            var number = token.Replace("_", "");
            if (long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return integer;
            if (number == "inf" || number == "+inf") return double.PositiveInfinity;
            if (number == "-inf") return double.NegativeInfinity;
            if (number == "nan") return double.NaN;
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)) return real;
            throw new FormatException("invalid value '" + token + "'");
        }

        private static string ReadString(string text, ref int position)
        {
            var quote = text[position++];
            var result = new StringBuilder();
            while (true)
            {
                if (position >= text.Length) throw new FormatException("unterminated string");
                var c = text[position++];
                if (c == quote) return result.ToString();
                if (c == '\\' && quote == '"')
                {
                    var escape = text[position++];
                    switch (escape)
                    {
                        case 'n': result.Append('\n'); break;
                        case 't': result.Append('\t'); break;
                        case 'r': result.Append('\r'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case 'u':
                            result.Append((char)Convert.ToInt32(text.Substring(position, 4), 16));
                            position += 4;
                            break;
                        case 'U':
                            result.Append(char.ConvertFromUtf32(Convert.ToInt32(text.Substring(position, 8), 16)));
                            position += 8;
                            break;
                        default: throw new FormatException("invalid escape \\" + escape);
                    }
                    continue;
                }
                result.Append(c);
            }
        }

        // ------------------------------------------------------------------ writing

        public static string Write(TomlTable table)
        {
            var output = new StringBuilder();
            WriteTable(output, table, null);
            return output.ToString();
        }

        private static void WriteTable(StringBuilder output, TomlTable table, string path)
        {
            foreach (var key in table.Keys)
            {
                var value = table[key];
                if (value is TomlTable || value is List<TomlTable>) continue;
                output.Append(Key(key)).Append(" = ").Append(Value(value)).Append('\n');
            }
            foreach (var key in table.Keys)
            {
                var name = path == null ? Key(key) : path + "." + Key(key);
                switch (table[key])
                {
                    case TomlTable child:
                        output.Append('\n').Append('[').Append(name).Append("]\n");
                        WriteTable(output, child, name);
                        break;
                    case List<TomlTable> list:
                        foreach (var item in list)
                        {
                            output.Append('\n').Append("[[").Append(name).Append("]]\n");
                            WriteTable(output, item, name);
                        }
                        break;
                }
            }
        }

        private static string Key(string key)
        {
            foreach (var c in key)
            {
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-') return Quote(key);
            }
            return key.Length == 0 ? "\"\"" : key;
        }

        private static string Value(object value)
        {
            switch (value)
            {
                case string text: return Quote(text);
                case bool flag: return flag ? "true" : "false";
                case long or int or short or byte: return Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture);
                case double or float:
                {
                    var real = Convert.ToDouble(value);
                    if (double.IsNaN(real)) return "nan";
                    if (double.IsInfinity(real)) return real > 0 ? "inf" : "-inf";
                    var formatted = real.ToString("R", CultureInfo.InvariantCulture);
                    return formatted.Contains(".") || formatted.Contains("E") ? formatted : formatted + ".0";
                }
                case System.Collections.IEnumerable list:
                {
                    var parts = new List<string>();
                    foreach (var item in list) parts.Add(Value(item));
                    return "[" + string.Join(", ", parts) + "]";
                }
                default: throw new ArgumentException("Can not write " + value.GetType() + " to TOML");
            }
        }

        private static string Quote(string text)
        {
            var result = new StringBuilder("\"");
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 0x20) result.Append("\\u").Append(((int)c).ToString("X4"));
                        else result.Append(c);
                        break;
                }
            }
            return result.Append('"').ToString();
        }
    }
}
