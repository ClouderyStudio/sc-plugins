// 网页控制台插件 —— 存档目录浏览 + 面板设置。
//
// 两块一起放这里，因为它们的共同点是"都直接和磁盘打交道"：
//   - 存档浏览：只读列目录 / 读文件，供管理员在面板里翻看 SC 的数据目录
//     （存档、日志、玩家数据都在里面，排查问题不用再远程 RDP 找文件）；
//   - 面板设置：读写本插件自己的 网页控制台配置.json，并支持"热重载"。
//
// ⚠ 安全边界（改这块务必保持）：
//   1. 存档浏览**只能读到数据根目录以内**。所有外部传入的相对路径都要先做规范化，
//      再确认结果仍落在根目录里 —— 否则 `../../` 就能翻到整台机器。
//   2. 面板设置里的**口令**是唯一闸门之一（见 WebPanelConfig 类注释），
//      所以 "改口令" 这个动作本身必须：① 已登录；② 旧口令校验通过（防止会话被劫持后直接换锁）。
//   3. 写配置只写本插件自己的文件，绝不去碰别人的配置。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Engine;

namespace ScWebPanel
{
    /// <summary>
    /// 面板设置的读取与写回。设置本身就是一份 <see cref="WebPanelConfig"/>，
    /// 存在插件目录下；这里只负责"取出来给页面看 / 把页面改的写回去"，
    /// 真正的生效（重启监听、换端口）由插件主体完成。
    /// </summary>
    public static class WebPanelSettings
    {
        /// <summary>
        /// 把配置序列化成给前端看的 JSON。<b>口令永不回传原文</b>，
        /// 只回一个 <c>hasPassword</c> 标志 —— 免得面板被人看到时连带口令泄露。
        /// </summary>
        public static string ToJson(WebPanelConfig config, int actualPort, bool running)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"success\":true");
            sb.Append(",\"enabled\":").Append(config.Enabled ? "true" : "false");
            sb.Append(",\"bindHost\":").Append(WebPanelJson.Quote(config.BindHost));
            sb.Append(",\"port\":").Append(config.Port);
            sb.Append(",\"actualPort\":").Append(actualPort > 0 ? actualPort.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"running\":").Append(running ? "true" : "false");
            sb.Append(",\"hasPassword\":").Append(config.HasPassword ? "true" : "false");
            // 口令是明文还是哈希，也给前端一个提示（决定"改成新口令"那栏怎么写）
            sb.Append(",\"passwordHashed\":")
              .Append(config.Password != null &&
                      config.Password.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
            sb.Append(",\"sessionMinutes\":").Append(config.SessionMinutes);
            sb.Append(",\"title\":").Append(WebPanelJson.Quote(config.Title));
            sb.Append(",\"useServerSentEvents\":").Append(config.UseServerSentEvents ? "true" : "false");
            sb.Append(",\"logBufferLines\":").Append(config.LogBufferLines);
            sb.Append(",\"logActions\":").Append(config.LogActions ? "true" : "false");
            sb.Append(",\"maxLoginFailures\":").Append(config.MaxLoginFailures);
            sb.Append(",\"lockoutSeconds\":").Append(config.LockoutSeconds);
            sb.Append(",\"requestTimeoutSeconds\":").Append(config.RequestTimeoutSeconds);
            sb.Append(",\"allowedCommandPrefixes\":").Append(WebPanelJson.Quote(config.AllowedCommandPrefixes));
            sb.Append(",\"deniedCommandPrefixes\":").Append(WebPanelJson.Quote(config.DeniedCommandPrefixes));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 应用前端提交的设置改动。返回 null 表示成功（<paramref name="error"/> 为 null）；
        /// 否则返回错误信息由调用方回给前端。**不改口令** —— 口令走单独的入口，
        /// 因为改口令必须先校验旧口令。
        ///
        /// 只接受请求里**显式出现**的字段：没出现的保持原值。这样"只改端口"不会
        /// 把标题之类的其它设置冲回默认。判断"出现过"用 <see cref="WebPanelJson.ParseString"/> 是否返回 null。
        /// </summary>
        public static bool TryApply(WebPanelConfig config, string body, out string error)
        {
            error = null;
            if (config == null) { error = "配置未加载"; return false; }

            // 端口
            string portRaw = WebPanelJson.ParseString(body, "port");
            if (portRaw != null)
            {
                if (!int.TryParse(portRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                {
                    error = "端口必须是数字";
                    return false;
                }
                if (port < 1024 || port > 65535)
                {
                    error = "端口需在 1024~65535 之间";
                    return false;
                }
                config.Port = port;
            }

            // 绑定地址：只允许 127.0.0.1 / localhost / + / * / 具体 IP，拒绝带协议的整段 URL
            string host = WebPanelJson.ParseString(body, "bindHost");
            if (host != null)
            {
                host = host.Trim();
                if (host.Contains("://") || host.Contains("/"))
                {
                    error = "绑定地址只能填主机名或 IP（如 127.0.0.1、+），不要带 http:// 或路径";
                    return false;
                }
                if (host.Length == 0) host = "127.0.0.1";
                config.BindHost = host;
            }

            string title = WebPanelJson.ParseString(body, "title");
            if (title != null)
            {
                config.Title = title.Trim();
            }

            string session = WebPanelJson.ParseString(body, "sessionMinutes");
            if (session != null && int.TryParse(session.Trim(), out int minutes))
            {
                config.SessionMinutes = minutes;
            }

            string logs = WebPanelJson.ParseString(body, "logBufferLines");
            if (logs != null && int.TryParse(logs.Trim(), out int lines))
            {
                config.LogBufferLines = lines;
            }

            string failures = WebPanelJson.ParseString(body, "maxLoginFailures");
            if (failures != null && int.TryParse(failures.Trim(), out int maxFail))
            {
                config.MaxLoginFailures = maxFail;
            }

            string lockout = WebPanelJson.ParseString(body, "lockoutSeconds");
            if (lockout != null && int.TryParse(lockout.Trim(), out int lockSec))
            {
                config.LockoutSeconds = lockSec;
            }

            string timeout = WebPanelJson.ParseString(body, "requestTimeoutSeconds");
            if (timeout != null && int.TryParse(timeout.Trim(), out int timeoutSec))
            {
                config.RequestTimeoutSeconds = timeoutSec;
            }

            string sse = WebPanelJson.ParseString(body, "useServerSentEvents");
            if (sse != null)
            {
                config.UseServerSentEvents = sse.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            string logActions = WebPanelJson.ParseString(body, "logActions");
            if (logActions != null)
            {
                config.LogActions = logActions.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            // 命令白/黑名单：允许直接编辑（这是面板权限的核心，给管理员自主调整的口子）
            string allowed = WebPanelJson.ParseString(body, "allowedCommandPrefixes");
            if (allowed != null) config.AllowedCommandPrefixes = allowed;

            string denied = WebPanelJson.ParseString(body, "deniedCommandPrefixes");
            if (denied != null) config.DeniedCommandPrefixes = denied;

            config.Clamp();
            return true;
        }

        /// <summary>
        /// 改口令。<b>必须先校验旧口令</b> —— 即便会话已被劫持，攻击者也不能直接把管理员锁在外面。
        /// 新口令可选是否存哈希（<paramref name="hash"/> = true 则只存 sha256）。
        /// </summary>
        public static bool TryChangePassword(WebPanelConfig config, string oldPassword, string newPassword,
            bool hash, out string error)
        {
            error = null;
            if (config == null) { error = "配置未加载"; return false; }

            // 只在"当前已有口令"时才要求旧口令 —— 首次设置口令不需要（那时也没有会话能到这一步）
            if (config.HasPassword && !WebPanelCrypto.VerifyPassword(oldPassword, config.Password))
            {
                error = "旧口令不正确";
                return false;
            }

            if (string.IsNullOrEmpty(newPassword))
            {
                error = "新口令不能为空（留空等于取消鉴权，面板不会允许）";
                return false;
            }

            if (newPassword.Length < 6)
            {
                error = "新口令至少 6 位";
                return false;
            }

            config.Password = hash ? "sha256:" + WebPanelCrypto.Sha256Hex(newPassword) : newPassword;
            return true;
        }
    }

    /// <summary>
    /// 存档目录浏览（只读）与面板设置读写。<b>纯磁盘操作，不碰游戏对象</b>，
    /// 因此不需要排队到主线程 —— HTTP 线程直接处理即可。
    /// </summary>
    public static class WebPanelFiles
    {
        /// <summary>数据根目录（SC 的 `app:/`，存档 / 日志 / 插件都在它下面）。</summary>
        public static string RootPath => Storage.GetSystemPath("app:/");

        /// <summary>
        /// 单次列目录返回的最大条目数。存档目录里动辄几千个区块文件，全吐给页面会把浏览器卡死；
        /// 截断时在响应里带 `truncated:true`，前端提示"只显示前 N 项"。
        /// </summary>
        private const int MaxEntries = 400;

        /// <summary>单个文件允许在线预览的最大字节数（超过就只给下载提示，不读进内存）。</summary>
        private const int MaxPreviewBytes = 256 * 1024;

        /// <summary>
        /// 列出某个目录下的条目。`relativePath` 为空 = 根目录。
        /// 只返回名字/大小/时间这类元信息，**不递归**（递归由前端逐层点进去）。
        /// </summary>
        public static string ListDirectory(string relativePath)
        {
            string root;
            string target;
            try
            {
                root = Path.GetFullPath(RootPath);
                if (!TryResolveInsideRoot(relativePath, out target, out string resolveError))
                {
                    return Error(resolveError);
                }

                if (!Directory.Exists(target))
                {
                    return Error("目录不存在：" + (relativePath ?? ""));
                }
            }
            catch (Exception ex)
            {
                return Error("路径解析失败：" + ex.Message);
            }

            var directories = new List<string>();
            var files = new List<string>();

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(target))
                {
                    directories.Add(dir);
                }
                foreach (string file in Directory.EnumerateFiles(target))
                {
                    files.Add(file);
                }
            }
            catch (Exception ex)
            {
                return Error("目录读取失败：" + ex.Message);
            }

            // 目录在前、文件在后，各自按名字排序（文件资源管理器的一贯顺序）
            directories.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            int total = directories.Count + files.Count;
            bool truncated = total > MaxEntries;

            var sb = new StringBuilder(8192);
            sb.Append("{\"success\":true");
            sb.Append(",\"root\":").Append(WebPanelJson.Quote(root));
            sb.Append(",\"path\":").Append(WebPanelJson.Quote(ToRelative(root, target)));
            sb.Append(",\"parent\":").Append(WebPanelJson.Quote(ToRelative(root, GetParentOrNull(target, root))));
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"entries\":[");

            bool first = true;
            int emitted = 0;

            foreach (string dir in directories)
            {
                if (emitted >= MaxEntries) break;
                AppendEntry(sb, ref first, root, dir, isDirectory: true);
                emitted++;
            }
            foreach (string file in files)
            {
                if (emitted >= MaxEntries) break;
                AppendEntry(sb, ref first, root, file, isDirectory: false);
                emitted++;
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 读取一个文件的文本内容（供面板里直接看存档 json / 日志 / 配置）。
        /// 二进制或超大文件会被拒绝，避免把二进制垃圾灌进页面。
        /// </summary>
        public static string ReadFile(string relativePath)
        {
            string root;
            string target;
            try
            {
                root = Path.GetFullPath(RootPath);
                if (!TryResolveInsideRoot(relativePath, out target, out string resolveError))
                {
                    return Error(resolveError);
                }

                if (!File.Exists(target))
                {
                    return Error("文件不存在：" + (relativePath ?? ""));
                }

                var info = new FileInfo(target);
                if (info.Length > MaxPreviewBytes)
                {
                    var oversized = new StringBuilder();
                    oversized.Append("{\"success\":false,\"message\":")
                        .Append(WebPanelJson.Quote(
                            $"文件太大（{FormatSize(info.Length)}），面板只在线预览 {FormatSize(MaxPreviewBytes)} 以内的文本"))
                        .Append(",\"size\":").Append(info.Length)
                        .Append('}');
                    return oversized.ToString();
                }

                byte[] bytes = File.ReadAllBytes(target);
                if (LooksBinary(bytes))
                {
                    var binary = new StringBuilder();
                    binary.Append("{\"success\":false,\"message\":")
                        .Append(WebPanelJson.Quote("这是二进制文件，无法在线预览"))
                        .Append(",\"size\":").Append(info.Length)
                        .Append('}');
                    return binary.ToString();
                }

                // UTF-8 读取；老存档里偶有 GBK 文件名内容，解不出的字节用替换符兜住，不让它抛。
                string text = new UTF8Encoding(false, false).GetString(bytes);

                var sb = new StringBuilder(text.Length + 256);
                sb.Append("{\"success\":true");
                sb.Append(",\"path\":").Append(WebPanelJson.Quote(ToRelative(root, target)));
                sb.Append(",\"name\":").Append(WebPanelJson.Quote(Path.GetFileName(target)));
                sb.Append(",\"size\":").Append(info.Length);
                sb.Append(",\"modified\":").Append(WebPanelJson.Quote(FormatTime(info.LastWriteTimeUtc)));
                sb.Append(",\"content\":").Append(WebPanelJson.Quote(text));
                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return Error("读取失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把外部传入的相对路径解析成根目录内的绝对路径。`..` 越界直接拒绝。
        /// 返回 false 时 <paramref name="error"/> 给出原因（会直接回给前端）。
        /// </summary>
        private static bool TryResolveInsideRoot(string relativePath, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            string root = Path.GetFullPath(RootPath);
            string combined = string.IsNullOrWhiteSpace(relativePath)
                ? root
                : Path.GetFullPath(Path.Combine(root, relativePath.Trim().Replace('/', Path.DirectorySeparatorChar)));

            // 关键校验：规范化之后必须仍以根目录开头（含分隔符），否则就是越界。
            // 比较时给根补一个分隔符，避免 "C:\data-evil" 被误判成在 "C:\data" 里。
            string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(combined.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "路径越界：只能浏览数据目录以内的文件";
                return false;
            }

            fullPath = combined;
            return true;
        }

        private static string GetParentOrNull(string target, string root)
        {
            if (string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return null;   // 已在根目录，没有上一层
            }
            return Path.GetDirectoryName(target);
        }

        private static void AppendEntry(StringBuilder sb, ref bool first, string root, string path, bool isDirectory)
        {
            string name = Path.GetFileName(path);
            if (!first) sb.Append(',');
            first = false;

            sb.Append('{');
            sb.Append("\"name\":").Append(WebPanelJson.Quote(name));
            sb.Append(",\"path\":").Append(WebPanelJson.Quote(ToRelative(root, path)));
            sb.Append(",\"dir\":").Append(isDirectory ? "true" : "false");

            if (isDirectory)
            {
                // 目录不算大小（递归算太贵），只给个"—"由前端显示
                sb.Append(",\"size\":null");
                sb.Append(",\"modified\":null");
                sb.Append(",\"kind\":").Append(WebPanelJson.Quote("目录"));
            }
            else
            {
                try
                {
                    var info = new FileInfo(path);
                    sb.Append(",\"size\":").Append(info.Length);
                    sb.Append(",\"sizeText\":").Append(WebPanelJson.Quote(FormatSize(info.Length)));
                    sb.Append(",\"modified\":").Append(WebPanelJson.Quote(FormatTime(info.LastWriteTimeUtc)));
                }
                catch
                {
                    sb.Append(",\"size\":null,\"sizeText\":").Append(WebPanelJson.Quote("-"))
                      .Append(",\"modified\":null");
                }

                string ext = Path.GetExtension(name) ?? "";
                sb.Append(",\"kind\":").Append(WebPanelJson.Quote(KindOf(ext)));
                // 只有文本类才允许点开预览，前端据此决定"查看"按钮是否可点
                sb.Append(",\"previewable\":").Append(IsPreviewable(ext) ? "true" : "false");
            }

            sb.Append('}');
        }

        private static string KindOf(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".json": return "JSON";
                case ".log":
                case ".txt": return "文本";
                case ".meta": return "元数据";
                case ".dat":
                case ".bin": return "数据";
                case ".zip":
                case ".gz": return "压缩包";
                case ".dll": return "程序库";
                case ".png":
                case ".jpg":
                case ".jpeg": return "图片";
                default: return string.IsNullOrEmpty(ext) ? "文件" : ext.TrimStart('.').ToUpperInvariant();
            }
        }

        private static bool IsPreviewable(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".json":
                case ".log":
                case ".txt":
                case ".meta":
                case ".csv":
                case ".xml":
                case ".md":
                case ".ini":
                case ".cfg":
                case ".yml":
                case ".yaml":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>粗略判断是否二进制：扫前 8KB，含 NUL 字节即视为二进制。</summary>
        private static bool LooksBinary(byte[] bytes)
        {
            int limit = Math.Min(bytes.Length, 8192);
            for (int i = 0; i < limit; i++)
            {
                if (bytes[i] == 0) return true;
            }
            return false;
        }

        private static string ToRelative(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string rel = Path.GetRelativePath(root, path);
                return rel == "." ? "" : rel.Replace('\\', '/');
            }
            catch
            {
                return path;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        private static string FormatTime(DateTime utc)
        {
            // 统一按服务器本地时间显示（管理员看的就是这台机器的时间）
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Error(string message)
        {
            return "{\"success\":false,\"message\":" + WebPanelJson.Quote(message ?? "未知错误") + "}";
        }
    }
}
