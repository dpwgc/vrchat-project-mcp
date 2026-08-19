// =================================================================================================
// MiniJson.cs
// 极简 JSON 序列化 / 反序列化器（零依赖实现）
// -------------------------------------------------------------------------------------------------
// 设计说明：
//   1. 本插件坚持"最简依赖"，不引入 Newtonsoft.Json 等第三方库，内置一个完整的小型 JSON 解析器；
//   2. 仅使用 .NET Standard 2.1 基础 API 与 C# 9 语法，兼容 Unity 2022 与 Unity 6；
//   3. JsonObject / JsonArray 是本插件内部通用数据模型：所有 MCP 工具的参数与返回值都使用它们组织，
//      序列化器额外兼容 string / bool / 数值 / 枚举 / IDictionary / IEnumerable 等常见 .NET 类型。
// =================================================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VrchatProjectMcp.Core.Json
{
    /// <summary>
    /// JSON 对象模型：键为字符串、值为任意 JSON 兼容数据（string / bool / 数值 / JsonObject / JsonArray / null）。
    /// 提供类型安全的取值辅助方法，方便工具代码编写。
    /// </summary>
    public sealed class JsonObject : Dictionary<string, object>
    {
        /// <summary>创建空对象。</summary>
        public JsonObject()
        {
        }

        /// <summary>从一个字符串字典拷贝构造（键值浅拷贝）。</summary>
        public JsonObject(IDictionary<string, object> source) : base(source)
        {
        }

        /// <summary>链式写法：设置一个键值并返回自身。</summary>
        public JsonObject Set(string key, object value)
        {
            this[key] = value;
            return this;
        }

        /// <summary>字段是否存在且值不为 null。</summary>
        public bool Has(string key)
        {
            return ContainsKey(key) && this[key] != null;
        }

        /// <summary>读取字符串字段（非字符串值按不变文化转换）。</summary>
        public string GetString(string key, string defaultValue = null)
        {
            if (!TryGetValue(key, out object v) || v == null) return defaultValue;
            if (v is string s) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>读取布尔字段（兼容 "true"/"false"/1/0 等形式）。</summary>
        public bool GetBool(string key, bool defaultValue = false)
        {
            if (!TryGetValue(key, out object v) || v == null) return defaultValue;
            if (v is bool b) return b;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return defaultValue;
        }

        /// <summary>读取 64 位整数字段。</summary>
        public long GetLong(string key, long defaultValue = 0)
        {
            if (!TryGetValue(key, out object v) || v == null) return defaultValue;
            try
            {
                if (v is string s)
                {
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return (long)Math.Round(d);
                    return defaultValue;
                }
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>读取双精度浮点字段。</summary>
        public double GetDouble(string key, double defaultValue = 0)
        {
            if (!TryGetValue(key, out object v) || v == null) return defaultValue;
            try
            {
                if (v is string s)
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                    return defaultValue;
                }
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>读取嵌套对象字段（不存在或类型不符时返回 null）。</summary>
        public JsonObject GetObject(string key)
        {
            if (TryGetValue(key, out object v) && v is JsonObject jo) return jo;
            return null;
        }

        /// <summary>读取数组字段（不存在或类型不符时返回 null）。</summary>
        public JsonArray GetArray(string key)
        {
            if (TryGetValue(key, out object v) && v is JsonArray ja) return ja;
            return null;
        }
    }

    /// <summary>
    /// JSON 数组模型。
    /// </summary>
    public sealed class JsonArray : List<object>
    {
        /// <summary>创建空数组。</summary>
        public JsonArray()
        {
        }

        /// <summary>从已有集合构造。</summary>
        public JsonArray(IEnumerable<object> items) : base(items)
        {
        }

        /// <summary>链式写法：追加一个元素并返回自身。</summary>
        public JsonArray Push(object item)
        {
            Add(item);
            return this;
        }

        /// <summary>按索引读取字符串元素。</summary>
        public string GetString(int index, string defaultValue = null)
        {
            if (index < 0 || index >= Count) return defaultValue;
            object v = this[index];
            if (v == null) return defaultValue;
            if (v is string s) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>按索引读取对象元素。</summary>
        public JsonObject GetObject(int index)
        {
            if (index < 0 || index >= Count) return null;
            return this[index] as JsonObject;
        }

        /// <summary>按索引读取数组元素。</summary>
        public JsonArray GetArray(int index)
        {
            if (index < 0 || index >= Count) return null;
            return this[index] as JsonArray;
        }
    }

    /// <summary>
    /// 极简 JSON 解析与序列化入口。
    /// </summary>
    public static class MiniJson
    {
        // ------------------------------------------------------------------
        // 反序列化
        // ------------------------------------------------------------------

        /// <summary>
        /// 解析 JSON 文本，返回 JsonObject / JsonArray / string / double / long / bool / null 之一。
        /// 解析失败抛出带位置信息的 FormatException。
        /// </summary>
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var parser = new JsonParser(json);
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new FormatException("JSON 解析错误：位置 " + parser.Position + " 存在多余内容");
            return value;
        }

        /// <summary>内部递归下降解析器。</summary>
        private sealed class JsonParser
        {
            private readonly string _text;

            /// <summary>当前读取位置。</summary>
            public int Position;

            public JsonParser(string text)
            {
                _text = text ?? string.Empty;
                Position = 0;
            }

            /// <summary>是否已到文本末尾。</summary>
            public bool AtEnd
            {
                get { return Position >= _text.Length; }
            }

            /// <summary>跳过空白字符。</summary>
            public void SkipWhitespace()
            {
                while (!AtEnd && (char.IsWhiteSpace(_text[Position]))) Position++;
            }

            /// <summary>解析一个任意 JSON 值。</summary>
            public object ParseValue()
            {
                SkipWhitespace();
                if (AtEnd) throw Error("意外的文本结尾");
                char c = _text[Position];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') return ParseLiteral("true", true);
                if (c == 'f') return ParseLiteral("false", false);
                if (c == 'n') return ParseLiteral("null", null);
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                throw Error("意外的字符 '" + c + "'");
            }

            /// <summary>解析对象。</summary>
            private JsonObject ParseObject()
            {
                var obj = new JsonObject();
                Position++; // 跳过 '{'
                SkipWhitespace();
                if (!AtEnd && _text[Position] == '}')
                {
                    Position++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[Position] != '"') throw Error("对象键必须是字符串");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _text[Position] != ':') throw Error("缺少 ':'");
                    Position++;
                    object value = ParseValue();
                    obj[key] = value; // 重复键以后者为准
                    SkipWhitespace();
                    if (AtEnd) throw Error("对象未闭合");
                    char c = _text[Position];
                    if (c == ',') { Position++; continue; }
                    if (c == '}') { Position++; return obj; }
                    throw Error("对象内出现意外字符 '" + c + "'");
                }
            }

            /// <summary>解析数组。</summary>
            private JsonArray ParseArray()
            {
                var arr = new JsonArray();
                Position++; // 跳过 '['
                SkipWhitespace();
                if (!AtEnd && _text[Position] == ']')
                {
                    Position++;
                    return arr;
                }
                while (true)
                {
                    object value = ParseValue();
                    arr.Add(value);
                    SkipWhitespace();
                    if (AtEnd) throw Error("数组未闭合");
                    char c = _text[Position];
                    if (c == ',') { Position++; continue; }
                    if (c == ']') { Position++; return arr; }
                    throw Error("数组内出现意外字符 '" + c + "'");
                }
            }

            /// <summary>解析字符串（含转义与 \uXXXX，正确处理代理对）。</summary>
            private string ParseString()
            {
                Position++; // 跳过开头的 '"'
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("字符串未闭合");
                    char c = _text[Position];
                    if (c == '"') { Position++; return sb.ToString(); }
                    if (c == '\\')
                    {
                        Position++;
                        if (AtEnd) throw Error("转义序列不完整");
                        char esc = _text[Position];
                        Position++;
                        switch (esc)
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
                                char code = ReadUnicodeEscape();
                                // 高代理项后紧跟低代理项时合并为一个码点
                                if (char.IsHighSurrogate(code) && !AtEnd && _text[Position] == '\\' && Position + 1 < _text.Length && _text[Position + 1] == 'u')
                                {
                                    Position += 2;
                                    char low = ReadUnicodeEscape();
                                    if (char.IsLowSurrogate(low)) sb.Append(char.ConvertFromUtf32(char.ConvertToUtf32(code, low)));
                                    else { sb.Append(code); sb.Append(low); }
                                }
                                else sb.Append(code);
                                break;
                            default: throw Error("无效的转义字符 '\\" + esc + "'");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        Position++;
                    }
                }
            }

            /// <summary>读取 \uXXXX 形式的 4 位十六进制转义。</summary>
            private char ReadUnicodeEscape()
            {
                if (Position + 4 > _text.Length) throw Error("\\u 转义不完整");
                string hex = _text.Substring(Position, 4);
                if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code)) throw Error("无效的 \\u 转义: " + hex);
                Position += 4;
                return (char)code;
            }

            /// <summary>解析数字（整数返回 long，小数/科学计数返回 double）。</summary>
            private object ParseNumber()
            {
                int start = Position;
                while (!AtEnd)
                {
                    char c = _text[Position];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') Position++;
                    else break;
                }
                string token = _text.Substring(start, Position - start);
                if (token.IndexOf('.') < 0 && token.IndexOf('e') < 0 && token.IndexOf('E') < 0)
                {
                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                }
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw Error("无效的数字: " + token);
            }

            /// <summary>解析 true / false / null 字面量。</summary>
            private object ParseLiteral(string literal, object value)
            {
                if (Position + literal.Length > _text.Length || string.CompareOrdinal(_text, Position, literal, 0, literal.Length) != 0)
                    throw Error("无效的字面量");
                Position += literal.Length;
                return value;
            }

            /// <summary>构造带位置信息的解析错误。</summary>
            private FormatException Error(string message)
            {
                return new FormatException("JSON 解析错误（位置 " + Position + "）: " + message);
            }
        }

        // ------------------------------------------------------------------
        // 序列化
        // ------------------------------------------------------------------

        /// <summary>
        /// 将对象序列化为 JSON 文本。
        /// 支持：null / string / char / bool / 各类数值 / 枚举 / JsonObject / JsonArray /
        /// IDictionary / IEnumerable / DateTime；其他类型兜底转为字符串，保证不抛异常。
        /// </summary>
        public static string Serialize(object value, bool pretty = false)
        {
            var sb = new StringBuilder(256);
            SerializeValue(value, sb, pretty, 0);
            return sb.ToString();
        }

        /// <summary>递归序列化一个值。</summary>
        private static void SerializeValue(object value, StringBuilder sb, bool pretty, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            // 字符串
            if (value is string str) { WriteString(str, sb); return; }
            if (value is char ch) { WriteString(ch.ToString(), sb); return; }

            // 布尔
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }

            // 对象（JsonObject 优先，其次任意 IDictionary）
            if (value is JsonObject jo)
            {
                WriteObject(jo, sb, pretty, depth);
                return;
            }
            if (value is IDictionary dict)
            {
                var tmp = new JsonObject();
                foreach (DictionaryEntry entry in dict) tmp[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
                WriteObject(tmp, sb, pretty, depth);
                return;
            }

            // 数组（JsonArray 或任意可枚举，字符串除外）
            if (value is JsonArray ja)
            {
                WriteArray(ja, sb, pretty, depth);
                return;
            }
            if (value is IEnumerable enumerable)
            {
                var tmp = new JsonArray();
                foreach (object item in enumerable) tmp.Add(item);
                WriteArray(tmp, sb, pretty, depth);
                return;
            }

            // 枚举 → 名称字符串
            if (value is Enum enumValue)
            {
                WriteString(enumValue.ToString(), sb);
                return;
            }

            // 时间
            if (value is DateTime dt)
            {
                WriteString(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), sb);
                return;
            }

            // 数值（NaN/Infinity 序列化为 null，避免产生非法 JSON）
            if (value is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal dec)
            {
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            // 兜底：其余类型转字符串，保证序列化永不失败
            WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, sb);
        }

        /// <summary>写出一个 JSON 对象。</summary>
        private static void WriteObject(JsonObject obj, StringBuilder sb, bool pretty, int depth)
        {
            sb.Append('{');
            int index = 0;
            foreach (KeyValuePair<string, object> kv in obj)
            {
                if (index > 0) sb.Append(',');
                NewLineIndent(sb, pretty, depth + 1);
                WriteString(kv.Key, sb);
                sb.Append(pretty ? ": " : ":");
                SerializeValue(kv.Value, sb, pretty, depth + 1);
                index++;
            }
            if (index > 0) NewLineIndent(sb, pretty, depth);
            sb.Append('}');
        }

        /// <summary>写出一个 JSON 数组。</summary>
        private static void WriteArray(JsonArray arr, StringBuilder sb, bool pretty, int depth)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewLineIndent(sb, pretty, depth + 1);
                SerializeValue(arr[i], sb, pretty, depth + 1);
            }
            if (arr.Count > 0) NewLineIndent(sb, pretty, depth);
            sb.Append(']');
        }

        /// <summary>pretty 模式下输出换行与缩进。</summary>
        private static void NewLineIndent(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        /// <summary>写出带转义的 JSON 字符串。</summary>
        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        else if (c == '\u2028') sb.Append("\\u2028"); // JS 兼容：转义行/段分隔符
                        else if (c == '\u2029') sb.Append("\\u2029");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
