using System;
using System.Globalization;
using System.Text;

namespace ScWebPanel
{
    /// <summary>
    /// 极简 JSON 输出/解析。**故意不引 Newtonsoft**：
    /// 插件已经用它读配置，但那个是给强类型对象用的；面板要手写 JSON 流（避免为了拼一串
    /// 状态数据去建一堆 DTO），手写就必须有一个可靠的"字符串转义"函数 —— 玩家名、
    /// 聊天内容、日志文本里什么都可能有，转义漏一个字符整个响应就废了。
    /// </summary>
    public static class WebPanelJson
    {
        /// <summary>把字符串转义成 JSON 字面量（含两侧引号）。</summary>
        public static string Quote(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder(value.Length + 16);
            sb.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // 控制字符必须转义，否则 JSON 非法
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 从请求体里取一个顶层字符串字段。只做够用的解析，不处理嵌套（面板的请求体都是扁平的）。
        /// 解析失败返回 null。
        /// </summary>
        public static string ParseString(string json, string field)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            string key = "\"" + field + "\"";
            int index = json.IndexOf(key, StringComparison.Ordinal);
            if (index < 0) return null;

            index += key.Length;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length || json[index] != ':') return null;
            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;

            // 也允许 "field": true / 123 这类非字符串值，按原文返回
            if (index < json.Length && json[index] == '"')
            {
                index++;
                var sb = new StringBuilder();
                while (index < json.Length)
                {
                    char ch = json[index];
                    if (ch == '\\' && index + 1 < json.Length)
                    {
                        char next = json[index + 1];
                        switch (next)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'u':
                                if (index + 5 < json.Length &&
                                    int.TryParse(json.Substring(index + 2, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out int code))
                                {
                                    sb.Append((char)code);
                                    index += 6;
                                    continue;
                                }
                                break;
                        }
                        index += 2;
                        continue;
                    }
                    if (ch == '"') break;
                    sb.Append(ch);
                    index++;
                }
                return sb.ToString();
            }

            // 非字符串：读到分隔符为止
            int start = index;
            while (index < json.Length && json[index] != ',' && json[index] != '}') index++;
            return json.Substring(start, index - start).Trim();
        }

        /// <summary>数字字段。</summary>
        public static long ParseLong(string json, string field, long fallback)
        {
            string raw = ParseString(json, field);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            return long.TryParse(raw.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : fallback;
        }

        /// <summary>
        /// 时间跨度格式化（给玩家在线时长这类显示用）。
        /// </summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";

            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}天{span.Hours}小时";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}小时{span.Minutes}分";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}分{span.Seconds}秒";
            return $"{span.Seconds}秒";
        }
    }
}
