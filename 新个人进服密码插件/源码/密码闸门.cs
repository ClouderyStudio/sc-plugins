using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Engine;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using Game.Server;
using HarmonyLib;
using LiteNetLib;

namespace NewPersonalCode
{
    /// <summary>
    /// 进服校验闸门：拦截核心的握手流程，决定"这个身份 + 这个密码"能不能进。
    ///
    /// <para>
    /// <b>威胁模型</b>：SCKey token / 游客 GUID 被别人拿到 ⇒ 他不用知道你的任何密码
    /// 就能以你的身份连进来，于是你的存档、你的领地、你的背包都成了他的。
    /// 开了本插件之后，进服还得多报一个密码——那是只有你知道的第二因素。
    /// 注意它 <b>不是加密</b>，只是把门槛抬高。
    /// </para>
    ///
    /// <para>
    /// <b>为什么必须用 Harmony 而不是官方的 <c>PasswordValidationEventManager</c></b>
    /// （2026-10-05 逐行读 <c>ConnectionRequestPackage</c> 确认）：
    /// 核心确实有官方扩展点 <c>IPasswordValidationEventHandle</c>，看起来应该直接用它。
    /// 但它在本版本里是<b>死代码</b>：<c>AcceptAclient</c> 里那句
    /// <c>else if (!string.IsNullOrEmpty(password))</c>（password 取自
    /// <c>subsystemGameInfo.WorldSettings.Password</c>）把整段校验包住了——
    /// 房间密码为空（我们不填那个世界设置）时 <c>ValidatePassword</c> <b>根本不会被调用</b>，
    /// 事件管理器一次都不会触发。所以只能 Harmony 接管。
    /// </para>
    ///
    /// <para>两处补丁，缺一不可：</para>
    /// <list type="number">
    /// <item><c>ServerInfoPackage..ctor(bool)</c> 后缀——让 <c>m_needPasswd</c> 为 true。
    /// 客户端 <c>NetPlayScreen</c> 靠这个字段决定点服务器时弹不弹密码框。
    /// 不改这里，玩家看不到输入框，密码永远送不上来。</item>
    /// <item><c>ConnectionRequestPackage.AcceptAclient</c> 前缀——真正的校验点。
    /// 校验不过就往 <c>connectionError</c> 里追加原因，再交回核心，由核心原有的
    /// <c>ConnectionRejectPackage</c> 流程把文案发给客户端。</item>
    /// </list>
    /// </summary>
    internal static class ConnectionPasswordGate
    {
        private const string HarmonyId = "sc.plugin.newpersonalcode";

        private static readonly object _sync = new object();

        /// <summary>GUID(N) → 连续失败次数。</summary>
        private static readonly Dictionary<string, int> _failures =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// IP → 连续失败次数。**这是真正的封禁维度。**
        ///
        /// 为什么必须有它：只按 GUID 计数时，扫码 / 第三方入口进来的客户端 GUID 全是
        /// <c>00000000-0000-0000-0000-000000000001</c>——所有人共用同一个 key，
        /// 计数互相累加，一个倒霉输错就把同入口的其他人一起连坐。
        /// 两个维度都要留着：GUID 管《这台设备换 IP 也没用》，IP 管《这个来源换 GUID 也没用》，
        /// 取任一超限即踢。
        /// </summary>
        private static readonly Dictionary<string, int> _failuresByIp =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>key → 最后一次失败的时间（游戏内秒）。</summary>
        private static readonly Dictionary<string, double> _failureAt =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>失败计数的衰减时间（秒）。超过这个时间没再失败就视为重新开始。</summary>
        private const double FailureTtlSeconds = 300.0;

        private static bool _patched;
        private static bool _fieldWarned;
        private static int _passed;
        private static int _rejected;
        private static double _reportAt;

        private static ConnectionPasswordConfig Cfg => NewPersonalCodePlugin.Instance?.Config;

        /// <summary>总开关。补丁和命令都靠它判"现在是否接管"；关掉时行为与核心原生完全一致。</summary>
        internal static bool IsActive
        {
            get
            {
                ConnectionPasswordConfig cfg = Cfg;
                return cfg != null && cfg.Enabled;
            }
        }

        // ==========================================
        // 接管核心
        // ==========================================

        internal static void Install()
        {
            // 只有启用时才打补丁：插件装了但没开，核心行为应当一个字节都不变。
            if (IsActive) ApplyPatches();
        }

        internal static void ApplyPatches()
        {
            if (_patched) return;
            try
            {
                MethodInfo postfix = typeof(ConnectionPasswordGate)
                    .GetMethod(nameof(ServerInfoPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo prefix = typeof(ConnectionPasswordGate)
                    .GetMethod(nameof(AcceptPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null || prefix == null)
                {
                    Log.Error("[新进服密码] 找不到补丁方法本身（这不正常），本插件不生效。");
                    return;
                }

                var harmony = new Harmony(HarmonyId);

                // ① 让客户端知道"这个服要密码"，否则没有输入框
                Type infoType = AccessTools.TypeByName("ServerInfoPackage")
                                ?? AccessTools.TypeByName("Game.NetWork.Packages.ServerInfoPackage");
                ConstructorInfo ctor = infoType?.GetConstructor(new[] { typeof(bool) });
                if (ctor == null)
                {
                    Log.Warning("[新进服密码] 找不到 ServerInfoPackage(bool) 构造器："
                                + "客户端不会弹密码框，本插件不生效。");
                }
                else
                {
                    harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));
                }

                // ② 真正校验的地方
                Type reqType = AccessTools.TypeByName("ConnectionRequestPackage")
                               ?? AccessTools.TypeByName("Game.NetWork.Packages.ConnectionRequestPackage");
                MethodInfo accept = reqType?.GetMethod("AcceptAclient",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (accept == null)
                {
                    Log.Warning("[新进服密码] 找不到 ConnectionRequestPackage.AcceptAclient："
                                + "不校验密码，本插件不生效。");
                }
                else
                {
                    harmony.Patch(accept, prefix: new HarmonyMethod(prefix));
                }

                _patched = true;
                Log.Information("[新进服密码] 已接管核心：ServerInfoPackage（要密码）＋ AcceptAclient（校验密码）");
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] 接管核心失败：" + ex);
            }
        }

        /// <summary>
        /// 把 <c>m_needPasswd</c> 覆写成 true。核心原本是
        /// <c>m_needPasswd = !string.IsNullOrEmpty(WorldSettings.Password)</c>，
        /// 而我们不打算动那个世界设置（它会进存档、还会被别的界面读到），所以只改这一个 bool。
        /// </summary>
        private static void ServerInfoPostfix(object __instance)
        {
            try
            {
                if (!IsActive) return;
                FieldInfo field = AccessTools.Field(__instance.GetType(), "m_needPasswd");
                if (field == null)
                {
                    LogOnce("[新进服密码] 找不到 ServerInfoPackage.m_needPasswd，客户端不会弹密码框。");
                    return;
                }
                field.SetValue(__instance, true);
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] 设置 m_needPasswd 失败：" + ex.Message);
            }
        }

        private static void LogOnce(string message)
        {
            if (_fieldWarned) return;
            _fieldWarned = true;
            Log.Warning(message);
        }

        /// <summary>
        /// 进服校验。
        ///
        /// <para>
        /// 判据（自上而下）：
        /// 1. 设过个人密码 ⇒ 必须匹配个人密码；
        /// 2. 没设个人密码 且 默认密码为空 ⇒ <b>直接放行</b>（可选个人保护模式）；
        /// 3. 没设个人密码 且 默认密码非空 ⇒ 必须匹配默认密码（全员门槛模式）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>失败时返回 true 而不是 false</b>——prefix 返回 false 会让 Harmony
        /// 跳过 <c>AcceptAclient</c> 的整个方法体，而"把 connectionError 发给客户端"
        /// 那一步恰恰就在那个方法体里。返回 false 等于既不拒绝也不接受，
        /// 客户端会一直卡在《连接中》直到超时，玩家根本看不到《密码错误》。
        /// 正确做法是：把文案写进 <c>connectionError</c> 再交回核心，由核心原有流程发拒绝包。
        /// </para>
        /// </summary>
        private static bool AcceptPrefix(ConnectionRequestPackage __instance, NetNode netNode,
                                         StringBuilder connectionError, bool useExternalPassword)
        {
            try
            {
                if (!IsActive) return true;

                ConnectionPasswordConfig cfg = Cfg;
                if (cfg == null) return true;

                // SCKey 模式的身份是服务端向官方验过的 m_SCKeyGuid；游客模式是客户端自报的
                // m_guestGuid（游客 GUID 本来就能自选，这里只能按核心给的那个来，不额外收紧）。
                Guid guid = __instance.m_authMode == ConnectionRequestPackage.AuthModeType.SCKey
                    ? __instance.m_SCKeyGuid
                    : __instance.m_guestGuid;

                if (guid == Guid.Empty)
                {
                    // 身份都拿不到就不拦：这一条核心自己会拒（"非法请求：客户端提供的身份标识不完整"），
                    // 我们没必要重复报错，否则反而可能把它原本清楚的提示盖掉。
                    return true;
                }

                string key = guid.ToString("N");
                string input = __instance.m_password ?? string.Empty;

                ConnectionPasswordStore.PasswordEntry entry = ConnectionPasswordStore.Find(guid);
                bool hasPersonal = entry != null;

                bool ok;
                if (hasPersonal)
                {
                    ok = ConnectionPasswordStore.Verify(entry, input);
                }
                else if (string.IsNullOrEmpty(cfg.DefaultPassword))
                {
                    // 可选个人保护模式：没设个人密码的人直接放行。
                    ok = true;
                }
                else
                {
                    // 全员门槛模式：用全服默认密码进门。
                    ok = ConnectionPasswordStore.VerifyDefault(cfg.DefaultPassword, input);
                }

                // ⚠️ IP 必须在成功/失败分支之前取好：成功时要清 IP 维度的失败计数。
                string ip = TryGetIp(__instance);

                if (ok)
                {
                    lock (_sync)
                    {
                        _failures.Remove(key);
                        _failuresByIp.Remove(IpKey(ip));
                        _failureAt.Remove(key);
                        _passed++;
                    }
                    if (hasPersonal)
                    {
                        Log.Information($"[新进服密码] {guid} 用个人密码通过");
                    }
                    return true;
                }

                // 两个维度（GUID / IP）分别计数
                int failures;
                int ipFailures;
                lock (_sync)
                {
                    _rejected++;
                    failures = IncrementFailure(key);
                    ipFailures = IncrementFailure(IpKey(ip));
                }

                connectionError.AppendLine("密码错误");

                Log.Warning($"[新进服密码] 拒绝 {guid}（{ip}）："
                            + (hasPersonal ? "个人密码不匹配" : "默认密码不匹配")
                            + $"，本设备累计失败 {failures} 次、该IP累计 {ipFailures} 次");

                // 任一维度超限即踢
                bool overGuid = cfg.MaxFailuresBeforeKick > 0 && failures >= cfg.MaxFailuresBeforeKick;
                bool overIp = cfg.MaxFailuresBeforeIp > 0 && ipFailures >= cfg.MaxFailuresBeforeIp;
                if (overGuid || overIp)
                {
                    string why = overGuid && overIp ? "本设备与该IP均超限"
                        : overGuid ? "本设备超限" : "该IP超限";
                    Log.Warning($"[新进服密码] {ip} 触发断线（{why}：设备 {failures}/{cfg.MaxFailuresBeforeKick}，"
                                + $"IP {ipFailures}/{cfg.MaxFailuresBeforeIp}）");
                    // 连接已经被我们断掉了，不必再让核心补发一个拒绝包（那会变成两次响应）
                    TryKick(netNode, key, ip, __instance);
                    return false;
                }

                // ⚠️ 这里必须返回 true（理由见方法注释）：把文案写进 connectionError 后交回核心。
                return true;
            }
            catch (Exception ex)
            {
                // 校验器自己炸了 ⇒ **放行**。理由：这是安全增强不是安全底线，放行等于退回核心原生行为
                // （没密码保护），而在这里抛异常把所有人挡在门外是更糟的结果。
                Log.Error("[新进服密码] 校验异常，本次放行（退回无密码保护状态）：" + ex);
                return true;
            }
        }

        private static string TryGetIp(ConnectionRequestPackage pkg)
        {
            try { return pkg.From?.Request?.RemoteEndPoint?.Address?.ToString() ?? "未知IP"; }
            catch { return "未知IP"; }
        }

        /// <summary>
        /// 连错超限就断开**这一条**连接。
        ///
        /// <para>
        /// ⚠️⚠️ <b>绝对不要用 <c>netNode.Stop(...)</c></b>（基础插件实测踩过大坑）：
        /// <c>Stop()</c> 只是设 <c>isStopping=true</c>，随后 <c>StopImmediate()</c> 在
        /// <c>WorkType==Server</c> 分支里会 <c>RemoveAllClients("服务器主动关闭")</c> +
        /// <c>netManager.Stop()</c>——那是<b>关掉整个游戏服务器</b>，不是踢某个人，
        /// 而且<b>不会自动恢复</b>（当时全服 22 小时进不去，进程还活着、日志还在自动存档，极难凭直觉发现）。
        /// </para>
        ///
        /// <para>
        /// 正确做法照抄核心自己拒绝连接的那一套：
        /// <c>netNode.SendWriterFromPackage(new ConnectionRejectPackage(文案), pkg.From.Request, reject: true)</c>
        /// —— <c>reject:true</c> 内部只做 <c>request.Reject()</c>，关的是<b>这一条</b>连接，
        /// 监听端口和其他在线玩家完全不受影响。
        /// </para>
        ///
        /// <para>
        /// 为什么不用 <c>CommonLib.Net.RemoveClient</c>：那需要已构造好的 <c>Client</c> 对象，
        /// 而本方法跑在 <c>AcceptAclient</c> 的 prefix 里，<c>CreateClient</c> 还没执行，
        /// 此时拿不到 Client，只能拿到 <c>From.Request</c>。
        /// </para>
        /// </summary>
        private static void TryKick(NetNode netNode, string key, string ip, ConnectionRequestPackage pkg)
        {
            try
            {
                lock (_sync)
                {
                    _failures.Remove(key);
                    _failureAt.Remove(key);
                    // IP 维度**故意不清**：来源 IP 还在撞就应该接着限。
                    // 真正"洗白"的动作是成功通过一次（见 AcceptPrefix 的 ok 分支）。
                }

                ConnectionRequest request = null;
                try { request = pkg?.From?.Request; } catch { /* 取不到就降级 */ }

                if (netNode != null && request != null)
                {
                    netNode.SendWriterFromPackage(
                        new ConnectionRejectPackage($"密码连续错误，断线：{ip}"),
                        request, reject: true);
                    Log.Warning($"[新进服密码] 已断开该连接（仅此一条）：{ip}");
                    return;
                }

                // 拿不到 request 时**宁可不踢，也绝不整服停机**：只清计数 + 记日志。
                // 计数已清 ⇒ 他下次连接重新计 5 次，不会一上来就被踢。
                Log.Warning($"[新进服密码] {ip} 连错超限，但未取到连接请求对象，已跳过踢人"
                            + "（绝不用 netNode.Stop，那会关掉整个服务器）。");
            }
            catch (Exception ex)
            {
                Log.Warning("[新进服密码] 断线失败：" + ex.Message);
            }
        }

        /// <summary>IP 维度的字典 key。加前缀避免和 GUID(N 格式) 撞键。</summary>
        private static string IpKey(string ip) => "ip:" + (string.IsNullOrEmpty(ip) ? "未知IP" : ip);

        /// <summary>
        /// 累加失败次数，<b>带 TTL 衰减</b>：距上次失败超过 <see cref="FailureTtlSeconds"/>
        /// 就当重新开始（返回 1）。没衰减的话，一个人三年前输错一次，
        /// 今天连输两次就会被踢——那不是防爆破，那是记仇。
        /// </summary>
        private static int IncrementFailure(string key)
        {
            // 用 Time.RealTime：单调递增、不受系统改时间影响。
            double now = Time.RealTime;
            bool isIp = key.StartsWith("ip:", StringComparison.Ordinal);

            if (_failureAt.TryGetValue(key, out double last) && now - last <= FailureTtlSeconds)
            {
                // 还在"连错窗口"内，继续累加
            }
            else
            {
                // 窗口已过（或第一次）：对应字典从零开始
                if (isIp) _failuresByIp.Remove(key); else _failures.Remove(key);
            }
            _failureAt[key] = now;

            int n;
            if (isIp)
            {
                _failuresByIp.TryGetValue(key, out n);
                n++;
                _failuresByIp[key] = n;
                if (_failuresByIp.Count > 4096) { _failuresByIp.Clear(); _failureAt.Clear(); }
            }
            else
            {
                _failures.TryGetValue(key, out n);
                n++;
                _failures[key] = n;
                // 别让它无限涨：同一个 GUID 换 IP 换端口重连，字典不会自己回收
                if (_failures.Count > 4096) { _failures.Clear(); _failureAt.Clear(); }
            }
            return n;
        }

        // ==========================================
        // 统计
        // ==========================================

        internal static bool DueForReport()
        {
            if (Time.RealTime < _reportAt) return false;
            _reportAt = Time.RealTime + 300.0;
            return true;
        }

        internal static void ReportStats()
        {
            lock (_sync)
            {
                if (_passed == 0 && _rejected == 0) return;
                Log.Information($"[新进服密码] 统计：放行 {_passed} 次、拒绝 {_rejected} 次（近 5 分钟）");
                _passed = 0;
                _rejected = 0;
            }
        }

        /// <summary>供 <c>/pwd status</c> 读的累计拦截数（不清零）。</summary>
        internal static int RejectedSinceBoot
        {
            get { lock (_sync) { return _rejected; } }
        }
    }
}
