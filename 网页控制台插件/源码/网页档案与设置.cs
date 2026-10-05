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
        ///
        /// ⚠ 可见性为 internal 是给同程序集内的 <see cref="WebPanelConnections"/> 用的
        /// （它要从根目录读 Bugs/Game.log，走同一条越界校验，不另写一套）。
        /// 对外暴露的仍然是 <see cref="ListDirectory"/> / <see cref="ReadFile"/>，别把本方法直接接到 HTTP 上。
        /// </summary>
        internal static bool TryResolveInsideRoot(string relativePath, out string fullPath, out string error)
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

    /// <summary>
    /// IP 与账号的关联统计。**给"封 IP 前先看看会不会连坐"用的。**
    ///
    /// 为什么需要它：核心的 `/ban ip add` 是**按网络出口地址**封的。同一个 IP 后面可能坐着
    /// 一家人 / 一个宿舍 / 一个网吧 —— 直接封就是把这些人一起挡在门外。
    /// 所以封之前必须先回答一个问题：**这个 IP 关联了几个不同的账号？**
    ///   1 个  = 独占，可以放心封；
    ///   &gt;1 个 = 共用，封它会连坐，要慎重（或改封账号）。
    ///
    /// 数据从哪来：核心每次接受连接都会写一行
    /// <c>[连接请求] 接受客户端 &lt;ip&gt;:&lt;port&gt; 连接: ID=n, 昵称=&lt;名&gt;, GUID=&lt;guid&gt;</c>
    /// 所以扫日志就能把「IP → 见过哪些 GUID」这条关系重建出来。
    ///
    /// 两个来源合并：
    ///   - 内存里的日志缓冲（<see cref="WebPanelLogSink"/>，本次启动以来的）
    ///   - 磁盘上的 <c>Bugs/Game.log</c> **尾部若干 MB**（跨重启的历史）
    /// ⚠️ 磁盘日志可能上百 MB，**绝不能整个读进内存**，这里只倒着读尾部一段（见 <see cref="TailLines"/>）。
    /// </summary>
    public static class WebPanelConnections
    {
        /// <summary>磁盘日志最多从尾部读多少字节。够覆盖最近几千次连接，又不至于把服务器读卡。</summary>
        private const long TailBytes = 8L * 1024 * 1024;

        /// <summary>结果缓存时长：这段时间内重复请求直接返回上次的结果，避免每次都去扫日志。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private static readonly object CacheLock = new object();
        private static string _cachedJson;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        /// <summary>日志文件相对数据根目录的位置（核心固定写这里）。</summary>
        private const string LogRelativePath = "Bugs/Game.log";

        /// <summary>
        /// 汇总「IP → 关联的账号」列表，返回 JSON 给前端。
        /// 结构：{ success, scanned, ips: [{ ip, guids, names, count, shared }], note }
        /// </summary>
        /// <param name="memoryLogs">
        /// 本次启动以来的内存日志（来自 <see cref="WebPanelLogSink.SnapshotTexts"/>）。
        /// 传 null 就只用磁盘日志 —— 由调用方给，别在这里挂全局单例。
        /// </param>
        public static string Build(string[] memoryLogs, bool forceRefresh)
        {
            lock (CacheLock)
            {
                if (!forceRefresh && _cachedJson != null &&
                    DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                {
                    return _cachedJson;
                }
            }

            try
            {
                // ip -> (guid 集合, 昵称集合)
                var map = new Dictionary<string, IpRecord>(StringComparer.OrdinalIgnoreCase);
                int scanned = 0;

                // 来源 1：本次启动以来的内存日志
                if (memoryLogs != null)
                {
                    foreach (string line in memoryLogs)
                    {
                        scanned++;
                        ParseConnectionLine(line, map);
                    }
                }

                // 来源 2：磁盘日志尾部（跨重启）
                foreach (string line in TailLines(LogRelativePath, TailBytes))
                {
                    scanned++;
                    ParseConnectionLine(line, map);
                }

                // 排序：关联账号多的排前面（那些正是"封了会连坐"的高危 IP）
                var ordered = map.Values
                    .OrderByDescending(r => r.Guids.Count)
                    .ThenBy(r => r.Ip, StringComparer.Ordinal)
                    .ToList();

                var sb = new StringBuilder(8192);
                sb.Append("{\"success\":true,\"scanned\":").Append(scanned);
                sb.Append(",\"ipCount\":").Append(ordered.Count);
                sb.Append(",\"sharedCount\":").Append(ordered.Count(r => r.Guids.Count > 1));
                sb.Append(",\"ips\":[");
                bool first = true;
                foreach (var r in ordered)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"ip\":").Append(WebPanelJson.Quote(r.Ip));
                    sb.Append(",\"count\":").Append(r.Guids.Count);
                    // shared = 该 IP 后面不止一个账号 ⇒ 封它必然连坐
                    sb.Append(",\"shared\":").Append(r.Guids.Count > 1 ? "true" : "false");
                    AppendStringArray(sb, "guids", r.Guids);
                    AppendStringArray(sb, "names", r.Names);
                    sb.Append('}');
                }
                sb.Append("]}");

                string json = sb.ToString();
                lock (CacheLock)
                {
                    _cachedJson = json;
                    _cachedAtUtc = DateTime.UtcNow;
                }
                return json;
            }
            catch (Exception ex)
            {
                return "{\"success\":false,\"message\":" + WebPanelJson.Quote("统计连接记录失败：" + ex.Message) + "}";
            }
        }

        private sealed class IpRecord
        {
            public string Ip;
            // 用集合去重：同一个人反复上线不该被算成多个账号
            public readonly HashSet<string> Guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 从一行日志里抠出 ip / GUID / 昵称。不是连接行就直接返回。
        ///
        /// 目标格式（核心原样输出，字段名可能随版本微调，所以解析写得宽松些）：
        /// <c>[连接请求] 接受客户端 1.2.3.4:5678 连接: ID=1, 昵称=某人, GUID=xxxx-...</c>
        /// </summary>
        private static void ParseConnectionLine(string line, Dictionary<string, IpRecord> map)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (line.IndexOf("接受客户端", StringComparison.Ordinal) < 0) return;

            string guid = ExtractField(line, "GUID=");
            if (string.IsNullOrEmpty(guid)) return;

            // ip 在 "接受客户端 " 之后、" 连接" 之前；其中带 :port，要切掉
            int at = line.IndexOf("接受客户端 ", StringComparison.Ordinal);
            if (at < 0) return;
            int from = at + "接受客户端 ".Length;
            int to = line.IndexOf(' ', from);
            if (to <= from) return;

            string hostPort = line.Substring(from, to - from).Trim();
            string ip = StripPort(hostPort);
            if (string.IsNullOrEmpty(ip)) return;

            string name = ExtractField(line, "昵称=");

            if (!map.TryGetValue(ip, out IpRecord rec))
            {
                rec = new IpRecord { Ip = ip };
                map[ip] = rec;
            }
            rec.Guids.Add(guid);
            if (!string.IsNullOrEmpty(name)) rec.Names.Add(name);
        }

        /// <summary>取 <c>key=</c> 之后到下一个逗号（或行尾）之间的值。</summary>
        private static string ExtractField(string line, string key)
        {
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            int from = at + key.Length;
            int to = line.IndexOf(',', from);
            if (to < 0) to = line.Length;
            return line.Substring(from, to - from).Trim();
        }

        /// <summary>
        /// 去掉 <c>:端口</c>。IPv6 形如 <c>[::1]:8080</c> 或 <c>::1</c>，
        /// 所以不能简单 Split(':') —— 只有出现方括号或"恰好一个冒号且后面全是数字"才切。
        /// </summary>
        private static string StripPort(string hostPort)
        {
            if (string.IsNullOrEmpty(hostPort)) return null;

            // [v6]:port
            if (hostPort.StartsWith("[", StringComparison.Ordinal))
            {
                int close = hostPort.IndexOf(']');
                return close > 0 ? hostPort.Substring(1, close - 1) : hostPort.Trim('[', ']');
            }

            int firstColon = hostPort.IndexOf(':');
            int lastColon = hostPort.LastIndexOf(':');
            // 只有一个冒号 ⇒ 是 v4:port，切掉
            if (firstColon >= 0 && firstColon == lastColon)
            {
                string portPart = hostPort.Substring(lastColon + 1);
                bool allDigits = portPart.Length > 0;
                foreach (char c in portPart)
                {
                    if (c < '0' || c > '9') { allDigits = false; break; }
                }
                return allDigits ? hostPort.Substring(0, lastColon) : hostPort;
            }
            // 多个冒号 ⇒ 裸 IPv6，原样返回
            return hostPort;
        }

        /// <summary>
        /// 从文件尾部倒着读，返回最后那些行（按文件顺序）。
        ///
        /// 为什么不整个读：正式服的 Game.log 能到几十上百 MB，一次性 ReadAllLines
        /// 会在主线程附近造成明显卡顿和内存峰值。这里只读尾部 <paramref name="maxBytes"/> 字节，
        /// 并且**丢弃第一行**（可能被从中间截断）。
        /// </summary>
        private static IEnumerable<string> TailLines(string relativePath, long maxBytes)
        {
            if (!WebPanelFiles.TryResolveInsideRoot(relativePath, out string full, out _))
            {
                yield break;
            }
            if (!File.Exists(full)) yield break;

            var info = new FileInfo(full);
            long start = Math.Max(0, info.Length - maxBytes);

            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                {
                    bool first = true;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // 第一行很可能是被截断的半行，丢掉
                        if (first && start > 0) { first = false; continue; }
                        first = false;
                        yield return line;
                    }
                }
            }
        }

        private static void AppendStringArray(StringBuilder sb, string name, HashSet<string> values)
        {
            sb.Append(",\"").Append(name).Append("\":[");
            bool first = true;
            foreach (string v in values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(WebPanelJson.Quote(v));
            }
            sb.Append(']');
        }
    }
}
