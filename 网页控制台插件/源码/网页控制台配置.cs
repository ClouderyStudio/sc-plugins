// 网页控制台插件 —— 配置与配置读写。
//
// 从基础插件的「统一配置」里摘出来的独立版本：
// 原来它是 BaseConfig 的一个段（WebPanel），跟基础插件共用一份 基础插件配置.json。
// 分离成独立插件后改成自己一份 网页控制台配置.json，好处是：
//   1. 改面板设置不用去动基础插件那份（那份线上是独立调过的，见 README 注意事项）；
//   2. 单独部署这个插件时不会依赖基础插件的配置结构。

using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Newtonsoft.Json;

namespace ScWebPanel
{
    /// <summary>
    /// 网页控制台的配置。
    ///
    /// ⚠ 安全前提（改这个类时务必保持）：
    /// - 面板登录后可以**执行服务端命令**。命令走 <c>CmdManager.HandleMessage(..., isTerminal: true)</c>，
    ///   而那条路会**整个跳过 AuthLevel 检查**（核心按"来自终端"处理）。也就是说：
    ///   面板自己的登录口令 + <see cref="AllowedCommandPrefixes"/> 是**唯一**的闸门。
    /// - 因此 <see cref="BindHost"/> 默认只绑 <c>127.0.0.1</c>（只有本机能访问）。
    ///   要让外网访问，必须显式改成 <c>+</c> 或 <c>*</c>，并且**一定要先设好强口令**。
    /// - <see cref="Password"/> 留空时插件**拒绝启动** —— 宁可不开，也不开一个无鉴权的命令终端。
    /// </summary>
    public sealed class WebPanelConfig
    {
        /// <summary>总开关。改这个要重启服务端（只在 Initialize 时读一次）。</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 监听地址。<c>127.0.0.1</c> = 仅本机；<c>+</c> 或 <c>*</c> = 所有网卡（外网可访问）。
        /// ⚠ HttpListener 的 <c>+</c>/<c>*</c> 前缀在 Windows 上需要 URL ACL 授权
        /// （管理员执行 <c>netsh http add urlacl url=http://+:8080/ user=Everyone</c>），
        /// 否则 <c>Start()</c> 会抛 <c>HttpListenerException</c>。真报错时日志里会写清楚。
        /// </summary>
        public string BindHost { get; set; } = "127.0.0.1";

        /// <summary>监听端口。**可配置**是这个功能的硬要求。</summary>
        public int Port { get; set; } = 8080;

        /// <summary>
        /// 登录口令。支持两种写法：明文，或 <c>sha256:&lt;64位小写十六进制&gt;</c> 只放哈希。
        /// 留空 = 插件拒绝启动。
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>登录一次有效多久（分钟）。到期要重新登录。</summary>
        public int SessionMinutes { get; set; } = 120;

        /// <summary>连续登录失败几次就拒绝这个来源 IP 一段时间（0 = 不封）。</summary>
        public int MaxLoginFailures { get; set; } = 10;

        /// <summary>触发上面的失败上限后，封这个 IP 多少秒。</summary>
        public int LockoutSeconds { get; set; } = 300;

        /// <summary>
        /// 网页终端允许执行的命令白名单（**前缀匹配，整词**；逗号分隔，不带前导斜杠）。
        ///
        /// ⚠ 这里是**唯一**的权限闸门（见类注释）。默认刻意收窄到"看和管人"，
        /// **不含** <c>stop</c>/<c>ban</c> 这类高杀伤命令；要放开就显式往这里加。
        /// 留空 = 网页终端一条命令都不许执行。
        /// </summary>
        public string AllowedCommandPrefixes { get; set; } =
            "help,list,who,base,shop,market,admin,tp,tp2,back,respawn,clear,time,kill,kick,give";

        /// <summary>
        /// 额外**明确禁止**的命令前缀（优先级高于白名单，用于"白名单里有 abc，但 abc 的某个子命令要禁"）。
        /// </summary>
        public string DeniedCommandPrefixes { get; set; } = "stop,ban";

        /// <summary>终端保留多少行日志（环形缓冲，超出丢最旧的）。</summary>
        public int LogBufferLines { get; set; } = 2000;

        /// <summary>
        /// 实时日志的推送方式：true = SSE 长连接（服务端推，秒级到达、省请求），
        /// false = 前端 2 秒轮询。改这个要重启服务端。
        /// 前端在 SSE 连不上时会自动退回轮询，所以开着也不用担心日志页空白。
        /// </summary>
        public bool UseServerSentEvents { get; set; } = true;

        /// <summary>单个 HTTP 请求最长等待秒数（防慢连接把线程占住）。</summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>记录每次网页登录与网页执行命令（排查"谁在面板上动了什么"靠它）。</summary>
        public bool LogActions { get; set; } = true;

        /// <summary>面板标题（显示在页面左上角与浏览器标题栏）。</summary>
        public string Title { get; set; } = "服务器控制台";

        public void Clamp()
        {
            if (string.IsNullOrWhiteSpace(BindHost)) BindHost = "127.0.0.1";
            BindHost = BindHost.Trim();

            // 端口合法区间：避开 0（随机）与 1024 以下（需要特权，且容易撞系统服务）
            if (Port < 1024) Port = 8080;
            if (Port > 65535) Port = 8080;

            if (Password == null) Password = "";
            if (SessionMinutes < 1) SessionMinutes = 1;
            if (SessionMinutes > 10080) SessionMinutes = 10080;      // 一周封顶
            if (MaxLoginFailures < 0) MaxLoginFailures = 0;
            if (LockoutSeconds < 0) LockoutSeconds = 0;
            if (LogBufferLines < 100) LogBufferLines = 100;
            if (LogBufferLines > 100000) LogBufferLines = 100000;
            if (RequestTimeoutSeconds < 5) RequestTimeoutSeconds = 5;
            if (RequestTimeoutSeconds > 300) RequestTimeoutSeconds = 300;
            if (AllowedCommandPrefixes == null) AllowedCommandPrefixes = "";
            if (DeniedCommandPrefixes == null) DeniedCommandPrefixes = "";
            if (string.IsNullOrWhiteSpace(Title)) Title = "服务器控制台";
            if (Title.Length > 40) Title = Title.Substring(0, 40);
        }

        /// <summary>登录口令是否已配置（留空视为没有，插件会拒绝启动）。</summary>
        public bool HasPassword => !string.IsNullOrWhiteSpace(Password);

        /// <summary>按逗号/分号/空白拆前缀表，去掉空项与可能手写的前导斜杠。</summary>
        public static string[] SplitPrefixes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new string[0];
            var parts = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var value = part.Trim().TrimStart('/').Trim();
                if (value.Length > 0) list.Add(value);
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// 配置读写。写盘走 .partial 再覆盖（原子写入），避免写一半断电留下半截 JSON。
    ///
    /// 读失败/反序列化出 null 时**保留默认配置并落盘一份**，而不是拿着 null 往下走 ——
    /// 后者会让后面的判空散落各处，一个漏判就是每帧刷日志。
    /// </summary>
    internal static class WebPanelConfigStore
    {
        public static WebPanelConfig Load(string path)
        {
            WebPanelConfig config = null;
            try
            {
                if (File.Exists(path))
                {
                    config = JsonConvert.DeserializeObject<WebPanelConfig>(File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[网页控制台] 配置读取失败，本次用默认值：" + ex.Message);
            }

            if (config == null)
            {
                config = new WebPanelConfig();
                Log.Warning("[网页控制台] 配置文件不存在或无法解析，已按默认配置处理（默认不启用）");
            }

            config.Clamp();
            return config;
        }

        public static void Save(string path, WebPanelConfig config)
        {
            try
            {
                config.Clamp();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string temp = path + ".partial";
                File.WriteAllText(temp, JsonConvert.SerializeObject(config, Formatting.Indented));
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                Log.Error("[网页控制台] 配置写入失败：" + ex.Message);
            }
        }
    }
}
