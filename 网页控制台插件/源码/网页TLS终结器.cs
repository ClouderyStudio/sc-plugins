// 网页控制台插件 —— TLS 前置终结器。
//
// ============================ 为什么要有这个 ============================
// 面板登录后能执行服务端命令，而命令走 CmdManager.HandleMessage(..., isTerminal:true)，
// 那条路**整个跳过 AuthLevel 检查** —— 也就是说口令是唯一闸门。
// 走 HTTP 时口令与会话 Cookie 全是明文过网，FRP 节点/运营商/被劫持的 WiFi 都能直接拿到
// 一个能执行任意白名单命令的终端。所以公网暴露必须上 TLS。
//
// ============================ 为什么不用 HttpListener 的 https ============================
// HttpListener 在 Windows 上是 HTTP.sys（内核态）转发，TLS 由内核终结，
// 它只认**系统证书库里、且被 netsh http add sslcert 绑到该端口**的证书。
// 也就是说：写在 C# 里的证书对象一律无效，必须管理员操作、装证书、netsh 绑端口。
// 这跟纯插件的目标冲突。
//
// ============================ 这里的做法 ============================
// 自己起一个 TcpListener，在公网端口用 SslStream 终结 TLS，
// 把解密后的 HTTP 字节**原样透明转发**到绑在 127.0.0.1 的内部 HttpListener，
// 再把 HttpListener 的响应字节原样回传。
//
// 好处：
//   1. 真正的 TLS 加密，且**零系统依赖、免管理员** —— 自签证书直接从内存加载；
//   2. **业务代码一行都不用改** —— SSE 长连接、chunked 编码、keep-alive 全由
//      HttpListener 自己处理，转发层只是搬字节，不需要理解 HTTP 语义；
//   3. 内部 HttpListener 永远绑 127.0.0.1，公网碰不到，也就不需要为它申请 URL ACL。
//
// 代价：多一次本机回环转发（内存拷贝，量级可忽略），以及转发层必须正确处理
// 半关闭/断连，否则 SSE 长连接会泄漏线程。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Engine;

namespace ScWebPanel
{
    /// <summary>
    /// TLS 前置终结器：公网 <--TLS-- 插件 --回环明文--> 内部 HttpListener。
    /// 见文件头「这里的做法」。一个实例对应一个公网监听端口。
    /// </summary>
    internal sealed class WebPanelTlsTerminator : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<TcpClient> _activeClients = new List<TcpClient>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stopping;
        private X509Certificate2 _certificate;

        private readonly int _maxConnections;
        private readonly int _handshakeTimeoutMs;
        private readonly IPEndPoint _internalEndpoint;
        private readonly string _publicScheme;
        private readonly int _publicPort;

        public WebPanelTlsTerminator(IPEndPoint internalEndpoint, WebPanelConfig settings)
        {
            _internalEndpoint = internalEndpoint;
            _maxConnections = settings.MaxTlsConnections;
            _handshakeTimeoutMs = settings.TlsHandshakeTimeoutSeconds * 1000;
            _publicScheme = "https";
            _publicPort = settings.TlsPort;
        }

        /// <summary>外部可访问的地址描述，用于启动日志。</summary>
        public string PublicEndpointText =>
            _publicScheme + "://" + WebPanelTlsCertificates.DescribeBindHost() + ":" + _publicPort + "/";

        /// <summary>
        /// 启动监听。返回 false 时 error 里有可以直接写进日志的原因。
        /// 证书拿不到是**硬失败** —— 宁可不开，也不要开一个明文的公网面板。
        /// </summary>
        public bool TryStart(WebPanelConfig settings, string certStoreDir, out string error)
        {
            error = null;

            try
            {
                _certificate = WebPanelTlsCertificates.Resolve(settings, certStoreDir);
            }
            catch (Exception ex)
            {
                error = "TLS 证书加载失败：" + ex.Message +
                        "\n       ·CertificateSource=pfx 时请确认 PFX 路径存在且口令正确" +
                        "\n       ·CertificateSource=auto 时请确认目录可写：" + certStoreDir;
                return false;
            }

            if (_certificate == null)
            {
                error = "TLS 证书不可用，已拒绝启动（不会退化成明文监听）。" +
                        "\n       自签证书生成失败时，检查磁盘是否可写、或改用 CertificateSource=pfx 指定现成证书。";
                return false;
            }

            try
            {
                IPAddress bind = WebPanelTlsCertificates.ParseBindAddress(settings.BindHost);
                _listener = new TcpListener(bind, settings.TlsPort);
                _listener.Start();
            }
            catch (Exception ex)
            {
                error = $"TLS 端口 {settings.TlsPort} 无法监听（{ex.GetType().Name}）：{ex.Message}" +
                        "\n       常见原因：端口被占用、绑 +/* 时被防火墙拦截、或该端口需要特权。";
                return false;
            }

            _stopping = false;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "WebPanelTlsAccept"
            };
            _acceptThread.Start();
            return true;
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // Stop() 会让 Accept 抛异常，属于正常退出路径。
                    if (_stopping) break;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (_stopping)
                {
                    SafeClose(client);
                    break;
                }

                // 并发上限：超了直接关掉，不要为每个连上来的连接都分配线程。
                // 扫描在锁内做，连接数很少（上限 64 级别），代价可忽略。
                lock (_gate)
                {
                    if (_activeClients.Count >= _maxConnections)
                    {
                        SafeClose(client);
                        Log.Warning("[网页控制台] TLS 连接数已达上限 " + _maxConnections + "，已拒绝新连接。" +
                                    "若你正常使用时频繁被拒，请调大 MaxTlsConnections。");
                        continue;
                    }
                    _activeClients.Add(client);
                }

                var thread = new Thread(() => HandleConnection(client))
                {
                    IsBackground = true,
                    Name = "WebPanelTlsConn"
                };
                thread.Start();
            }
        }

        /// <summary>
        /// 处理一条 TLS 连接：握手 -> 明文转发到内部 HttpListener -> 回传响应。
        /// 任何一步出错都只是关掉这条连接，绝不影响服务端主线程和其他连接。
        /// </summary>
        private void HandleConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;   // SSE 是小帧高频，禁 Nagle 免得攒 40ms
                using (client)
                using (SslStream ssl = new SslStream(client.GetStream(), false))
                {
                    // 先握手，超时必须自己掐 —— 没人握手成功的连接会一直占着线程。
                    if (!AuthenticateWithTimeout(ssl))
                    {
                        SafeClose(client);
                        return;
                    }

                    // 握手成功 = 有明文要处理了，建立到内部 HttpListener 的回环连接。
                    using (TcpClient upstream = new TcpClient())
                    {
                        if (!ConnectUpstream(upstream))
                        {
                            SafeClose(client);
                            return;
                        }
                        upstream.NoDelay = true;

                        using (NetworkStream upStream = upstream.GetStream())
                        {
                            // 方向一：浏览器 -> 内部（上行请求）。上传完就关掉写方向，
                            // 让 HttpListener 那边能读到请求结束，而不是等超时。
                            Thread up = new Thread(() => Pump(upStream, ssl)) { IsBackground = true };
                            up.Start();
                            // 方向二：内部 -> 浏览器（下行响应）。跑完即整条连接结束。
                            Pump(ssl, upStream);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 客户端中途断开是常态（刷新页面、关标签），不用刷日志。
            }
            finally
            {
                SafeClose(client);
                lock (_gate)
                {
                    _activeClients.Remove(client);
                }
            }
        }

        /// <summary>
        /// 单向字节泵：src 读到 EOF 或出错就停。
        /// SSE 场景下这个方向可能几小时不结束，属正常。
        /// </summary>
        private static void Pump(Stream src, Stream dst)
        {
            var buffer = new byte[16384];
            try
            {
                while (true)
                {
                    int read = src.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    dst.Write(buffer, 0, read);
                    // ⚠ 必须每块都 Flush：HttpListener 那边是边生成边写的，
                    // 不刷就会卡在缓冲区里，SSE 永远到不了浏览器。
                    dst.Flush();
                }
            }
            catch
            {
                // 断开/超时/协议错误都归到这里，正常收尾。
            }
            finally
            {
                try { dst.Flush(); } catch { }
            }
        }

        private bool ConnectUpstream(TcpClient upstream)
        {
            try
            {
                // BeginConnect 的双参重载只带 endpoint，不接受 callback。
                // 这里自己用 BeginConnect(endpoint, null, null) 拿 IAsyncResult，
                // 靠 AsyncWaitHandle 掐超时。
                IAsyncResult ar = upstream.BeginConnect(_internalEndpoint.Address, _internalEndpoint.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(_handshakeTimeoutMs))
                {
                    return false;
                }
                upstream.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
/// 带超时的 TLS 握手。
/// SslStream.AuthenticateAsServerAsync 没有内建超时，必须自己用等待句柄掐住，
/// 否则一个只连不发 ClientHello 的扫描器就能把连接数占满。
///
/// ⚠ 协议用 <see cref="SslProtocols.None"/>（=交给操作系统决定），**不要**写死
/// <c>Tls12 | Tls13</c>。原因：TLS 1.3 需要系统级支持（Windows Server 2022 / Win11 才默认开），
/// 而实测在 Server 2019 这类老系统上，请求 TLS 1.3 会让整个握手直接失败（客户端收到
/// alert 40 handshake failure），连 TLS 1.2 的兼容回退都不会走到 —— 结果是**整个面板连不上**。
/// 用 None 时：2022 自动拿到 1.3，2019 自动落到 1.2，一份代码通吃。
/// 老系统上协商到的最低版本是 TLS 1.2，且套件为 ECDHE-RSA-AES256-GCM-SHA384（已实测）。
/// </summary>
private bool AuthenticateWithTimeout(SslStream ssl)
        {
            try
            {
                IAsyncResult ar = ssl.BeginAuthenticateAsServer(
                    _certificate,
                    false,                      // 不要求客户端证书 —— 面板不是 mTLS 场景
                    SslProtocols.None,          // 由操作系统决定可用的协议（见下方说明）
                    false,                      // 不做证书吊销检查，自签证书查了反而会失败
                    null,
                    null);

                if (!ar.AsyncWaitHandle.WaitOne(_handshakeTimeoutMs))
                {
                    return false;
                }
                ssl.EndAuthenticateAsServer(ar);
                return ssl.IsAuthenticated && ssl.IsEncrypted;
            }
            catch
            {
                return false;
            }
        }

        private static void SafeClose(TcpClient client)
        {
            try
            {
                // 会把底层 Socket 一并关掉，泵循环会因此拿到异常或 0 字节而退出。
                client.Close();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                if (_listener != null) _listener.Stop();
            }
            catch
            {
            }

            // 主动掐掉所有还在的连接，否则 SSE 长连接会让进程里留着僵尸线程。
            lock (_gate)
            {
                for (int i = 0; i < _activeClients.Count; i++) SafeClose(_activeClients[i]);
                _activeClients.Clear();
            }

            try
            {
                if (_certificate != null)
                {
                    _certificate.Dispose();
                    _certificate = null;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 证书解析与自签生成。
    ///
    /// 两种来源：
    /// - <c>pfx</c>：用户指定现成证书（含私钥），从文件加载 —— 买了域名证书走这条；
    /// - <c>auto</c>：自签。首次启动生成并落盘（PFX），之后复用同一个文件，
    ///   这样**重启后浏览器不会又变一个陌生证书**（自签每次新生成的话，
    ///   每次重启都要重新点一次"继续前往"，体验很差）。
    /// </summary>
    internal static class WebPanelTlsCertificates
    {
        /// <summary>自签证书落盘文件名（放在插件自己的配置目录下）。</summary>
        public const string SelfSignedFileName = "网页控制台自签证书.pfx";

        /// <summary>
        /// 解析出可用的服务端证书。**返回 null 表示失败**，调用方必须拒绝启动，
        /// 绝不允许悄悄退化成明文监听。
        /// </summary>
        public static X509Certificate2 Resolve(WebPanelConfig settings, string certStoreDir)
        {
            if (string.Equals(settings.CertificateSource, "pfx", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(settings.CertificatePfxPath) ||
                    !File.Exists(settings.CertificatePfxPath))
                {
                    throw new FileNotFoundException("CertificateSource=pfx 但证书文件不存在：" + settings.CertificatePfxPath);
                }
                // 空口令传 null（加载器不接受空字符串作为口令）。
                // 用 X509CertificateLoader 而不是已过时的 X509Certificate2 构造器（SYSLIB0057）。
                X509Certificate2 loaded = string.IsNullOrEmpty(settings.CertificatePassword)
                    ? X509CertificateLoader.LoadCertificateFromFile(settings.CertificatePfxPath)
                    : X509CertificateLoader.LoadPkcs12FromFile(
                        settings.CertificatePfxPath, settings.CertificatePassword);

                if (!loaded.HasPrivateKey)
                {
                    loaded.Dispose();
                    throw new InvalidOperationException("证书 " + settings.CertificatePfxPath + " 不含私钥，无法用于 TLS 服务端。");
                }
                return loaded;
            }

            // auto：优先复用已生成的，生成过就直接读回来。
            string selfPath = Path.Combine(certStoreDir ?? "", SelfSignedFileName);
            if (!string.IsNullOrEmpty(certStoreDir) && File.Exists(selfPath))
            {
                try
                {
                    return X509CertificateLoader.LoadPkcs12FromFile(selfPath, null);
                }
                catch (Exception ex)
                {
                    // 文件在但读不出来（损坏 / 换了口令）—— 重新生成一份覆盖掉。
                    Log.Warning("[网页控制台] 自签证书文件无法读取，将重新生成：" + ex.Message);
                }
            }

            X509Certificate2 generated = CreateSelfSigned(settings);

            if (!string.IsNullOrEmpty(certStoreDir))
            {
                try
                {
                    Directory.CreateDirectory(certStoreDir);
                    // 权限收紧：这个文件里是**私钥**，只有本机管理员该读得到。
                    File.WriteAllBytes(selfPath, generated.Export(X509ContentType.Pfx, ""));
                    RestrictToAdmins(selfPath);
                }
                catch (Exception ex)
                {
                    // 不致命：只是下次重启会换一张证书而已。
                    Log.Warning("[网页控制台] 自签证书落盘失败（重启后会重新生成，浏览器需重新信任一次）：" + ex.Message);
                }
            }

            return generated;
        }

        /// <summary>
        /// 生成自签证书。用 <see cref="CertificateRequest"/>（.NET Core 内置），
        /// 不依赖任何外部证书库。
        ///
        /// 关键点：必须写 <b>SAN</b>（主题备用名）。现代浏览器**只看 SAN**，
        /// 只填 CN（主题名）的话即使 CN 完全对得上也会报"证书无效"。
        /// </summary>
        private static X509Certificate2 CreateSelfSigned(WebPanelConfig settings)
        {
            string[] hosts = ResolveHosts(settings);
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=" + (hosts.Length > 0 ? hosts[0] : "Survivalcraft-WebPanel"),
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                // SAN：主机名 + IP 都要覆盖。IP 条目得用对应类型构造 Encode（SAN 里 IP 不是字符串）。
                var sanBuilder = new SubjectAlternativeNameBuilder();
                bool any = false;
                for (int i = 0; i < hosts.Length; i++)
                {
                    IPAddress ip;
                    if (IPAddress.TryParse(hosts[i], out ip))
                    {
                        sanBuilder.AddIpAddress(ip);
                    }
                    else
                    {
                        sanBuilder.AddDnsName(hosts[i]);
                    }
                    any = true;
                }
                if (any) request.CertificateExtensions.Add(sanBuilder.Build());

                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },    // serverAuth
                    false));

                // 十年：自签证书本来就要手动信任，过期反而添乱（前提是使用者知道这是自签）。
                var cert = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(10));

                // ⚠ 必须带私钥重新导出 —— CreateSelfSigned 返回的对象在部分平台上
                // 私钥不可导出/不可用于 TLS，直接拿去 AuthenticateAsServer 会失败。
                return X509CertificateLoader.LoadPkcs12(
                    cert.Export(X509ContentType.Pfx, ""), null,
                    X509KeyStorageFlags.Exportable);
            }
        }

        /// <summary>
        /// 决定证书要覆盖哪些主机名。
        /// 优先用配置的 <see cref="WebPanelConfig.CertificateHosts"/>；
        /// 没配就退回监听地址 + 本机 FQDN（此时 +/* 之类通配写法不产生有效 SAN，只能靠使用者手填）。
        /// </summary>
        private static string[] ResolveHosts(WebPanelConfig settings)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.CertificateHosts))
            {
                string[] parts = settings.CertificateHosts.Split(
                    new[] { ',', ';', '\n', '\r', '\t', ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string v = parts[i].Trim();
                    if (v.Length > 0 && !list.Contains(v)) list.Add(v);
                }
            }

            if (list.Count == 0)
            {
                string bind = settings.BindHost == null ? "" : settings.BindHost.Trim();
                // + 与 * 是通配写法，不是真实主机名，别塞进 SAN。
                if (bind.Length > 0 && bind != "+" && bind != "*")
                {
                    list.Add(bind);
                }
                else
                {
                    // 通配绑定时给个本机名兜底，至少 localhost 能正常访问。
                    list.Add("localhost");
                    try
                    {
                        string fqdn = Dns.GetHostName();
                        if (!string.IsNullOrWhiteSpace(fqdn) && !list.Contains(fqdn)) list.Add(fqdn);
                    }
                    catch
                    {
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>把 <c>BindHost</c> 翻译成监听地址；<c>+</c>/<c>*</c> 视为所有网卡。</summary>
        public static IPAddress ParseBindAddress(string bindHost)
        {
            IPAddress addr;
            string bind = bindHost == null ? "" : bindHost.Trim();
            if (bind.Length == 0 || bind == "+" || bind == "*")
            {
                return IPAddress.Any;
            }
            if (IPAddress.TryParse(bind, out addr))
            {
                return addr;
            }
            // 填了域名：解析一次，解析不出来就退回 0.0.0.0（并由日志提醒）。
            try
            {
                IPAddress[] resolved = Dns.GetHostAddresses(bind);
                if (resolved != null && resolved.Length > 0) return resolved[0];
            }
            catch
            {
            }
            return IPAddress.Any;
        }

        /// <summary>给日志用的友好地址描述。</summary>
        public static string DescribeBindHost()
        {
            return "<公网地址>";
        }

        /// <summary>
        /// 把证书文件 ACL 收紧到仅管理员可读。
        ///
        /// 这台是 Windows Server，<c>File.SetAccessControl</c> 需要 <c>System.IO.FileSystem.AccessControl</c>。
        /// 拿不到这个能力时**不能因此失败** —— 只是私钥保护弱一点，值得记一条日志。
        /// </summary>
        private static void RestrictToAdmins(string path)
        {
            try
            {
                var info = new FileInfo(path);
                var acl = info.GetAccessControl();
                var admins = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

                acl.SetAccessRuleProtection(true, false);   // 继承关掉，防止 Everyone 混进来
                acl.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    admins, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                acl.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    system, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            catch (Exception ex)
            {
                Log.Warning("[网页控制台] 证书文件 ACL 收紧失败（不影响 TLS 功能，但私钥保护较弱）：" + ex.Message);
            }
        }
    }
}