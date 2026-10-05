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

        /// <summary>日志环形缓冲（终端页的数据源）。</summary>
        private readonly WebPanelLogSink _logSink = new WebPanelLogSink();

        /// <summary>会话令牌表。key = token，value = 会话。</summary>
        private readonly Dictionary<string, WebPanelSession> _sessions =
            new Dictionary<string, WebPanelSession>(StringComparer.Ordinal);

        /// <summary>登录失败计数：IP -> (次数, 封禁截止时间)。</summary>
        private readonly Dictionary<string, LoginFailure> _failures =
            new Dictionary<string, LoginFailure>(StringComparer.Ordinal);

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

            Log.Information($"[网页控制台] 已启动：http://{DescribeHost(settings.BindHost)}:{settings.Port}/ " +
                            $"（日志缓冲 {settings.LogBufferLines} 行，网页终端允许的命令前缀：" +
                            $"{(WebPanelConfig.SplitPrefixes(settings.AllowedCommandPrefixes).Length == 0 ? "无" : settings.AllowedCommandPrefixes)}）");

            if (IsExternallyVisible(settings.BindHost))
            {
                Log.Warning("[网页控制台] 注意：监听地址 " + settings.BindHost +
                            " 允许外部访问。请确认口令足够强，必要时改用 127.0.0.1 并用反向代理暴露。");
            }
        }

        private bool TryStartListener(WebPanelConfig settings, out string error)
        {
            error = null;

            string prefix = BuildPrefix(settings.BindHost, settings.Port);
            try
            {
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
                return true;
            }
            catch (HttpListenerException ex)
            {
                // 最常见的就是端口被占 / 没有 URL ACL 权限。把这两条直接写进提示里，省得反复猜。
                error = $"HttpListener 无法监听 {prefix}（错误码 {ex.ErrorCode}）：{ex.Message}\n" +
                        "       常见原因：① 端口已被占用；② 绑定 +/* 时缺少 URL ACL —— " +
                        $"以管理员执行 `netsh http add urlacl url={prefix} user=Everyone`，或把 BindHost 改回 127.0.0.1。";
                return false;
            }
            catch (Exception ex)
            {
                error = $"HttpListener 初始化异常（{ex.GetType().Name}）：{ex.Message}";
                return false;
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

            // 先把 SSE 连接都关掉：否则浏览器会一直挂着半开连接
            CloseAllSseClients();
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

            // ---------- 其余全部要求已登录 ----------
            if (!TryAuthorize(request, settings, out WebPanelSession session, out string authError))
            {
                WriteJson(response, 401, JsonError(authError ?? "未登录或会话已过期"));
                return;
            }

            if (path == "/api/logout")
            {
                lock (_sessions) _sessions.Remove(session.Token);
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

            string body = ReadBody(context.Request);
            string password = WebPanelJson.ParseString(body, "password");

            if (!WebPanelCrypto.VerifyPassword(password, settings.Password))
            {
                RegisterFailure(settings, remoteIp);
                if (settings.LogActions)
                {
                    Log.Warning($"[网页控制台] 登录失败：{remoteIp}");
                }
                WriteJson(response, 401, JsonError("口令错误"));
                return;
            }

            lock (_failures) _failures.Remove(remoteIp);

            var session = new WebPanelSession
            {
                Token = WebPanelCrypto.NewToken(),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(settings.SessionMinutes),
                RemoteIp = remoteIp
            };
            lock (_sessions) _sessions[session.Token] = session;

            if (settings.LogActions)
            {
                Log.Information($"[网页控制台] 登录成功：{remoteIp}（会话 {settings.SessionMinutes} 分钟）");
            }

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"token\":");
            sb.Append(WebPanelJson.Quote(session.Token));
            sb.Append(",\"expiresInSeconds\":");
            sb.Append((long)(session.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
            sb.Append(",\"title\":");
            sb.Append(WebPanelJson.Quote(settings.Title));
            sb.Append("}");
            WriteJson(response, 200, sb.ToString());
        }

        private bool TryAuthorize(HttpListenerRequest request, WebPanelConfig settings, out WebPanelSession session, out string error)
        {
            session = null;
            error = null;

            // 令牌可以放 Authorization: Bearer xxx，也可以放 ?token=xxx（SSE 用不了自定义头）
            string token = null;
            string auth = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = auth.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(token))
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

            // 用一次就续一次（滑动过期），避免长时间盯着页面突然被踢
            found.ExpiresUtc = DateTime.UtcNow.AddMinutes(settings.SessionMinutes);
            session = found;
            return true;
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
        /// 重启 HTTP 监听（不动日志捕获 / SSE 泵 / 会话表里已登录的人）。
        /// 只有监听前缀（host:port）变了才真重启；否则视为无需动作。
        /// ⚠ 这一步**不能**顺序错误：必须先起新的、成功后再停旧的；否则中途失败会
        /// 变成"旧的停了、新的没起"，面板直接失联 —— 那只能靠重启服务端补救。
        /// </summary>
        private bool ReloadListener(WebPanelConfig settings, out string error)
        {
            error = null;
            if (settings == null) return false;
            if (!settings.Enabled) return false;

            string desired = BuildPrefix(settings.BindHost, settings.Port);

            // 已经在监听同一个前缀 → 无需重启
            var existing = _listener;
            if (existing != null)
            {
                bool same = false;
                foreach (string prefix in existing.Prefixes)
                {
                    if (string.Equals(prefix, desired, StringComparison.OrdinalIgnoreCase)) { same = true; break; }
                }
                if (same)
                {
                    return true;
                }
            }

            // 起新的（失败就保持旧的继续服务）
            HttpListener fresh;
            try
            {
                fresh = new HttpListener();
                fresh.Prefixes.Add(desired);
                fresh.Start();
            }
            catch (HttpListenerException ex)
            {
                error = $"新监听 {desired} 起不来（错误码 {ex.ErrorCode}）：{ex.Message}" +
                        "；可能端口被占或缺少 URL ACL，已保持原监听不变";
                return false;
            }
            catch (Exception ex)
            {
                error = $"新监听初始化异常：{ex.Message}；已保持原监听不变";
                return false;
            }

            // 新的起来了 → 换掉旧的
            var old = _listener;
            _listener = fresh;

            if (old != null)
            {
                try
                {
                    old.Stop();
                    old.Close();
                }
                catch
                {
                }
            }

            // 监听线程：让它自然跑到下一个循环会发现 _listener 变了。
            // 为简单起见，旧线程在一次 GetContext 抛异常/返回后会退出；这里再起一条新线程兜住。
            if (_listenThread == null || !_listenThread.IsAlive)
            {
                _stopping = false;
                _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "WebPanelListener" };
                _listenThread.Start();
            }

            Log.Information("[网页控制台] 监听已切换到 " + desired);
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
    }
}
