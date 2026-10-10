using System;
using System.Collections.Generic;
using Engine;
using Game;
using Game.Server;

namespace NewPersonalCode
{
    /// <summary>
    /// 轻量"服主身份"闸门。只服务于本插件自己的两条提权命令
    /// （<c>/pwd default</c>、<c>/pwd status</c>）。
    ///
    /// <para>
    /// <b>为什么不看 <c>PlayerData.ServerManager</c></b>：客户端可以在进服的
    /// <c>PlayerDataPackage</c> 里<b>自称管理员</b>，也可能被外挂通过自定义包把它写成 true
    /// （基础插件 2026-10-07 取证实锤）。唯一客户端碰不到的秘密就是密码。
    /// </para>
    ///
    /// <para>
    /// <b>但这不是一套完整的权限系统</b>：它只管本插件自己的两条命令，
    /// 不会去保护 <c>/tp</c>、<c>/give</c> 之类的其他命令。要做全服权限门，
    /// 那是基础插件 <c>AdminAuthModule</c> 的活。本插件刻意做小：
    /// 密码插件不该顺手接管全服的权限判定，那会让两个插件互相打架。
    /// </para>
    ///
    /// <para>
    /// 未配 <see cref="ConnectionPasswordConfig.AdminPassword"/> 时该闸门关闭，
    /// 退回"看 ServerManager"的旧信任模型——单机服 / 自建服不需要关心它。
    /// </para>
    /// </summary>
    internal static class AdminGate
    {
        private static readonly object _sync = new object();

        /// <summary>GUID → 验证截止时间（游戏内秒）。</summary>
        private static readonly Dictionary<Guid, double> _authedUntil = new Dictionary<Guid, double>();

        /// <summary>是否配置了提权密码（配置了闸门才真正生效）。</summary>
        internal static bool AdminPasswordConfigured
        {
            get
            {
                ConnectionPasswordConfig cfg = NewPersonalCodePlugin.Instance?.Config;
                return cfg != null && !string.IsNullOrWhiteSpace(cfg.AdminPassword);
            }
        }

        /// <summary>
        /// 是否"可信服主"。配置了提权密码 ⇒ 必须已通过 <c>/pw</c>；没配 ⇒ 退回 ServerManager。
        /// </summary>
        internal static bool IsVerifiedAdmin(PlayerData pd)
        {
            if (pd == null) return false;

            if (!AdminPasswordConfigured)
            {
                // 闸门未启用：退回旧信任模型（服主显式不配密码时的选择）
                return pd.ServerManager;
            }

            lock (_sync)
            {
                if (_authedUntil.TryGetValue(pd.PlayerGUID, out double until) && Time.RealTime < until)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>校验一次提权密码。</summary>
        internal static bool Verify(PlayerData pd, string input)
        {
            if (pd == null) return false;
            ConnectionPasswordConfig cfg = NewPersonalCodePlugin.Instance?.Config;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.AdminPassword)) return false;

            if (!ConnectionPasswordStore.VerifyDefault(cfg.AdminPassword, input)) return false;

            double minutes = cfg.AdminSessionMinutes;
            lock (_sync)
            {
                // 0 = 本次连接内一直有效（给一个很长的窗口，断线重连后 GUID 相同也仍算，
                // 这正是用户显式选择 0 时的语义）
                _authedUntil[pd.PlayerGUID] = minutes <= 0
                    ? Time.RealTime + 3650.0 * 86400.0
                    : Time.RealTime + minutes * 60.0;
            }
            return true;
        }

        /// <summary>撤销验证（<c>/pw clear</c>）。</summary>
        internal static void Revoke(PlayerData pd)
        {
            if (pd == null) return;
            lock (_sync) { _authedUntil.Remove(pd.PlayerGUID); }
        }

        /// <summary>剩余有效秒数；未验证返回 0。</summary>
        internal static double RemainingSeconds(PlayerData pd)
        {
            if (pd == null) return 0;
            lock (_sync)
            {
                if (_authedUntil.TryGetValue(pd.PlayerGUID, out double until) && Time.RealTime < until)
                {
                    return until - Time.RealTime;
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// <c>/pw</c> —— 提权验证命令，只对本插件的管理员命令生效。
    ///
    /// 用法：<c>/pw ＜密码＞</c> 验证；<c>/pw clear</c> 撤销；只打 <c>/pw</c> 看状态。
    ///
    /// 注意：密码会出现在**玩家自己**的聊天行里（客户端回显命令，服务端无法阻止），
    /// 所以别用别处在用的密码。
    /// </summary>
    public sealed class AdminPwCmd : AbstractProcessCmd
    {
        public override string Cmd => "pw";

        public override string Introduce =>
            "/pw ＜密码＞ - 验证服主身份（只对本插件的 /pwd default·status 生效）；/pw clear 撤销；只打 /pw 看状态";

        public override int AuthLevel => 0;

        public override DisplayType Display => DisplayType.All;

        public override void ProcessCmd()
        {
            if (!AdminGate.AdminPasswordConfigured)
            {
                SendMessage(Cmd, "本插件没有配置提权密码（AdminPassword），/pw 暂时不用。");
                return;
            }

            PlayerData pd = m_isTerminal ? null : m_client?.PlayerData;
            if (pd == null)
            {
                SendMessage(Cmd, "控制台不需要 /pw（控制台本来就以服务端身份运行）。");
                return;
            }

            string arg = m_messageDatas != null && m_messageDatas.Length >= 2
                ? (m_messageDatas[1] ?? string.Empty)
                : string.Empty;

            if (string.IsNullOrEmpty(arg))
            {
                double left = AdminGate.RemainingSeconds(pd);
                if (left > 0)
                {
                    double mins = left / 60.0;
                    SendMessage(Cmd, $"<c=green>已验证。</c> 剩余约 {mins:0} 分钟。用 /pw clear 可撤销。");
                }
                else
                {
                    SendMessage(Cmd, "用 <c=cyan>/pw ＜密码＞</c> 验证；验证后可用 <c=cyan>/pwd default</c> "
                                     + "与 <c=cyan>/pwd status</c>。");
                }
                return;
            }

            if (string.Equals(arg, "clear", StringComparison.OrdinalIgnoreCase))
            {
                AdminGate.Revoke(pd);
                SendMessage(Cmd, "<c=green>已撤销验证。</c>");
                return;
            }

            if (AdminGate.Verify(pd, arg))
            {
                SendMessage(Cmd, "<c=green>验证通过。</c>　现在可以用 /pwd default 与 /pwd status。");
                Log.Information($"[新进服密码] {pd.Name}（{pd.PlayerGUID}）通过了服主验证");
            }
            else
            {
                SendMessage(Cmd, "<c=red>密码不对。</c>");
                Log.Warning($"[新进服密码] {pd.Name} 服主验证失败");
            }
        }
    }
}
