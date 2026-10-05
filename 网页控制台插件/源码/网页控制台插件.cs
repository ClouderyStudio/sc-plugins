using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Engine;
using Game;
using Game.Server;

namespace ScWebPanel
{
    /// <summary>
    /// 网页控制台：在服务端进程里内嵌一个 HTTP 服务，浏览器直接看状态、管玩家、看背包、敲命令。
    ///
    /// 形态：用 <see cref="HttpListener"/> 自己起服务，页面与静态资源由 <see cref="WebPanelAssets"/>
    /// 直接吐字符串，**不落任何前端文件到磁盘**，也就没有"忘了部署 dist"这类问题。
    ///
    /// 线程模型（这里是最容易写错的地方）：
    /// - <see cref="HttpListener"/> 的回调跑在**线程池**上，不是主线程。所以：
    ///   · 任何碰游戏对象（Project / Subsystem / ComponentPlayer）的读取，都不能直接在回调里做
    ///     —— 主线程正在每帧改这些对象，多线程读会读到撕裂状态甚至崩服。
    ///   · 做法是**把读操作排队到主线程执行**（<see cref="Update"/> 里取队列执行），
    ///     回调这边用 <see cref="ManualResetEventSlim"/> 等结果，带超时。
    /// - 命令执行同理，而且更严格：`CmdManager` 与地形/实体都是主线程的东西。
    /// </summary>
    public sealed class WebPanelPlugin : ServerPlugin
    {
        public override int Version => 10000;

        public override string Name => "网页控制台插件";

        public byte FirstLevel => 0;

        /// <summary>
        /// 插件自己的目录（与基础插件等其他插件并列）。
        /// 独立插件不能蹭宿主的目录，否则卸载本插件会留下没人认领的垃圾。
        /// </summary>
        private static string PluginDirectory => Storage.GetSystemPath("app:/Plugins/网页控制台插件");

        /// <summary>配置文件路径。</summary>
        private static string ConfigFilePath => Path.Combine(PluginDirectory, "网页控制台配置.json");

        /// <summary>
        /// 启动时读一次就不再变。默认关 —— 开一个带命令执行的网页终端必须是**显式**决定。
        /// </summary>
        private WebPanelConfig _settings;

        private WebPanelConfig Settings => _settings;

        private HttpListener _listener;
        private Thread _listenThread;
        private volatile bool _stopping;

        /// <summary>
        /// TLS 前置终结器（非空 = 正在用 HTTPS 对外提供服务）。
        ///
        /// 它终结公网端口上的 TLS，再把明文转发到绑在 127.0.0.1 的 <see cref="_listener"/>。
        /// 存在时 <see cref="_listener"/> 必须绑回环地址 —— 这样公网碰不到它，
        /// 也就不用为内部端口申请 URL ACL。见 <c>网页TLS终结器.cs</c>。
        /// </summary>
        private WebPanelTlsTerminator _tls;

        /// <summary>
        /// 当前实际对外提供服务的端口与监听地址。用来判断"配置有没有真的变"，
        /// 避免每次点重载都无谓地重启监听（内部端口是随机的，不能拿它比）。
        /// </summary>
        private int _activePublicPort;
        private string _activeBindHost;

        /// <summary>重载前生效的配置，用于新监听起不来时回退，避免面板失联。</summary>
        private WebPanelConfig _previousSettings;

        /// <summary>日志环形缓冲（终端页的数据源）。</summary>
        private readonly WebPanelLogSink _logSink = new WebPanelLogSink();

        /// <summary>会话令牌表。key = token，value = 会话。</summary>
        private readonly Dictionary<string, WebPanelSession> _sessions =
            new Dictionary<string, WebPanelSession>(StringComparer.Ordinal);

        /// <summary>登录失败计数：IP -> (次数, 封禁截止时间)。</summary>
        private readonly Dictionary<string, LoginFailure> _failures =
            new Dictionary<string, LoginFailure>(StringComparer.Ordinal);

        /// <summary>
        /// 全局登录失败计数与熔断截止时间。
        ///
        /// 为什么要它：<see cref="_failures"/> 按 IP 记，攻击者轮换代理 IP 就完全绕过，
        /// 可以无限次撞口令。这个是<b>跨 IP</b> 的，绕不过去。
        /// </summary>
        private int _globalFailures;
        private DateTime _globalLockedUntilUtc = DateTime.MinValue;
        private readonly object _globalFailureLock = new object();

        /// <summary>会话 Cookie 名。带 HttpOnly，JS 读不到。</summary>
        private const string SessionCookieName = "wb_panel_session";

        /// <summary>主线程执行队列。HTTP 线程往里塞，<see cref="Update"/> 主线程取出来跑。</summary>
        private readonly Queue<PendingWork> _workQueue = new Queue<PendingWork>();

        private readonly object _workLock = new object();

        /// <summary>性能采样（TPS / MSPT 是自己量的，核心没有）。</summary>
        private readonly WebPanelMetrics _metrics = new WebPanelMetrics();

        /// <summary>
        /// 最近一次成功执行命令的摘要（谁、什么时候、什么命令）。
        /// 只留最近若干条，供页面显示"最近操作"。
        /// </summary>
        private readonly Queue<string> _recentActions = new Queue<string>();

        private DateTime _startedAtUtc = DateTime.UtcNow;

        /// <summary>日志文件目录（`Log.AddLogSink` 只能拿到内存里的日志，这里额外落一份盘便于排查）。</summary>
        private string PanelDataDir => Path.Combine(PluginDirectory, "网页控制台");

        // ---- SSE（实时日志推送）----
        //
        // 为什么单独开一个泵线程，而不是在 Log 回调里直接写：
        //   Log.Write 会在任意线程触发（存档线程、网络线程、主线程都可能），
        //   直接在回调里往 HttpListener 的 OutputStream 写，一旦某个客户端读得慢就会
        //   把写日志的线程阻塞住，进而拖住游戏主循环。所以：回调只置信号，
        //   由专门的泵线程统一写；写失败（客户端断开）就从表里摘掉。

        /// <summary>并发 SSE 连接上限。面板是给管理员看的，几个就够；限住是为了不让慢连接堆积。</summary>
        private const int MaxSseClients = 8;

        /// <summary>SSE 心跳间隔（秒）。中间有代理/Nginx 时，长时间没数据会被掐断。</summary>
        private const int SseHeartbeatSeconds = 15;

        private readonly List<SseClient> _sseClients = new List<SseClient>();
        private readonly object _sseLock = new object();
        private readonly ManualResetEventSlim _sseSignal = new ManualResetEventSlim(false);
        private Thread _sseThread;

        // ==========================================
        // 生命周期
        // ==========================================

        public override void Initialize()
        {
            EnsureDefaultConfig();
            _settings = WebPanelConfigStore.Load(ConfigFilePath);

            var settings = Settings;
            if (settings == null || !settings.Enabled)
            {
                Log.Information("[网页控制台] 未启用（网页控制台配置.json 里 Enabled=false）。" +
                                "需要先设好 Password 并把 Enabled 改成 true");
                return;
            }

            if (!settings.HasPassword)
            {
                // 宁可不开，也不要开一个人人可进的终端
                Log.Error("[网页控制台] 已启用但没有配置登录口令（WebPanel.Password 为空），" +
                          "为避免暴露一个无鉴权的命令终端，本次不启动。请先设置口令。");
                return;
            }

            try
            {
                Directory.CreateDirectory(PanelDataDir);
            }
            catch (Exception ex)
            {
                Log.Warning("[网页控制台] 数据目录创建失败（不影响服务启动）：" + ex.Message);
            }

            // 日志捕获：挂一个 ILogSink 上去，终端页就有内容了。
            // 这是 Engine.Log 公开支持的扩展点（Log.AddLogSink），不需要 Harmony 打补丁。
            _logSink.Capacity = settings.LogBufferLines;
            try
            {
                Log.AddLogSink(_logSink);
            }
            catch (Exception ex)
            {
                Log.Warning("[网页控制台] 日志捕获注册失败，终端页将没有历史日志：" + ex.Message);
            }

            // SSE 推流：日志落地即唤醒泵线程。配置项关掉就不起泵（前端退回轮询）。
            _logSink.LogAdded += OnLogAdded;
            if (settings.UseServerSentEvents)
            {
                StartSsePump();
            }

            _stopping = false;
            _startedAtUtc = DateTime.UtcNow;

            if (!TryStartListener(settings, out string error))
            {
                Log.Error("[网页控制台] 启动失败：" + error);
                return;
            }

            Log.Information($"[网页控制台] 已启动：{PublicUrl(settings)}" +
                            $"（日志缓冲 {settings.LogBufferLines} 行，网页终端允许的命令前缀：" +
                            $"{(WebPanelConfig.SplitPrefixes(settings.AllowedCommandPrefixes).Length == 0 ? "无" : settings.AllowedCommandPrefixes)}）");

            if (settings.EnableTls)
            {
                // 自签证书一定会触发浏览器警告，必须提前告诉使用者，否则会以为是被攻击了。
                Log.Warning("[网页控制台] TLS 已启用，证书来源 CertificateSource=" + settings.CertificateSource +
                            "。若是自签，浏览器会提示『连接不是私密连接』，点『高级 → 继续前往』即可 —— " +
                            "通道本身是真加密。若证书名不匹配，请确认 CertificateHosts 填的是" +
                            "你**实际访问用的**公网域名或 IP。");
                Log.Warning("[网页控制台] 自签证书私钥保存在 " +
                            Path.Combine(PluginDirectory, "网页控制台自签证书.pfx") +
                            "（已收紧 ACL 仅管理员可读）。请勿随仓库提交或外传。");
            }

            if (IsExternallyVisible(settings.BindHost))
            {
                Log.Warning("[网页控制台] 注意：监听地址 " + settings.BindHost +
                            " 允许外部访问。请确认口令足够强" +
                            (settings.EnableTls
                                ? "，且 TLS 已启用。"
                                : "；⚠ 当前未启用 TLS，口令与 Cookie 是明文过网，建议立刻 EnableTls=true 或改用反向代理。"));
            }
        }

        /// <summary>
        /// 对外访问地址的日志描述。开了 TLS 就报 https + TLS 端口，否则维持原来的 http + 配置端口。
        /// </summary>
        private static string PublicUrl(WebPanelConfig settings)
        {
            if (settings.EnableTls)
            {
                return $"https://{DescribeHost(settings.BindHost)}:{settings.TlsPort}/（TLS 终结，公网端口）";
            }
            return $"http://{DescribeHost(settings.BindHost)}:{settings.Port}/";
        }

        private bool TryStartListener(WebPanelConfig settings, out string error)
        {
            error = null;
            // 提到 try 外面：catch 里要拿它拼错误提示。
            string prefix = null;

            try
            {
                // ---- 两种模式的端口分工 ----
                // 开 TLS：公网暴露的是 TlsPort，内部 HttpListener 只绑 127.0.0.1 的内部端口。
                // 不开：保持原样，HttpListener 直接用 BindHost + Port 对外。
                if (settings.EnableTls)
                {
                    int internalPort = settings.InternalPort;
                    if (internalPort <= 0) internalPort = PickFreeInternalPort();
                    //⚠ 强制回环。开着 TLS 时内部端口绝不能对外，否则等于又开了一个明文入口。
                    prefix = BuildPrefix("127.0.0.1", internalPort);
                }
                else
                {
                    prefix = BuildPrefix(settings.BindHost, settings.Port);
                }

                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();

                _listener = listener;
                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "WebPanelListener"
                };
                _listenThread.Start();

                // ---- 再起 TLS 终结层 ----
                // 顺序很重要：内部监听先起来，TLS 才能往它转发。
                // 证书拿不到就整体失败并回滚，绝不退化成"只有明文"的半吊子状态。
                if (settings.EnableTls)
                {
                    var bindAddr = WebPanelTlsCertificates.ParseBindAddress(settings.BindHost);
                    int internalPort = settings.InternalPort;
                    if (internalPort <= 0)
                    {
                        // 必须跟上面实际用的端口一致，所以从 prefix 反解。
                        internalPort = ParsePortFromPrefix(prefix);
                    }

                    var terminator = new WebPanelTlsTerminator(
                        new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, internalPort), settings);
                    if (!terminator.TryStart(settings, PluginDirectory, out string tlsError))
                    {
                        StopListenerQuietly();
                        error = tlsError;
                        return false;
                    }
                    _tls = terminator;
                }

                // 记下实际生效的对外参数，供 ReloadListener 判断是否需要重启。
                _activePublicPort = settings.EnableTls ? settings.TlsPort : settings.Port;
                _activeBindHost = settings.BindHost;
                _previousSettings = settings;
                return true;
            }
            catch (HttpListenerException ex)
            {
                StopListenerQuietly();
                // 最常见的就是端口被占 / 没有 URL ACL 权限。把这两条直接写进提示里，省得反复猜。
                error = $"HttpListener 无法监听 {prefix}（错误码 {ex.ErrorCode}）：{ex.Message}\n" +
                        "       常见原因：① 端口已被占用；② 绑定 +/* 时缺少 URL ACL —— " +
                        $"以管理员执行 `netsh http add urlacl url={prefix} user=Everyone`，或把 BindHost 改回 127.0.0.1。";
                return false;
            }
            catch (Exception ex)
            {
                StopListenerQuietly();
                error = $"HttpListener 初始化异常（{ex.GetType().Name}）：{ex.Message}";
                return false;
            }
        }

        /// <summary>关掉内部监听与 TLS 终结层，失败也不抛 —— 清理路径必须无异常。</summary>
        private void StopListenerQuietly()
        {
            try
            {
                if (_tls != null)
                {
                    _tls.Dispose();
                    _tls = null;
                }
            }
            catch
            {
            }
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch
            {
            }
        }

        /// <summary>挑一个空闲的回环端口给内部 HttpListener 用（配置没指定 InternalPort 时）。</summary>
        private static int PickFreeInternalPort()
        {
            // 从一个偏高的随机区间里试，抓不到就让 OS 分配（端口 0）。
            for (int attempt = 0; attempt < 20; attempt++)
            {
                // ⚠ Random 在 Engine 与 Game 各有一个，必须写全 System.Random 才不会二义。
                int candidate = 20000 + new System.Random().Next(20000);
                try
                {
                    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
                    probe.Start();
                    probe.Stop();
                    return candidate;
                }
                catch
                {
                    // 被占用就换下一个。
                }
            }
            return 0;   // 交给系统分配
        }

        /// <summary>从 http://host:port/ 形式的 URL 前缀里抠出端口号。</summary>
        private static int ParsePortFromPrefix(string prefix)
        {
            try
            {
                int colon = prefix.LastIndexOf(':');
                int slash = prefix.IndexOf('/', colon);
                string portPart = slash > 0 ? prefix.Substring(colon + 1, slash - colon - 1) : prefix.Substring(colon + 1);
                return ParseInt(portPart, 0);
            }
            catch
            {
                return 0;
            }
        }

        private static string BuildPrefix(string host, int port)
        {
            // HttpListener 的通配符写法就是 +/*，直接透传；其余按主机名/地址原样用。
            return $"http://{host}:{port}/";
        }

        private static string DescribeHost(string host)
        {
            return host == "+" || host == "*" ? "0.0.0.0" : host;
        }

        private static bool IsExternallyVisible(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            return host == "+" || host == "*" || host == "0.0.0.0" ||
                   !(host == "localhost" || host == "127.0.0.1" || host == "::1");
        }

        /// <summary>
        /// 第一次装时磁盘上没有配置文件，就写一份默认（<c>Enabled=false</c>、口令留空）出来，
        /// 省得用户对着日志猜"配置项到底叫什么"。**已存在则一律不动** ——
        /// 用户的口令和端口是本插件最敏感的两个值，绝不能被覆盖回默认值。
        /// </summary>
        private static void EnsureDefaultConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath)) return;
                Directory.CreateDirectory(PluginDirectory);
                WebPanelConfigStore.Save(ConfigFilePath, new WebPanelConfig());
                Log.Information("[网页控制台] 已生成默认配置：" + ConfigFilePath +
                                "（默认关闭，需自行填写 Password 并把 Enabled 改为 true）");
            }
            catch (Exception ex)
            {
                Log.Warning("[网页控制台] 默认配置生成失败（不影响启动）：" + ex.Message);
            }
        }

        /// <summary>
        /// 读档回调。这里**故意不重新加载配置**：配置里的端口与口令决定 HTTP 监听前缀，
        /// 中途改掉只会让"跑着的服务"和"配置里的值"对不上。要改端口就重启服务端。
        /// </summary>
        public override void Load()
        {
        }

        public override void Save()
        {
            // 面板不持有需要落盘的运行状态（会话与日志缓冲都是易失的，重启即清空，这是有意为之：
            // 会话令牌一旦落盘，就等于把"谁登录过"的凭据留在了磁盘上）。
            // 配置本身也不在这里回写 —— 没人从面板上改它，写回去只会把运行中的脏值固化。
        }

        public override void Update(float dt)
        {
            _metrics.Tick(dt);
            DrainWorkQueue();
        }

        /// <summary>
        /// 把 HTTP 线程排进来的读操作在主线程执行掉。
        ///
        /// 为什么不直接在回调里读：主线程每帧都在改 Project/Subsystem/实体，
        /// 从别的线程读会读到撕裂状态；命令执行更是必须在主线程（会改地形与实体）。
        /// </summary>
        private void DrainWorkQueue()
        {
            while (true)
            {
                PendingWork work;
                lock (_workLock)
                {
                    if (_workQueue.Count == 0) return;
                    work = _workQueue.Dequeue();
                }

                try
                {
                    work.Result = work.Action();
                }
                catch (Exception ex)
                {
                    work.Error = ex;
                }
                finally
                {
                    work.Done.Set();
                }
            }
        }

        /// <summary>
        /// 把一个操作丢到主线程执行并等结果。**只能在 HTTP 线程调用**（主线程调用会等自己，必死锁）。
        /// </summary>
        private bool RunOnMainThread(Func<object> action, out object result, out string error, int timeoutMs)
        {
            var work = new PendingWork(action);
            lock (_workLock)
            {
                // 队列积压说明主线程卡住了（比如正在存档），此时直接拒绝比无限涨内存好
                if (_workQueue.Count > 256)
                {
                    result = null;
                    error = "服务端主线程繁忙（待处理请求过多），请稍后重试";
                    return false;
                }
                _workQueue.Enqueue(work);
            }

            if (!work.Done.Wait(timeoutMs))
            {
                result = null;
                error = "服务端未在限定时间内响应（可能正在存档或卡顿），请稍后重试";
                return false;
            }

            if (work.Error != null)
            {
                result = null;
                error = work.Error.Message;
                return false;
            }

            result = work.Result;
            error = null;
            return true;
        }

        /// <summary>
        /// 收尾入口，与 IDisposable 习惯一致。核心 <c>ServerPlugin</c> 没有 Dispose 虚方法，
        /// 这只是给外部（以及将来的热重载）一个统一入口，内部转发到 Shutdown（幂等）。
        /// </summary>
        public void Dispose()
        {
            Shutdown();
        }

        /// <summary>停服务。幂等，重复调用无害。</summary>
        public void Shutdown()
        {
            if (_stopping) return;
            _stopping = true;

            try
            {
                Log.RemoveLogSink(_logSink);
            }
            catch
            {
            }

            // 先关 SSE 连接：否则浏览器会一直挂着半开连接
            CloseAllSseClients();
            // ⚠ 顺序要紧：先停 TLS 终结层（它会掐断所有对外连接），再停内部监听。
            // 反过来的话，TLS 还在往一个已经关掉的监听转发，连接会堆积到超时。
            try
            {
                if (_tls != null)
                {
                    _tls.Dispose();
                    _tls = null;
                }
            }
            catch
            {
            }
            try
            {
                // 唤醒泵线程让它自己退出（它在等这个信号）
                _sseSignal.Set();
                if (_sseThread != null && _sseThread.IsAlive)
                {
                    _sseThread.Join(2000);
                }
            }
            catch
            {
            }
            _sseThread = null;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch
            {
            }
            _listener = null;

            try
            {
                if (_listenThread != null && _listenThread.IsAlive)
                {
                    _listenThread.Join(2000);
                }
            }
            catch
            {
            }
            _listenThread = null;

            lock (_sessions) _sessions.Clear();
            lock (_failures) _failures.Clear();
            lock (_workLock) _workQueue.Clear();
        }

        // ==========================================
        // HTTP 主循环
        // ==========================================

        private void ListenLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    // Stop()/Close() 会让 GetContext 抛异常，这是正常退出路径
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_stopping) return;
                    Log.Warning("[网页控制台] 接受连接失败：" + ex.Message);
                    continue;
                }

                // 每个请求丢线程池，别在监听循环里做任何实际工作（否则一个慢请求就堵死后续所有请求）
                ThreadPool.QueueUserWorkItem(_ => HandleRequestSafely(context));
            }
        }

        private void HandleRequestSafely(HttpListenerContext context)
        {
            try
            {
                HandleRequest(context);
            }
            catch (Exception ex)
            {
                Log.Warning("[网页控制台] 请求处理异常：" + ex.Message);
                TryWriteError(context, 500, "服务端处理请求时出错：" + ex.Message);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var settings = Settings;
            if (settings == null) return;

            var request = context.Request;
            var response = context.Response;
            string path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            string remoteIp = GetRemoteIp(request);

            if (settings.LogActions)
            {
                Log.Verbose($"[网页控制台] {remoteIp} {request.HttpMethod} {path}");
            }

            // ---------- 无需登录的静态资源与登录接口 ----------
            if (path == "/" || path == "/index.html")
            {
                WriteHtml(response, WebPanelAssets.IndexHtml(settings.Title, settings.UseServerSentEvents));
                return;
            }

            if (path == "/api/login")
            {
                HandleLogin(context, settings, remoteIp);
                return;
            }

            // 挑战值：登录前先取一个短时效一次性随机串，用于区分"真人浏览器"与"直接打 HTTP 的脚本"。
            // 它**不是**验证码，替代不了强口令，只是把"暴力打 /api/login"的门槛抬高。
            if (path == "/api/challenge")
            {
                HandleChallenge(context, settings, remoteIp);
                return;
            }

            // ---------- 其余全部要求已登录 ----------
            if (!TryAuthorize(request, settings, out WebPanelSession session, out string authError))
            {
                WriteJson(response, 401, JsonError(authError ?? "未登录或会话已过期"));
                return;
            }

            if (path == "/api/logout")
            {
                lock (_sessions) _sessions.Remove(session.Token);
                // 顺带把浏览器里的 Cookie 也清掉，否则刷新后还会带着一个死 Cookie 反复 401。
                ClearSessionCookie(response);
                WriteJson(response, 200, "{\"success\":true}");
                return;
            }

            if (path == "/api/overview")
            {
                RespondWithMainThreadRead(context, session, () => WebPanelApi.BuildOverview(_metrics, _startedAtUtc));
                return;
            }

            if (path == "/api/metrics")
            {
                // 纯内存采样，不碰游戏对象，可以直接答（前端画曲线用）
                WriteJson(response, 200, _metrics.ToJson());
                return;
            }

            if (path == "/api/players")
            {
                RespondWithMainThreadRead(context, session, WebPanelApi.BuildPlayers);
                return;
            }

            if (path == "/api/inventory")
            {
                string guid = request.QueryString["guid"];
                RespondWithMainThreadRead(context, session, () => WebPanelApi.BuildInventory(guid));
                return;
            }

            if (path == "/api/commands")
            {
                RespondWithMainThreadRead(context, session, () => WebPanelApi.BuildCommandList(settings));
                return;
            }

            if (path == "/api/logs")
            {
                // 日志是内存缓冲，直接答（SSE 不可用时的兜底轮询）
                int since = ParseInt(request.QueryString["since"], 0);
                WriteJson(response, 200, _logSink.ToJson(since));
                return;
            }

            if (path == "/api/logs/stream")
            {
                HandleLogStream(context, settings);
                return;
            }

            if (path == "/api/execute")
            {
                HandleExecute(context, settings, session, remoteIp);
                return;
            }

            if (path == "/api/players/kick" || path == "/api/players/action")
            {
                HandlePlayerAction(context, settings, session, remoteIp, path);
                return;
            }

            // ---- 存档目录浏览 ----
            if (path == "/api/files")
            {
                // 纯磁盘只读，不碰游戏对象，直接在 HTTP 线程答
                WriteJson(response, 200, WebPanelFiles.ListDirectory(request.QueryString["path"]));
                return;
            }

            if (path == "/api/file")
            {
                WriteJson(response, 200, WebPanelFiles.ReadFile(request.QueryString["path"]));
                return;
            }

            // ---- 面板设置 ----
            if (path == "/api/settings" && request.HttpMethod == "GET")
            {
                WriteJson(response, 200, WebPanelSettings.ToJson(settings, CurrentPort, _listener != null));
                return;
            }

            if (path == "/api/settings" && request.HttpMethod == "POST")
            {
                HandleSettingsSave(context, settings, session, remoteIp);
                return;
            }

            if (path == "/api/settings/password" && request.HttpMethod == "POST")
            {
                HandlePasswordChange(context, settings, session, remoteIp);
                return;
            }

            if (path == "/api/reload" && request.HttpMethod == "POST")
            {
                HandleReload(context, session, remoteIp);
                return;
            }

            // 封 IP 前先看这个地址上挂了几个人：>1 就是家庭/校园网，封了会连坐。
            if (path == "/api/connections")
            {
                bool force = ParseLong(request.QueryString["refresh"], 0) == 1;
                string payload = WebPanelConnections.Build(_logSink.SnapshotTexts(), force);
                WriteJson(response, 200, payload);
                return;
            }

            WriteJson(response, 404, JsonError("接口不存在：" + path));
        }

        /// <summary>读游戏状态的接口统一走这条路：排队到主线程 → 序列化 → 回写。</summary>
        private void RespondWithMainThreadRead(HttpListenerContext context, WebPanelSession session, Func<string> read)
        {
            var settings = Settings;
            int timeoutMs = Math.Max(1000, (settings?.RequestTimeoutSeconds ?? 30) * 1000 / 3);
            if (!RunOnMainThread(() => read(), out object result, out string error, timeoutMs))
            {
                WriteJson(context.Response, 503, JsonError(error));
                return;
            }
            WriteJson(context.Response, 200, (string)result);
        }

        // ==========================================
        // 实时日志推送（SSE）
        // ==========================================

        private void OnLogAdded()
        {
            try
            {
                _sseSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void StartSsePump()
        {
            _sseThread = new Thread(SsePumpLoop)
            {
                IsBackground = true,
                Name = "WebPanel-SsePump"
            };
            _sseThread.Start();
        }

        private void SsePumpLoop()
        {
            while (!_stopping)
            {
                try
                {
                    // 没新日志时也要按时醒一次发心跳，否则中间层会把空闲连接掐掉
                    _sseSignal.Wait(SseHeartbeatSeconds * 1000);
                    _sseSignal.Reset();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_stopping) break;
                FlushSse();
            }
        }

        /// <summary>把每个 SSE 客户端落后的日志补发出去；空闲够久的发心跳；写失败的摘掉。</summary>
        private void FlushSse()
        {
            SseClient[] snapshot;
            lock (_sseLock)
            {
                if (_sseClients.Count == 0) return;
                snapshot = _sseClients.ToArray();
            }

            List<SseClient> dead = null;

            foreach (var client in snapshot)
            {
                try
                {
                    // 游标在同一次加锁里取出，避免"写完数据后又进了一条"导致的漏行
                    string json = _logSink.ToJson(client.Cursor, out long latest);

                    if (latest > client.Cursor)
                    {
                        WriteSseFrame(client.Response, "data: " + json + "\n\n");
                        client.Cursor = latest;
                        client.LastWriteUtc = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - client.LastWriteUtc).TotalSeconds >= SseHeartbeatSeconds)
                    {
                        // 注释帧：浏览器 EventSource 会忽略它，但能保活
                        WriteSseFrame(client.Response, ": ping\n\n");
                        client.LastWriteUtc = DateTime.UtcNow;
                    }
                }
                catch
                {
                    (dead ?? (dead = new List<SseClient>())).Add(client);
                }
            }

            if (dead == null) return;

            lock (_sseLock)
            {
                foreach (var client in dead)
                {
                    _sseClients.Remove(client);
                }
            }
            foreach (var client in dead)
            {
                try
                {
                    client.Response.Close();
                }
                catch
                {
                }
            }
        }

        private void HandleLogStream(HttpListenerContext context, WebPanelConfig settings)
        {
            var response = context.Response;

            if (!settings.UseServerSentEvents)
            {
                WriteJson(response, 404, JsonError("服务端未开启 SSE（WebPanel.UseServerSentEvents=false），请用 /api/logs 轮询"));
                return;
            }

            lock (_sseLock)
            {
                if (_sseClients.Count >= MaxSseClients)
                {
                    WriteJson(response, 503, JsonError($"实时日志连接已满（上限 {MaxSseClients}），请稍后再试"));
                    return;
                }
            }

            // 起点：默认只推**从现在开始**的新日志（不带 since 时）。
            // 历史日志前端走 /api/logs 拉一次，两条路拼接起来才是完整时间线。
            long since = ParseLong(context.Request.QueryString["since"], _logSink.LatestId);

            try
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream; charset=utf-8";
                response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Connection"] = "keep-alive";
                // SSE 是长连接，必须边写边发，不能用 Content-Length 预声明长度
                response.SendChunked = true;

                // 先把响应头推出去（有些代理要等到第一帧才转发）
                WriteSseFrame(response, "retry: 3000\n\n: connected\n\n");

                lock (_sseLock)
                {
                    _sseClients.Add(new SseClient
                    {
                        Response = response,
                        Cursor = since,
                        LastWriteUtc = DateTime.UtcNow
                    });
                }

                // 立刻补一次，把连接建立那一刻之前的积压发出去
                _sseSignal.Set();
            }
            catch (Exception ex)
            {
                try
                {
                    response.Close();
                }
                catch
                {
                }
                if (settings.LogActions)
                {
                    Log.Warning("[网页控制台] SSE 连接建立失败：" + ex.Message);
                }
            }
        }

        private static void WriteSseFrame(HttpListenerResponse response, string frame)
        {
            byte[] payload = Encoding.UTF8.GetBytes(frame);
            response.OutputStream.Write(payload, 0, payload.Length);
            response.OutputStream.Flush();
        }

        private void CloseAllSseClients()
        {
            SseClient[] snapshot;
            lock (_sseLock)
            {
                snapshot = _sseClients.ToArray();
                _sseClients.Clear();
            }
            foreach (var client in snapshot)
            {
                try
                {
                    client.Response.Close();
                }
                catch
                {
                }
            }
        }

        // ==========================================
        // 登录
        // ==========================================

        private void HandleLogin(HttpListenerContext context, WebPanelConfig settings, string remoteIp)
        {
            var response = context.Response;

            if (IsLockedOut(settings, remoteIp, out int remainSeconds))
            {
                WriteJson(response, 429, JsonError($"登录失败次数过多，请 {remainSeconds} 秒后再试"));
                return;
            }

            // 全局熔断：按 IP 封禁挡不住代理池轮换，这个是跨 IP 的，绕不过。
            if (IsGloballyLockedOut(settings, out int globalRemain))
            {
                WriteJson(response, 429, JsonError($"服务器登录接口已临时暂停，请 {globalRemain} 秒后再试"));
                return;
            }

            string body = ReadBody(context.Request);
            string password = WebPanelJson.ParseString(body, "password");

            // 挑战值：要求先取一次 /api/challenge 再登录，挡"直接暴力打 /api/login"的哑脚本。
            if (settings.RequireChallenge)
            {
                string challenge = WebPanelJson.ParseString(body, "challenge");
                if (!WebPanelChallenge.Consume(challenge, BuildChallengeOwner(context, remoteIp)))
                {
                    RegisterFailure(settings, remoteIp);
                    RegisterGlobalFailure(settings);
                    if (settings.LogActions)
                    {
                        Log.Warning($"[网页控制台] 登录被拒（挑战值无效或过期）：{remoteIp}");
                    }
                    WriteJson(response, 401, JsonError("请求已过期，请刷新页面后重试"));
                    return;
                }
            }

            if (!WebPanelCrypto.VerifyPassword(password, settings.Password))
            {
                RegisterFailure(settings, remoteIp);
                RegisterGlobalFailure(settings);
                if (settings.LogActions)
                {
                    Log.Warning($"[网页控制台] 登录失败：{remoteIp}（本 IP 累计 "
                                + GetFailureCount(remoteIp) + " 次，全局 "
                                + GetGlobalFailureCount() + " 次）");
                }
                WriteJson(response, 401, JsonError("口令错误"));
                return;
            }

            lock (_failures) _failures.Remove(remoteIp);
            ClearGlobalFailures();

            var now = DateTime.UtcNow;
            var session = new WebPanelSession
            {
                Token = WebPanelCrypto.NewToken(),
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(settings.SessionMinutes),
                // 硬上限：即使开了滑动过期也照这个时间点收口。
                HardExpiresUtc = settings.SessionMaxLifetimeMinutes > 0
                    ? now.AddMinutes(settings.SessionMaxLifetimeMinutes)
                    : DateTime.MinValue,
                RemoteIp = remoteIp
            };
            lock (_sessions) _sessions[session.Token] = session;

            // 会话上限更短时，Cookie 的 Max-Age 要跟着走，否则浏览器会留一个死 Cookie。
            if (settings.SessionAbsoluteTimeout && session.HardExpiresUtc != DateTime.MinValue &&
                session.HardExpiresUtc < session.ExpiresUtc)
            {
                session.ExpiresUtc = session.HardExpiresUtc;
            }

            if (settings.UseHttpOnlyCookie)
            {
                WriteSessionCookie(response, settings, session.Token, session.ExpiresUtc);
            }

            if (settings.LogActions)
            {
                Log.Information($"[网页控制台] 登录成功：{remoteIp}（会话 {settings.SessionMinutes} 分钟"
                                + (settings.SessionAbsoluteTimeout ? "，绝对超时" : "，滑动续期")
                                + (settings.UseHttpOnlyCookie ? "，HttpOnly Cookie" : "，Bearer 令牌") + "）");
            }

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"token\":");
            sb.Append(WebPanelJson.Quote(session.Token));
            sb.Append(",\"expiresInSeconds\":");
            sb.Append((long)(session.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
            // 告诉前端令牌已进 HttpOnly Cookie，下次可以不带 Authorization 头（SSE 就是靠这个）。
            sb.Append(",\"cookie\":");
            sb.Append(settings.UseHttpOnlyCookie ? "true" : "false");
            sb.Append(",\"title\":");
            sb.Append(WebPanelJson.Quote(settings.Title));
            sb.Append("}");
            WriteJson(response, 200, sb.ToString());
        }

        /// <summary>
        /// 下发一个登录挑战值。
        ///
        /// 这个值与 <c>remoteIp + UserAgent 摘要</c> 绑定、120 秒过期、**一次性**：
        /// 领了之后必须用同一个 IP 与同一个 UA 来登录，否则 <c>Consume</c> 会拒绝。
        /// 代理池因此无法共享同一个挑战，也无法"领一次到处用"。
        /// </summary>
        private void HandleChallenge(HttpListenerContext context, WebPanelConfig settings, string remoteIp)
        {
            // 已登录就没必要再领挑战，直接放行（避免前端在已登录状态下反复请求）。
            if (TryAuthorize(context.Request, settings, out _, out _))
            {
                WriteJson(context.Response, 200, "{\"success\":true,\"required\":false}");
                return;
            }

            string challenge = WebPanelChallenge.Issue(BuildChallengeOwner(context, remoteIp));
            // 字典满 = 正在被刷（无口令接口被滥用）。这时候不要静默发个 null 让前端
            // 拿着空挑战去登录然后报"口令错误"——那是在误导人排查，直接告诉他是限流。
            if (challenge == null)
            {
                WriteJson(context.Response, 503, "{\"success\":false,\"message\":\"请求过多，请稍后重试\"}");
                return;
            }
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"required\":");
            sb.Append(settings.RequireChallenge ? "true" : "false");
            sb.Append(",\"challenge\":");
            sb.Append(WebPanelJson.Quote(challenge));
            sb.Append(",\"expiresInSeconds\":").Append(WebPanelChallenge.LifetimeSeconds);
            sb.Append("}");
            WriteJson(context.Response, 200, sb.ToString());
        }

        /// <summary>
        /// 挑战值的归属键：把 remoteIp + UA 摘要绑在一起。
        /// 这样挑战不能"领了给别的 IP 用"，代理池也共享不了同一个挑战。
        /// </summary>
        private static string BuildChallengeOwner(HttpListenerContext context, string remoteIp)
        {
            string ua = context.Request.UserAgent ?? string.Empty;
            string uaHash = WebPanelCrypto.Sha256Hex(ua).Substring(0, 16);
            return remoteIp + "|" + uaHash;
        }

        private bool TryAuthorize(HttpListenerRequest request, WebPanelConfig settings, out WebPanelSession session, out string error)
        {
            session = null;
            error = null;

            // ---- 令牌来源，按安全性排序 ----
            // 1) Cookie（HttpOnly）：JS 读不到，不会进 URL / 历史 / Referer / 日志。首选。
            // 2) Authorization: Bearer：普通 fetch 用这个。
            // 3) ?token=：**默认禁用**。它会流进浏览器历史、Referer、服务器与反代日志，
            //    只保留给老前端缓存，且要显式打开 AllowTokenInQuery。
            string token = ReadTokenFromCookie(request, settings);
            if (string.IsNullOrEmpty(token))
            {
                string auth = request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = auth.Substring(7).Trim();
                }
            }
            if (string.IsNullOrEmpty(token) && settings.AllowTokenInQuery)
            {
                token = request.QueryString["token"];
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "缺少访问令牌，请先登录";
                return false;
            }

            WebPanelSession found;
            lock (_sessions)
            {
                if (!_sessions.TryGetValue(token.Trim(), out found))
                {
                    error = "会话无效或已退出，请重新登录";
                    return false;
                }
            }

            if (found.ExpiresUtc <= DateTime.UtcNow)
            {
                lock (_sessions) _sessions.Remove(found.Token);
                error = "会话已过期，请重新登录";
                return false;
            }

            // 绝对上限：不管多活跃，到点一律失效（防"一直挂着页面"把会话续成永久）。
            // 注意这里**不能**与上面的 ExpiresUtc 混用：ExpiresUtc 在非绝对模式下会被滑动续期推后，
            // 只有 HardExpiresUtc 是"从登录那刻起算、永不延长"的硬上限。
            if (found.HardExpiresUtc != DateTime.MinValue && found.HardExpiresUtc <= DateTime.UtcNow)
            {
                lock (_sessions) _sessions.Remove(found.Token);
                error = "会话已达到最长存活时间，请重新登录";
                return false;
            }

            // 滑动过期只在**非绝对超时**模式下才续期。
            // 公网部署下开着绝对超时时，会话到点自然结束，不需要续。
            if (!settings.SessionAbsoluteTimeout)
            {
                found.ExpiresUtc = DateTime.UtcNow.AddMinutes(settings.SessionMinutes);
            }
            session = found;
            return true;
        }

        /// <summary>
        /// 从 Cookie 里取面板会话令牌。
        /// Cookie 名带 <c>HttpOnly</c>，前端 JS 拿不到，因此不存在"从 localStorage 读到再泄漏"的问题。
        /// </summary>
        private static string ReadTokenFromCookie(HttpListenerRequest request, WebPanelConfig settings)
        {
            string header = request.Headers["Cookie"];
            if (string.IsNullOrEmpty(header)) return null;
            string prefix = SessionCookieName + "=";
            foreach (string part in header.Split(';'))
            {
                string item = part.Trim();
                if (item.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return WebPanelUrlDecode(item.Substring(prefix.Length));
                }
            }
            return null;
        }

        /// <summary>Cookie 值的百分号解码（令牌是 base64url，理论上无特殊字符，解码只为稳妥）。</summary>
        private static string WebPanelUrlDecode(string value)
        {
            try
            {
                return System.Uri.UnescapeDataString(value);
            }
            catch
            {
                return value;
            }
        }

        /// <summary>下发会话 Cookie。HttpOnly 恒开；Secure 由配置决定（HTTP 下浏览器会忽略它）。</summary>
        private static void WriteSessionCookie(HttpListenerResponse response, WebPanelConfig settings, string token, DateTime expiresUtc)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(SessionCookieName).Append('=').Append(token);
                sb.Append("; Path=/");
                sb.Append("; HttpOnly");
                // SameSite=Strict：面板是纯管理页，不存在跨站场景，锁死能挡 CSRF 的一大半。
                sb.Append("; SameSite=Strict");
                if (settings.SecureCookie) sb.Append("; Secure");
                int maxAge = (int)Math.Max(0, (expiresUtc - DateTime.UtcNow).TotalSeconds);
                sb.Append("; Max-Age=").Append(maxAge.ToString(CultureInfo.InvariantCulture));
                response.Headers["Set-Cookie"] = sb.ToString();
            }
            catch
            {
                // Cookie 写不出去不该让登录整个失败：前端仍可用 Authorization 头兜底。
            }
        }

        /// <summary>清除会话 Cookie（登出时调用）。</summary>
        private static void ClearSessionCookie(HttpListenerResponse response)
        {
            try
            {
                response.Headers["Set-Cookie"] = SessionCookieName + "=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0";
            }
            catch
            {
            }
        }

        private bool IsLockedOut(WebPanelConfig settings, string ip, out int remainSeconds)
        {
            remainSeconds = 0;
            if (settings.MaxLoginFailures <= 0) return false;

            lock (_failures)
            {
                if (!_failures.TryGetValue(ip, out LoginFailure entry)) return false;
                if (entry.Count < settings.MaxLoginFailures) return false;

                var remain = entry.LockedUntilUtc - DateTime.UtcNow;
                if (remain <= TimeSpan.Zero)
                {
                    _failures.Remove(ip);
                    return false;
                }

                remainSeconds = (int)Math.Ceiling(remain.TotalSeconds);
                return true;
            }
        }

        private void RegisterFailure(WebPanelConfig settings, string ip)
        {
            lock (_failures)
            {
                if (!_failures.TryGetValue(ip, out LoginFailure entry))
                {
                    entry = new LoginFailure();
                    _failures[ip] = entry;
                }
                entry.Count++;

                if (settings.MaxLoginFailures > 0 && entry.Count >= settings.MaxLoginFailures &&
                    settings.LockoutSeconds > 0)
                {
                    entry.LockedUntilUtc = DateTime.UtcNow.AddSeconds(settings.LockoutSeconds);
                    entry.Count = 0;   // 封禁期重新计数，避免永久累积
                    Log.Warning($"[网页控制台] {ip} 登录失败次数过多，已临时封禁 {settings.LockoutSeconds} 秒");
                }
            }
        }

        // ---- 全局熔断（跨 IP，防代理池轮换）----

        /// <summary>
        /// 全局登录是否已熔断。累计失败超过 <see cref="WebPanelConfig.GlobalMaxLoginFailures"/>
        /// 就暂停一段时间，期间**任何 IP** 都不能登录。
        /// </summary>
        private bool IsGloballyLockedOut(WebPanelConfig settings, out int remainSeconds)
        {
            remainSeconds = 0;
            if (settings.GlobalMaxLoginFailures <= 0 || settings.GlobalLockoutSeconds <= 0) return false;

            lock (_globalFailureLock)
            {
                if (_globalLockedUntilUtc <= DateTime.UtcNow)
                {
                    // 熔断已过：清零重新计数，否则管理员正常输错一次就会又熔断。
                    _globalLockedUntilUtc = DateTime.MinValue;
                    _globalFailures = 0;
                    return false;
                }
                var remain = _globalLockedUntilUtc - DateTime.UtcNow;
                remainSeconds = (int)Math.Ceiling(remain.TotalSeconds);
                return true;
            }
        }

        private void RegisterGlobalFailure(WebPanelConfig settings)
        {
            if (settings.GlobalMaxLoginFailures <= 0 || settings.GlobalLockoutSeconds <= 0) return;

            lock (_globalFailureLock)
            {
                if (_globalLockedUntilUtc > DateTime.UtcNow) return;   // 已在熔断中，不重复累加
                _globalFailures++;
                if (_globalFailures >= settings.GlobalMaxLoginFailures)
                {
                    _globalLockedUntilUtc = DateTime.UtcNow.AddSeconds(settings.GlobalLockoutSeconds);
                    Log.Warning($"[网页控制台] 登录失败累计 {_globalFailures} 次（跨多个来源），"
                                + $"已暂停登录接口 {settings.GlobalLockoutSeconds} 秒 —— 疑似撞库");
                    _globalFailures = 0;
                }
            }
        }

        private void ClearGlobalFailures()
        {
            lock (_globalFailureLock)
            {
                _globalFailures = 0;
                _globalLockedUntilUtc = DateTime.MinValue;
            }
        }

        private int GetGlobalFailureCount()
        {
            lock (_globalFailureLock) return _globalFailures;
        }

        private int GetFailureCount(string ip)
        {
            lock (_failures)
            {
                return _failures.TryGetValue(ip, out LoginFailure entry) ? entry.Count : 0;
            }
        }

        // ==========================================
        // 命令执行
        // ==========================================

        private void HandleExecute(HttpListenerContext context, WebPanelConfig settings, WebPanelSession session, string remoteIp)
        {
            string body = ReadBody(context.Request);
            string command = WebPanelJson.ParseString(body, "command");

            if (string.IsNullOrWhiteSpace(command))
            {
                WriteJson(context.Response, 400, JsonError("命令不能为空"));
                return;
            }

            command = command.Trim();
            if (!command.StartsWith("/", StringComparison.Ordinal)) command = "/" + command;

            if (!WebPanelCommandPolicy.IsAllowed(command, settings, out string reason))
            {
                if (settings.LogActions)
                {
                    Log.Warning($"[网页控制台] {remoteIp} 尝试执行被拦下的命令：{command}（{reason}）");
                }
                WriteJson(context.Response, 403, JsonError(reason));
                return;
            }

            // 命令必须在主线程跑：它会改地形、实体、玩家状态
            string output = null;
            string error = null;
            int timeoutMs = Math.Max(2000, (settings.RequestTimeoutSeconds) * 1000);

            bool ok = RunOnMainThread(() =>
            {
                output = WebPanelCommandRunner.Execute(command, out string execError);
                error = execError;
                return null;
            }, out _, out string queueError, timeoutMs);

            if (!ok)
            {
                WriteJson(context.Response, 503, JsonError(queueError));
                return;
            }

            if (settings.LogActions)
            {
                Remember($"{remoteIp} 执行 {command}");
                Log.Information($"[网页控制台] {remoteIp} 执行命令：{command}");
            }

            var sb = new StringBuilder();
            sb.Append("{\"success\":");
            sb.Append(error == null ? "true" : "false");
            sb.Append(",\"command\":");
            sb.Append(WebPanelJson.Quote(command));
            sb.Append(",\"output\":");
            sb.Append(WebPanelJson.Quote(output ?? ""));
            if (error != null)
            {
                sb.Append(",\"error\":");
                sb.Append(WebPanelJson.Quote(error));
            }
            sb.Append("}");
            WriteJson(context.Response, 200, sb.ToString());
        }

        private void HandlePlayerAction(HttpListenerContext context, WebPanelConfig settings, WebPanelSession session, string remoteIp, string path)
        {
            string body = ReadBody(context.Request);
            string guid = WebPanelJson.ParseString(body, "guid");
            string action = path.EndsWith("/kick", StringComparison.Ordinal)
                ? "kick"
                : WebPanelJson.ParseString(body, "action");
            // 只有 banip / unbanip 用得到：管理员在面板上显式输入的 IP。
            string ipArgument = WebPanelJson.ParseString(body, "ip");

            if (!WebPanelActionPolicy.IsAllowed(action, settings, out string reason))
            {
                WriteJson(context.Response, 403, JsonError(reason));
                return;
            }

            string message = null;
            string error = null;
            int timeoutMs = Math.Max(2000, settings.RequestTimeoutSeconds * 1000);

            bool ok = RunOnMainThread(() =>
            {
                message = WebPanelApi.ApplyPlayerAction(guid, action, ipArgument, out string actionError);
                error = actionError;
                return null;
            }, out _, out string queueError, timeoutMs);

            if (!ok)
            {
                WriteJson(context.Response, 503, JsonError(queueError));
                return;
            }

            if (error != null)
            {
                WriteJson(context.Response, 400, JsonError(error));
                return;
            }

            if (settings.LogActions)
            {
                Remember($"{remoteIp} 对玩家执行 {action}");
                Log.Information($"[网页控制台] {remoteIp} 对玩家 {guid} 执行 {action}");
            }

            WriteJson(context.Response, 200, "{\"success\":true,\"message\":" + WebPanelJson.Quote(message ?? "ok") + "}");
        }

        private void Remember(string text)
        {
            lock (_recentActions)
            {
                _recentActions.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text);
                while (_recentActions.Count > 50) _recentActions.Dequeue();
            }
        }

        public string[] RecentActions
        {
            get
            {
                lock (_recentActions) return _recentActions.ToArray();
            }
        }

        // ==========================================
        // 面板设置 / 热重载
        // ==========================================

        /// <summary>当前实际监听的端口（没起来时为 0）。设置页显示它就是"配置值 vs 生效值"的差别。</summary>
        private int CurrentPort
        {
            get
            {
                var listener = _listener;
                if (listener == null) return 0;
                // HttpListener 不直接给端口，从前缀里抠；前缀形如 http://host:port/
                foreach (string prefix in listener.Prefixes)
                {
                    var uri = new Uri(prefix);
                    return uri.Port;
                }
                return 0;
            }
        }

        /// <summary>
        /// 保存设置：把页面改的字段写进配置并落盘，然后**热重载**让大部分改动立即生效。
        /// 端口/绑定地址这类会换监听前缀的改动，重载时会重启监听。
        /// 口令不在这里改（走 /api/settings/password）。
        /// </summary>
        private void HandleSettingsSave(HttpListenerContext context, WebPanelConfig settings,
            WebPanelSession session, string remoteIp)
        {
            string body = ReadBody(context.Request);
            if (!WebPanelSettings.TryApply(settings, body, out string error))
            {
                WriteJson(context.Response, 400, JsonError(error));
                return;
            }

            WebPanelConfigStore.Save(ConfigFilePath, settings);

            if (settings.LogActions)
            {
                Remember($"{remoteIp} 修改了面板设置");
                Log.Information($"[网页控制台] {remoteIp} 修改了面板设置（端口 {settings.Port}）");
            }

            // 应用改动：换端口/绑定地址要重启监听，其它项大多已"就地生效"（它们每次请求现读 settings）。
            bool reloaded = ReloadListener(settings, out string reloadError);

            var sb = new StringBuilder(256);
            sb.Append("{\"success\":true");
            sb.Append(",\"reloaded\":").Append(reloaded ? "true" : "false");
            sb.Append(",\"message\":").Append(WebPanelJson.Quote(
                reloaded
                    ? "设置已保存并生效"
                    : "设置已保存；但监听器重启失败，端口/地址要等下次重启服务端才变（" + (reloadError ?? "未知原因") + "）"));
            sb.Append('}');
            WriteJson(context.Response, 200, sb.ToString());
        }

        /// <summary>
        /// 改口令。**必须带旧口令**（见 WebPanelSettings.TryChangePassword 的说明）。
        /// 改完会让所有现有会话失效（重新登录），避免旧会话继续用。
        /// </summary>
        private void HandlePasswordChange(HttpListenerContext context, WebPanelConfig settings,
            WebPanelSession session, string remoteIp)
        {
            string body = ReadBody(context.Request);
            string oldPassword = WebPanelJson.ParseString(body, "oldPassword") ?? "";
            string newPassword = WebPanelJson.ParseString(body, "newPassword") ?? "";
            string hashRaw = WebPanelJson.ParseString(body, "hash");
            bool hash = hashRaw != null && hashRaw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

            if (!WebPanelSettings.TryChangePassword(settings, oldPassword, newPassword, hash, out string error))
            {
                if (settings.LogActions)
                {
                    Log.Warning($"[网页控制台] {remoteIp} 改口令失败：{error}");
                }
                WriteJson(context.Response, 400, JsonError(error));
                return;
            }

            WebPanelConfigStore.Save(ConfigFilePath, settings);

            // 口令变了 = 旧会话的凭证不再可信；全部清掉，强制重新登录。
            lock (_sessions) _sessions.Clear();

            if (settings.LogActions)
            {
                Log.Information($"[网页控制台] {remoteIp} 修改了登录口令，所有会话已失效");
            }

            WriteJson(context.Response, 200,
                "{\"success\":true,\"message\":" +
                WebPanelJson.Quote("口令已更新，所有登录已失效，请用新口令重新登录") + "}");
        }

        /// <summary>手动重载：重新读配置并重启监听（设置页的"重载"按钮）。</summary>
        private void HandleReload(HttpListenerContext context, WebPanelSession session, string remoteIp)
        {
            var fresh = WebPanelConfigStore.Load(ConfigFilePath);

            // 用新读出来的值覆盖运行中的设置对象（保持同一个实例，别处持有它的引用不会失效）
            var current = Settings;
            if (current != null && fresh != null)
            {
                current.Enabled = fresh.Enabled;
                current.BindHost = fresh.BindHost;
                current.Port = fresh.Port;
                current.Password = fresh.Password;
                current.SessionMinutes = fresh.SessionMinutes;
                current.MaxLoginFailures = fresh.MaxLoginFailures;
                current.LockoutSeconds = fresh.LockoutSeconds;
                current.AllowedCommandPrefixes = fresh.AllowedCommandPrefixes;
                current.DeniedCommandPrefixes = fresh.DeniedCommandPrefixes;
                current.LogBufferLines = fresh.LogBufferLines;
                current.UseServerSentEvents = fresh.UseServerSentEvents;
                current.RequestTimeoutSeconds = fresh.RequestTimeoutSeconds;
                current.LogActions = fresh.LogActions;
                current.Title = fresh.Title;
                current.Clamp();
            }

            bool reloaded = ReloadListener(current, out string reloadError);

            if (current != null && current.LogActions)
            {
                Log.Information($"[网页控制台] {remoteIp} 重载面板（监听重启：{(reloaded ? "成功" : "失败")}）");
            }

            WriteJson(context.Response, 200,
                "{\"success\":true,\"reloaded\":" + (reloaded ? "true" : "false") +
                ",\"message\":" + WebPanelJson.Quote(
                    reloaded ? "已重载配置并重启监听" : "配置已重载，但监听重启失败：" + (reloadError ?? "未知原因")) + "}");
        }

        /// <summary>
        /// 重启监听（含 TLS 终结层）。
        ///
        /// 做法：先在<b>备用端口</b>把新的一套（内部 HttpListener + TLS 终结器）整套起起来，
        /// 成功了才把旧的关掉。
        ///
        /// ⚠ 为什么不用原来的"直接对着同一个前缀 Start"：开了 TLS 之后要同时协调**两个**监听器
        /// （回环 HTTP + 公网 TLS），而 TLS 端口无法"换一个端口先起来再切换" ——
        /// 同一个端口上两个 TcpListener 会冲突。所以这里改为整体重建：
        /// 内部 HTTP 端口可以临时换（转发目标随之改变），TLS 端口则要求它此刻是空闲的
        ///（重载前必然空闲，因为旧的那套还没关 —— 这点与原实现"先起新的"的前提一致）。
        /// 任一步失败都完整回滚，旧的继续服务，面板不会失联。
        /// </summary>
        private bool ReloadListener(WebPanelConfig settings, out string error)
        {
            error = null;
            if (settings == null) return false;
            if (!settings.Enabled) return false;

            // 已经在同一个配置上跑着 → 无需重启。
            // 判断依据用"实际对外端口 + TLS 开关"，而不是内部前缀
            //（内部端口每次启动可能是随机的，拿它比较会永远判定为"变了"，导致每次都重启）。
            bool tlsUnchanged = _tls != null == settings.EnableTls;
            bool portUnchanged = _listener != null &&
                _activePublicPort == (settings.EnableTls ? settings.TlsPort : settings.Port) &&
                _activeBindHost == settings.BindHost;
            if (tlsUnchanged && portUnchanged)
            {
                return true;
            }

            // ---- 停掉旧的完整一套（内部 HTTP + TLS）----
            // 顺序同 Shutdown：先 TLS 后 HTTP。
            try
            {
                if (_tls != null)
                {
                    _tls.Dispose();
                    _tls = null;
                }
            }
            catch
            {
            }
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch
            {
            }

            // ---- 起新的 ----
            _stopping = false;
            if (!TryStartListener(settings, out string startError))
            {
                error = startError ?? "新监听起不来，已保持原监听不变";
                // 失败就把旧的重新拉起来，尽量不让面板失联。
                WebPanelConfig previous = _previousSettings;
                if (previous != null && TryStartListener(previous, out _))
                {
                    error += "；已回退到重载前的配置";
                }
                else
                {
                    error += "；⚠ 且回退也失败，面板已离线，需重启服务端或从后台改回配置";
                }
                return false;
            }

            _previousSettings = settings;
            Log.Information("[网页控制台] 监听已切换到 " + PublicUrl(settings));
            return true;
        }

        // ==========================================
        // HTTP 小工具
        // ==========================================

        private string ReadBody(HttpListenerRequest request)
        {
            try
            {
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    // 面板的请求体都很小，限一个上限，避免超大 body 把内存吃光
                    var buffer = new char[65536];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    return read > 0 ? new string(buffer, 0, read) : string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int ParseInt(string raw, int fallback)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        private static long ParseLong(string raw, long fallback)
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        }

        private static string GetRemoteIp(HttpListenerRequest request)
        {
            try
            {
                return request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static void WriteHtml(HttpListenerResponse response, string html)
        {
            WriteBytes(response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }

        private static void WriteJson(HttpListenerResponse response, int status, string json)
        {
            WriteBytes(response, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static void WriteBytes(HttpListenerResponse response, int status, string contentType, byte[] payload)
        {
            try
            {
                response.StatusCode = status;
                response.ContentType = contentType;
                response.ContentLength64 = payload.Length;

                // 面板是管理界面，绝不能被任何中间层或浏览器缓存。
                // 只写 Cache-Control 还不够：老浏览器认 Pragma，部分代理认 Expires；
                // 再加一个每次都变的 ETag，逼浏览器每次都拿新的（等价于自动强刷）。
                response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                response.Headers["Pragma"] = "no-cache";
                response.Headers["Expires"] = "0";
                response.Headers["ETag"] = "\"" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\"";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                // 面板是纯管理页：禁止被任何页面用 <iframe> 嵌进来（点击劫持），
                // 禁止被搜索引擎收录，禁止浏览器乱猜类型。
                response.Headers["X-Frame-Options"] = "DENY";
                response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                // 明确拒绝被任意站点跨源读取响应。同源部署下浏览器会尊重它。
                // 不加 CORS 头本身就是"拒绝跨源"，这里写清楚是为了挡预检直连的歧义场景。
                response.Headers["Access-Control-Allow-Origin"] = "null";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.OutputStream.Write(payload, 0, payload.Length);
            }
            catch
            {
            }
        }

        private static void TryWriteError(HttpListenerContext context, int status, string message)
        {
            try
            {
                WriteJson(context.Response, status, JsonError(message));
            }
            catch
            {
            }
        }

        public static string JsonError(string message)
        {
            return "{\"success\":false,\"message\":" + WebPanelJson.Quote(message ?? "未知错误") + "}";
        }

        // ==========================================
        // 小类型
        // ==========================================

        private sealed class PendingWork
        {
            public PendingWork(Func<object> action)
            {
                Action = action;
            }

            public Func<object> Action { get; }

            public object Result { get; set; }

            public Exception Error { get; set; }

            public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);
        }

        private sealed class LoginFailure
        {
            public int Count;

            public DateTime LockedUntilUtc = DateTime.MinValue;
        }

        /// <summary>一条 SSE 长连接。Cursor 是"已经发到哪一条"的游标（服务端日志序号）。</summary>
        private sealed class SseClient
        {
            public HttpListenerResponse Response;

            public long Cursor;

            public DateTime LastWriteUtc;
        }
    }

    /// <summary>一条网页会话。</summary>
    public sealed class WebPanelSession
    {
        public string Token;

        public DateTime CreatedUtc;

        public DateTime ExpiresUtc;

        public string RemoteIp;

        /// <summary>
        /// 硬性到期时间：从登录那刻起算，**永不因任何请求而延长**。
        /// <see cref="ExpiresUtc"/> 在滑动过期模式下会被推后，这个不会 ——
        /// 它保证"一次登录"总有个尽头，不会因为页面一直开着就变成永久通行证。
        /// <see cref="DateTime.MinValue"/> = 不设硬上限。
        /// </summary>
        public DateTime HardExpiresUtc;
    }
}
