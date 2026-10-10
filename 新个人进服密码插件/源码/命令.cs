using System;
using System.Text;
using Engine;
using Game;
using Game.Server;

namespace NewPersonalCode
{
    /// <summary>
    /// <c>/pwd</c>（别名 <c>/password</c>）—— 进服密码自助命令。
    ///
    /// 用法：
    ///   <c>/pwd</c>                    看状态：有没有个人密码、下次该用哪个口径进门
    ///   <c>/pwd set ＜密码＞ ＜确认密码＞</c>  设置 / 修改自己的个人密码
    ///   <c>/pwd clear</c>              删掉个人密码
    ///   <c>/pwd default ＜密码＞</c>     <b>服主</b>：设全服默认密码
    ///   <c>/pwd status</c>              <b>服主</b>：看有多少人设了个人密码
    ///   <c>/pwd ＜密码＞</c>            拿一串东西测一下是不是自己的密码
    ///
    /// AuthLevel = 0：所有人都要能设置自己的密码，所以不能有任何权限门槛。
    /// 服主专属的两条在 <see cref="Run"/> 里用 <see cref="AdminGate.IsVerifiedAdmin"/> 单独判。
    /// </summary>
    public sealed class PwdCmd : AbstractProcessCmd
    {
        public override string Cmd => "pwd";

        public override string Introduce =>
            "/pwd [set ＜密码＞ ＜确认密码＞ | clear | default ＜密码＞ | status] - 进服密码（防 token 被盗用）；只打 /pwd 看状态";

        public override int AuthLevel => 0;

        public override DisplayType Display => DisplayType.All;

        public override void ProcessCmd()
        {
            if (m_isTerminal)
            {
                SendMessage(Cmd, "控制台不需要设进服密码。");
                return;
            }

            PlayerData pd = m_client?.PlayerData;
            if (pd == null)
            {
                SendMessage(Cmd, "无法识别你的身份。");
                return;
            }

            if (!ConnectionPasswordGate.IsActive)
            {
                SendMessage(Cmd, "<c=orange>本服没有开启进服密码</c>，这条命令暂时不用。");
                return;
            }

            string sub = m_messageDatas != null && m_messageDatas.Length >= 2
                ? (m_messageDatas[1] ?? string.Empty).ToLowerInvariant()
                : string.Empty;

            // 服主专属的两条：判据是 AdminGate.IsVerifiedAdmin
            // （不看 PlayerData.ServerManager 自称——客户端能在进服包里自称管理员）。
            if (sub == "default" || sub == "status")
            {
                if (!AdminGate.IsVerifiedAdmin(pd))
                {
                    SendMessage(Cmd, "<c=orange>这一条要服主身份。</c>"
                                     + (AdminGate.AdminPasswordConfigured
                                         ? "先 /pw ＜密码＞ 验证。"
                                         : "本插件未配置 AdminPassword 提权密码。"));
                    return;
                }
                if (sub == "default") { DoSetDefault(); return; }
                DoStatus();
                return;
            }

            switch (sub)
            {
                case "":
                    ShowSelfStatus(pd);
                    return;

                case "set":
                    DoSet(pd);
                    return;

                case "clear":
                    DoClear(pd);
                    return;

                default:
                    // 不认识子命令时，若只跟了一个词，就当是"拿它试一下密码"
                    if (m_messageDatas != null && m_messageDatas.Length == 2)
                    {
                        DoVerify(pd, m_messageDatas[1] ?? string.Empty);
                        return;
                    }
                    SendMessage(Cmd, "<c=orange>用法：</c>/pwd　看状态　　"
                                     + "<c=cyan>/pwd set ＜密码＞ ＜确认密码＞</c>　设密码");
                    return;
            }
        }

        /// <summary>只打 /pwd：告诉他现在是什么状态、下次进门该敲什么。</summary>
        private void ShowSelfStatus(PlayerData pd)
        {
            Guid guid = pd.PlayerGUID;
            bool personal = ConnectionPasswordStore.OwnScopeIsPersonal(guid);

            var sb = new StringBuilder();
            if (personal)
            {
                sb.Append("<c=green>你已经设了个人密码。</c>　下次进服时用你自己的密码。");
                sb.Append("\n改密码：<c=cyan>/pwd set 新密码 确认密码</c>　　"
                          + "删掉：<c=cyan>/pwd clear</c>");
            }
            else if (ConnectionPasswordStore.DefaultUsable())
            {
                sb.Append("<c=yellow>你还没有个人密码</c>，现在进服用的是全服默认密码。");
                sb.Append("\n强烈建议设一个自己的：<c=cyan>/pwd set ＜密码＞ ＜确认密码＞</c>");
            }
            else
            {
                sb.Append("<c=green>你不需要密码就能进服</c>（本服是可选保护模式）。");
                sb.Append("\n想给自己加一道锁的话：<c=cyan>/pwd set ＜密码＞ ＜确认密码＞</c>");
            }
            sb.Append("\n<c=gray>密码是在客户端的密码框里敲的，不会留在你的聊天记录里。</c>");
            SendMessage(Cmd, sb.ToString());
        }

        private void DoSet(PlayerData pd)
        {
            if (m_messageDatas == null || m_messageDatas.Length < 4)
            {
                SendMessage(Cmd, "<c=orange>要两个参数：</c>/pwd set ＜密码＞ ＜确认密码＞");
                return;
            }

            // 密码里不能有空格（客户端密码框只认一串连续字符），所以这里只取前两个词，
            // 多打的要当成没写——直接报错比默默截断更好。
            string password = m_messageDatas[2] ?? string.Empty;
            string confirm = m_messageDatas[3] ?? string.Empty;
            if (m_messageDatas.Length > 4)
            {
                SendMessage(Cmd, "<c=orange>参数太多了</c>（密码里不能有空格）。"
                                 + "格式：/pwd set ＜密码＞ ＜确认密码＞");
                return;
            }

            Guid guid = pd.PlayerGUID;
            bool had = ConnectionPasswordStore.OwnScopeIsPersonal(guid);
            string error = ConnectionPasswordStore.Set(guid, password, confirm);
            if (error != null)
            {
                SendMessage(Cmd, "<c=orange>" + Esc(error) + "</c>");
                return;
            }

            Log.Information($"[新进服密码] {pd.Name}（{guid}）" + (had ? "修改了" : "设置了") + "个人密码");
            SendMessage(Cmd, "<c=green>好了。</c>　"
                             + (had ? "新密码从下次进服开始生效。" : "下次进服时用你刚设的密码。"));
        }

        private void DoClear(PlayerData pd)
        {
            Guid guid = pd.PlayerGUID;
            if (!ConnectionPasswordStore.Clear(guid))
            {
                SendMessage(Cmd, "<c=orange>你本来就没有个人密码。</c>");
                return;
            }
            Log.Information($"[新进服密码] {pd.Name}（{guid}）删除了个人密码");
            SendMessage(Cmd, "<c=green>已删除。</c>　"
                             + (ConnectionPasswordStore.DefaultUsable()
                                 ? "以后进服用全服默认密码。"
                                 : "以后进服不需要密码了。"));
        }

        /// <summary>拿某串东西试一下是不是自己的密码（不是改密码，只回一句对不对）。</summary>
        private void DoVerify(PlayerData pd, string input)
        {
            if (ConnectionPasswordStore.CheckOwn(pd.PlayerGUID, input))
            {
                SendMessage(Cmd, "<c=green>密码正确。</c>");
                return;
            }
            SendMessage(Cmd, "<c=orange>不对。</c>　要改密码用 <c=cyan>/pwd set 新密码 确认密码</c>");
        }

        private void DoSetDefault()
        {
            if (m_messageDatas == null || m_messageDatas.Length < 3)
            {
                SendMessage(Cmd, "<c=orange>用法：</c>/pwd default ＜密码＞");
                return;
            }
            if (m_messageDatas.Length > 3)
            {
                SendMessage(Cmd, "<c=orange>默认密码里不能有空格</c>（客户端密码框只认一串连续字符）。");
                return;
            }

            ConnectionPasswordConfig cfg = NewPersonalCodePlugin.Instance?.Config;
            if (cfg == null) { SendMessage(Cmd, "配置异常。"); return; }

            string value = m_messageDatas[2] ?? string.Empty;
            if (value.Length < cfg.MinLength)
            {
                SendMessage(Cmd, $"<c=orange>至少 {cfg.MinLength} 个字。</c>");
                return;
            }
            if (value.Length > cfg.MaxLength)
            {
                SendMessage(Cmd, $"<c=orange>最多 {cfg.MaxLength} 个字</c>（客户端密码框就那么宽）。");
                return;
            }

            cfg.DefaultPassword = value;
            NewPersonalCodePlugin.Instance.SaveConfig();
            Log.Information("[新进服密码] 服主设置了新的全服默认密码（明文存配置，不写进聊天记录回显）");
            SendMessage(Cmd, "<c=green>全服默认密码已更新。</c>　已设个人密码的玩家不受影响（他们走自己那份）。");
        }

        private void DoStatus()
        {
            ConnectionPasswordConfig cfg = NewPersonalCodePlugin.Instance?.Config;
            var sb = new StringBuilder();
            sb.Append("进服密码：<c=green>已启用</c>");
            sb.Append("\n已设个人密码：<c=cyan>").Append(ConnectionPasswordStore.PersonalCount()).Append("</c> 人");
            sb.Append("\n默认密码：").Append(ConnectionPasswordStore.DefaultUsable()
                ? "<c=green>已设</c>（没设个人密码的人用它进门）"
                : "<c=green>未设</c>（没设个人密码的人直接放行 = 可选保护模式）");
            sb.Append("\n累计拦截：<c=cyan>").Append(ConnectionPasswordGate.RejectedSinceBoot).Append("</c> 次（本次运行）");
            sb.Append("\n存储文件：<c=cyan>").Append(Esc(ConnectionPasswordStore.DataPath)).Append("</c>");
            sb.Append("\n<c=gray>密码一律只存 PBKDF2 哈希，明文不落盘；这里也不回显任何密码。</c>");
            SendMessage(Cmd, sb.ToString());
        }

        /// <summary>富文本转义（路径之类会被当富文本解析的内容都过这里）。</summary>
        private static string Esc(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\\", "\\\\").Replace("<", "\\<").Replace(">", "\\>");
        }
    }

    /// <summary><c>/password</c>：<see cref="PwdCmd"/> 的别名，两个名字都能敲。</summary>
    public sealed class PasswordCmd : AbstractProcessCmd
    {
        public override string Cmd => "password";

        public override string Introduce =>
            "/password [set ＜密码＞ ＜确认密码＞ | clear | default ＜密码＞ | status] - /pwd 的别名";

        public override int AuthLevel => 0;

        public override DisplayType Display => DisplayType.All;

        public override void ProcessCmd()
        {
            // 直接委托给 /pwd 的实现，两条命令永远同一套行为，不会各自漂移
            var pwd = new PwdCmd();
            pwd.Process(username: m_username, netNode: m_netNode, client: m_client,
                        messageDatas: m_messageDatas, isTerminal: m_isTerminal,
                        commandOutput: m_commandOutput);
        }
    }
}
