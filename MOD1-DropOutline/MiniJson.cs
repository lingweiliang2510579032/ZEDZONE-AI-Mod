using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dropoutline
{
    /// <summary>
    /// 极简 JSON 解析/序列化（只覆盖 LLM 请求响应所需的子集）：
    /// 对象 → Dictionary&lt;string,object&gt;，数组 → List&lt;object&gt;，字符串/数字/布尔/null。
    /// 不引第三方库，避免给插件加依赖。
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            var v = ParseValue(json, ref i);
            return v;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    if (Match(s, i, "true")) { i += 4; return true; }
                    break;
                case 'f':
                    if (Match(s, i, "false")) { i += 5; return false; }
                    break;
                case 'n':
                    if (Match(s, i, "null")) { i += 4; return null; }
                    break;
                default:
                    return ParseNumber(s, ref i);
            }
            throw new FormatException("JSON 解析失败 @" + i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); i += 2; break;
                        case 'r': sb.Append('\r'); i += 2; break;
                        case 't': sb.Append('\t'); i += 2; break;
                        case 'b': sb.Append('\b'); i += 2; break;
                        case 'f': sb.Append('\f'); i += 2; break;
                        case '"': sb.Append('"'); i += 2; break;
                        case '\\': sb.Append('\\'); i += 2; break;
                        case '/': sb.Append('/'); i += 2; break;
                        case 'u':
                            if (i + 5 < s.Length &&
                                int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                i += 6;
                            }
                            else i += 2;
                            break;
                        default: sb.Append(n); i += 2; break;
                    }
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            var token = s.Substring(start, i - start);
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static bool Match(string s, int i, string lit) =>
            i + lit.Length <= s.Length && string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0;

        // ───────── 取值辅助 ─────────

        public static Dictionary<string, object> AsObj(object o) => o as Dictionary<string, object>;

        public static List<object> AsList(object o) => o as List<object>;

        public static string Str(object o) => o as string;

        public static object Get(object obj, params object[] path)
        {
            object cur = obj;
            foreach (var step in path)
            {
                if (cur == null) return null;
                if (step is int idx)
                {
                    var list = AsList(cur);
                    if (list == null || idx < 0 || idx >= list.Count) return null;
                    cur = list[idx];
                }
                else
                {
                    var d = AsObj(cur);
                    if (d == null) return null;
                    d.TryGetValue(step.ToString(), out cur);
                }
            }
            return cur;
        }

        // ───────── 序列化 ─────────

        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
