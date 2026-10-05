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

        // ==========================================
        // TLS（公网暴露）
        // ==========================================

        /// <summary>
        /// 是否启用 HTTPS 终结（<b>公网暴露必开</b>）。
        ///
        /// 为什么需要它：面板登录后能执行服务端命令，而命令走
        /// <c>CmdManager.HandleMessage(..., isTerminal:true)</c> ——整条路跳过 AuthLevel 检查。
        /// 走 HTTP 意味着<b>登录口令与会话 Cookie 都是明文过网</b>，任何中间人（FRP 节点、
        /// 运营商、被劫持的 WiFi）都能直接拿到命令终端的完整权限。
        ///
        /// 实现方式：插件内起一个 <c>TcpListener</c> + <see cref="System.Net.Security.SslStream"/>
        /// 在公网端口终结 TLS，把解密后的 HTTP 字节<b>透明转发</b>到绑在 127.0.0.1 的内部
        /// <c>HttpListener</c>。这样既拿到了 TLS，又<b>不用重写任何业务代码</b>，
        /// 也不用向系统证书库存东西、免管理员权限。
        /// </summary>
        public bool EnableTls { get; set; } = false;

        /// <summary>
        /// TLS 监听端口（公网对外暴露的就是这个端口）。
        /// 建议用 <b>443</b> 或其它高端口 —— 80/443 以下在部分环境需要特权，且容易撞系统服务。
        /// </summary>
        public int TlsPort { get; set; } = 8443;

        /// <summary>
        /// 证书来源：<c>auto</c> = 自签（首次启动自动生成并落盘，默认），
        /// <c>pfx</c> = 用 <see cref="CertificatePfxPath"/> 指定的证书。
        ///
        /// ⚠ <b>自签证书浏览器会显示"不安全"警告</b>，必须点"高级 → 继续前往"才能进。
        /// 这不妨碍加密效果（通道仍是真加密），但**首次使用要跟使用者说清楚**。
        /// 想彻底无警告需要买域名 + CA 签发证书，把 PFX 路径填进来即可，无需改代码。
        /// </summary>
        public string CertificateSource { get; set; } = "auto";

        /// <summary>证书文件路径（CertificateSource=pfx 时必填）。支持 .pfx / .p12。</summary>
        public string CertificatePfxPath { get; set; } = "";

        /// <summary>PFX 的打开口令（留空表示证书本身无口令）。</summary>
        public string CertificatePassword { get; set; } = "";

        /// <summary>
        /// 证书要覆盖的主机名（写入 SAN）。多个用逗号分隔，例如 <c>mc30.rhymc.com,mc30</c>。
        /// 留空则自动取 <see cref="BindHost"/> 与本机 FQDN —— 但注意公网访问用的是你的公网域名/IP，
        /// **这里填错会导致证书名不匹配，浏览器警告更严重**。
        /// </summary>
        public string CertificateHosts { get; set; } = "";

        /// <summary>
        /// 内部 HttpListener 端口（TLS 终结器把解密流量转到这里）。
        /// 保持 <b>127.0.0.1 绑定</b>，所以这个端口**永远不会被外网直接访问**，选什么值都行。
        /// 0 = 自动挑一个空闲端口。
        /// </summary>
        public int InternalPort { get; set; } = 0;

        /// <summary>TLS 并发连接上限（每个连接占一个线程，防连接耗尽）。</summary>
        public int MaxTlsConnections { get; set; } = 64;

        /// <summary>TLS 握手超时（秒）。握手很慢通常是有人在扫端口。</summary>
        public int TlsHandshakeTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// 登录口令。支持两种写法：明文，或 <c>sha256:&lt;64位小写十六进制&gt;</c> 只放哈希。
        /// 留空 = 插件拒绝启动。
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>登录一次有效多久（分钟）。到期要重新登录。</summary>
        public int SessionMinutes { get; set; } = 30;

        /// <summary>
        /// ⚠ <b>公网部署必读</b>：会话是否<b>绝对超时</b>（不因任何请求而延长）。
        ///
        /// 关闭时（false）每次请求都会把过期时间往后推，面板开着不碰也不会掉线；
        /// 开启时（true）到期即失效，必须重新输口令。
        /// 公网暴露时建议开启 —— 否则一个被"捡到"的令牌可以无限续命。
        /// </summary>
        public bool SessionAbsoluteTimeout { get; set; } = true;

        /// <summary>
        /// 单个会话的<b>绝对</b>存活上限（分钟），无论有没有在用。
        /// 防止"一直点着页面"把一次登录无限延长。0 = 不限（仅在关闭绝对超时时才有意义）。
        /// </summary>
        public int SessionMaxLifetimeMinutes { get; set; } = 240;

        /// <summary>连续登录失败几次就拒绝这个来源 IP 一段时间（0 = 不封）。</summary>
        public int MaxLoginFailures { get; set; } = 5;

        /// <summary>触发上面的失败上限后，封这个 IP 多少秒。</summary>
        public int LockoutSeconds { get; set; } = 900;

        /// <summary>
        /// <b>全服</b>失败次数上限：不管来自多少个不同 IP，累计失败超过这个数就<b>全局暂停登录</b>
        /// 一段时间。
        ///
        /// 存在的理由：<see cref="MaxLoginFailures"/> 是按 IP 记的，攻击者换代理池轮换 IP
        /// 就能<b>完全绕过</b>，无限次撞库。这个是全局的，绕不过。
        /// 0 = 关闭该保护。
        /// </summary>
        public int GlobalMaxLoginFailures { get; set; } = 30;

        /// <summary>触发全局上限后，暂停登录多少秒。</summary>
        public int GlobalLockoutSeconds { get; set; } = 900;

        /// <summary>
        /// 登录成功后是否把会话写进 <c>HttpOnly</c> Cookie。
        ///
        /// ⚠ 这是<b>公网部署的关键开关</b>：默认 false 时，SSE 因为 <c>EventSource</c>
        /// 不能自定义 header，只能把令牌放进 URL（<c>?token=xxx</c>），
        /// 而 URL 会流进浏览器历史、Referer、服务器/反代/FRP 日志 —— 等于半公开。
        /// 改成 true 后令牌只存在 Cookie 里，JS 读不到（HttpOnly），URL 里也不再出现。
        /// </summary>
        public bool UseHttpOnlyCookie { get; set; } = true;

        /// <summary>
        /// 登录成功后是否<b>禁止</b>通过 <c>?token=</c> 查询参数鉴权（默认禁止）。
        ///
        /// 留这个开关只为兼容老前端缓存；新前端走 Cookie + Authorization 头。
        /// 保持 false（=禁止）可彻底关掉"令牌进日志"这条路。
        /// </summary>
        public bool AllowTokenInQuery { get; set; } = false;

        /// <summary>
        /// 登录成功后是否下发 <c>Secure</c> 标记的 Cookie。
        /// 仅在<b>已经</b>通过 https 访问时才有意义（HTTP 下浏览器会忽略该标记）。
        /// </summary>
        public bool SecureCookie { get; set; } = false;

        /// <summary>
        /// 口令除 IP 限流外的第二道闸门：要求客户端在 <c>X-Panel-Challenge</c> 里回带
        /// 服务端下发的挑战值。用于区分"真人浏览器"与"直接打 HTTP 的脚本"。
        /// 见 <see cref="WebPanelChallenge"/>。
        /// </summary>
        public bool RequireChallenge { get; set; } = false;

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
            if (SessionMaxLifetimeMinutes < 0) SessionMaxLifetimeMinutes = 0;
            if (SessionMaxLifetimeMinutes > 10080) SessionMaxLifetimeMinutes = 10080;
            if (MaxLoginFailures < 0) MaxLoginFailures = 0;
            if (LockoutSeconds < 0) LockoutSeconds = 0;
            if (GlobalMaxLoginFailures < 0) GlobalMaxLoginFailures = 0;
            if (GlobalLockoutSeconds < 0) GlobalLockoutSeconds = 0;
            if (LogBufferLines < 100) LogBufferLines = 100;
            if (LogBufferLines > 100000) LogBufferLines = 100000;
            if (RequestTimeoutSeconds < 5) RequestTimeoutSeconds = 5;
            if (RequestTimeoutSeconds > 300) RequestTimeoutSeconds = 300;
            if (AllowedCommandPrefixes == null) AllowedCommandPrefixes = "";
            if (DeniedCommandPrefixes == null) DeniedCommandPrefixes = "";
            if (string.IsNullOrWhiteSpace(Title)) Title = "服务器控制台";
            if (Title.Length > 40) Title = Title.Substring(0, 40);

            // ---- TLS 相关 ----
            if (CertificateSource == null) CertificateSource = "auto";
            CertificateSource = CertificateSource.Trim().ToLowerInvariant();
            if (CertificateSource != "auto" && CertificateSource != "pfx") CertificateSource = "auto";
            if (CertificatePfxPath == null) CertificatePfxPath = "";
            if (CertificatePassword == null) CertificatePassword = "";
            if (CertificateHosts == null) CertificateHosts = "";

            if (TlsPort < 1024) TlsPort = 8443;
            if (TlsPort > 65535) TlsPort = 8443;
            // TLS 端口不能和内部 HTTP 端口撞车，否则转发会打死自己
            if (InternalPort != 0 && InternalPort == TlsPort) InternalPort = 0;

            if (InternalPort < 0) InternalPort = 0;
            if (InternalPort > 65535) InternalPort = 0;
            // 内部端口必须落在合法区间；0 表示自动分配
            if (InternalPort != 0 && (InternalPort < 1024 || InternalPort > 65535)) InternalPort = 0;

            if (MaxTlsConnections < 1) MaxTlsConnections = 1;
            if (MaxTlsConnections > 512) MaxTlsConnections = 512;
            if (TlsHandshakeTimeoutSeconds < 3) TlsHandshakeTimeoutSeconds = 3;
            if (TlsHandshakeTimeoutSeconds > 120) TlsHandshakeTimeoutSeconds = 120;

            // ⚠ 开了 TLS 就必须给 Cookie 打 Secure 标记，否则浏览器可能在明文回退时照样发。
            // 这里只在"用户没显式关掉"的情况下自动打开，不覆盖明确的 false。
            if (EnableTls && !HasExplicitSecureCookie) SecureCookie = true;
        }

        /// <summary>
        /// 配置里是否<b>显式</b>写过 SecureCookie。
        ///
        /// 用于区分「用户主动关掉」与「这是老配置、根本没这个字段」——
        /// 后者在启用 TLS 时应该被自动纠正成 true，前者要尊重。
        /// 通过读原始 JSON 里是否存在该键来判断。
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasExplicitSecureCookie { get; set; } = false;

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
                    string raw = File.ReadAllText(path);
                    config = JsonConvert.DeserializeObject<WebPanelConfig>(raw);

                    // 探测 SecureCookie 是否被显式写过（Clamp 里要靠它区分"用户主动关掉"与"老配置没这字段"）。
                    // 直接在 JSON 文本里查键，比反序列化出一个 JObject 更省依赖也更快。
                    if (config != null && !HasTopLevelKey(raw, "SecureCookie"))
                    {
                        config.HasExplicitSecureCookie = false;
                    }
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

        /// <summary>
        /// 判断原始 JSON 里有没有某个<b>顶层</b>键（只看第一个字符之后、位于顶层的出现）。
        /// 目的只是"这个字段用户写没写过"，不需要完整解析，也不该因为格式怪异就抛异常。
        /// </summary>
        private static bool HasTopLevelKey(string raw, string key)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            string needle = "\"" + key + "\"";
            int index = raw.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) return false;

            // 从命中位置往前回溯：跳过值里的同名串（那会出现在 ":" 之后），
            // 只有出现在对象结构位置（前面是 { 或 ,）才算顶层键。
            for (int i = index - 1; i >= 0; i--)
            {
                char c = raw[i];
                if (c == '{' || c == ',') return true;
                if (c == ':' || c == '"') return false;
                if (!char.IsWhiteSpace(c)) return false;
            }
            return false;
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
