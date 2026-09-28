using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MenuPrio
{
    /// <summary>
    /// Minimal JSON writer/reader for the history file, hand rolled so the tray
    /// process does not need System.Web.Extensions loaded.
    /// </summary>
    internal static class Json
    {
        // ---------------- writing ----------------

        public static string Write(HistoryData data)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"Groups\":{");

            bool first = true;
            foreach (var kv in data.Groups)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteGroup(sb, kv.Value);
            }

            sb.Append("},\"GroupOrder\":[");
            for (int i = 0; i < data.GroupOrder.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteString(sb, data.GroupOrder[i]);
            }

            sb.Append("],\"Events\":[");
            for (int i = 0; i < data.Events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteEvent(sb, data.Events[i]);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void WriteGroup(StringBuilder sb, HistoryGroup g)
        {
            sb.Append("{\"Key\":");
            WriteString(sb, g.Key);
            sb.Append(",\"Strict\":");
            sb.Append(g.Strict ? "true" : "false");
            sb.Append(",\"Candidates\":[");
            for (int i = 0; i < g.Candidates.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteCandidate(sb, g.Candidates[i]);
            }
            sb.Append("]}");
        }

        private static void WriteCandidate(StringBuilder sb, Candidate c)
        {
            sb.Append("{\"Name\":");
            WriteString(sb, c.Name);
            sb.Append(",\"Target\":");
            WriteString(sb, c.Target);
            sb.Append(",\"Args\":");
            WriteString(sb, c.Args);
            sb.Append(",\"WorkDir\":");
            WriteString(sb, c.WorkDir);
            sb.Append(",\"Count\":");
            sb.Append(c.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"LastUsed\":");
            WriteString(sb, c.LastUsed.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"Source\":");
            WriteString(sb, c.Source);
            sb.Append('}');
        }

        private static void WriteEvent(StringBuilder sb, HistoryEvent e)
        {
            sb.Append("{\"Time\":");
            WriteString(sb, e.Time.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"Typed\":");
            WriteString(sb, e.Typed);
            sb.Append(",\"Opened\":");
            WriteString(sb, e.Opened);
            sb.Append(",\"Target\":");
            WriteString(sb, e.Target);
            sb.Append(",\"Source\":");
            WriteString(sb, e.Source);
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- reading ----------------

        /// <summary>Returns null when the text is not a JSON object.</summary>
        public static HistoryData Read(string text)
        {
            int pos = 0;
            var root = ParseValue(text, ref pos) as Dictionary<string, object>;
            if (root == null) return null;

            var data = new HistoryData();

            var groups = GetObject(root, "Groups");
            if (groups != null)
            {
                foreach (var kv in groups)
                {
                    var g = ReadGroup(kv.Value);
                    if (g == null || string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.IsNullOrEmpty(g.Key)) g.Key = kv.Key;
                    data.Groups[kv.Key] = g;
                }
            }

            var order = GetArray(root, "GroupOrder");
            if (order != null)
            {
                foreach (var o in order)
                {
                    var s = o as string;
                    if (!string.IsNullOrEmpty(s)) data.GroupOrder.Add(s);
                }
            }

            var events = GetArray(root, "Events");
            if (events != null)
            {
                foreach (var o in events)
                {
                    var e = ReadEvent(o);
                    if (e != null) data.Events.Add(e);
                }
            }

            return data;
        }

        private static HistoryGroup ReadGroup(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;

            var g = new HistoryGroup();
            g.Key = GetString(d, "Key");
            g.Strict = GetBool(d, "Strict");

            var list = GetArray(d, "Candidates");
            if (list != null)
            {
                foreach (var c in list)
                {
                    var candidate = ReadCandidate(c);
                    if (candidate != null) g.Candidates.Add(candidate);
                }
            }
            return g;
        }

        private static Candidate ReadCandidate(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;

            return new Candidate
            {
                Name = GetString(d, "Name"),
                Target = GetString(d, "Target"),
                Args = GetString(d, "Args"),
                WorkDir = GetString(d, "WorkDir"),
                Count = GetInt(d, "Count"),
                LastUsed = GetDate(d, "LastUsed"),
                Source = GetString(d, "Source")
            };
        }

        private static HistoryEvent ReadEvent(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;

            return new HistoryEvent
            {
                Time = GetDate(d, "Time"),
                Typed = GetString(d, "Typed"),
                Opened = GetString(d, "Opened"),
                Target = GetString(d, "Target"),
                Source = GetString(d, "Source")
            };
        }

        // ---------------- parse helpers ----------------

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as Dictionary<string, object>;
        }

        private static List<object> GetArray(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as List<object>;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as string;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v is bool) return (bool)v;
            return false;
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v is long) return (int)(long)v;
            if (v is int) return (int)v;
            if (v is double) return (int)(double)v;
            return 0;
        }

        private static DateTime GetDate(Dictionary<string, object> d, string key)
        {
            var s = GetString(d, key);
            DateTime dt;
            if (!string.IsNullOrEmpty(s)
                && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                return dt;
            return DateTime.MinValue;
        }

        // ---------------- basic parser ----------------

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("json: unexpected end");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }

            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("json: expected key");
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("json: expected ':'");
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("json: unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("json: expected ',' or '}'");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("json: unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("json: expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // '"'
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("json: bad \\u escape");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("json: bad escape");
                }
            }
            throw new FormatException("json: unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("json: bad value");

            var tok = s.Substring(start, i - start);
            long l;
            if (tok.IndexOf('.') < 0 && tok.IndexOf('e') < 0 && tok.IndexOf('E') < 0
                && long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                return l;

            double d;
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("json: bad number");
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("json: expected " + word);
            i += word.Length;
        }
    }
}
