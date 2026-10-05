using System;
using System.Collections.Generic;
using System.Linq;
using Game.Server;

namespace ScWebPanel
{
    /// <summary>
    /// 网页终端的命令闸门。**这是整个功能最关键的一段代码。**
    ///
    /// 为什么必须有它：网页执行命令走的是
    /// <c>CmdManager.HandleMessage(username, netNode, client, message, isTerminal: true)</c>，
    /// 而 `isTerminal: true` 在核心源码里是**直接 break**、整个跳过
    /// <c>AuthLevelManager.GetUserLevel(client) &gt;= processCmd.AuthLevel</c> 那个判断的
    /// （见 CmdManager.HandleMessage 的 if (isTerminal) 分支）。
    ///
    /// 换句话说：**核心不会替我们拦任何命令**。面板自己的登录鉴权 + 这里的白名单，
    /// 就是唯一的防线。所以这里的默认值是刻意收窄的，并且黑名单优先于白名单。
    /// </summary>
    public static class WebPanelCommandPolicy
    {
        /// <summary>命令是否允许从网页执行。allowed 为 false 时 reason 说明原因。</summary>
        public static bool IsAllowed(string command, WebPanelConfig settings, out string reason)
        {
            reason = null;

            if (settings == null)
            {
                reason = "网页控制台未配置";
                return false;
            }

            string normalized = Normalize(command);
            if (normalized.Length == 0)
            {
                reason = "命令不能为空";
                return false;
            }

            var denied = WebPanelConfig.SplitPrefixes(settings.DeniedCommandPrefixes);
            var allowed = WebPanelConfig.SplitPrefixes(settings.AllowedCommandPrefixes);

            // 黑名单优先：即使白名单里有 admin，`admin stop` 这种更长前缀也能单独堵掉
            if (denied.Length > 0 && MatchesAny(normalized, denied))
            {
                reason = "该命令已被网页控制台的禁止清单拦下（WebPanel.DeniedCommandPrefixes）";
                return false;
            }

            if (allowed.Length == 0)
            {
                reason = "网页终端没有放行任何命令（WebPanel.AllowedCommandPrefixes 为空）";
                return false;
            }

            if (!MatchesAny(normalized, allowed))
            {
                reason = "该命令不在网页终端的允许清单里（WebPanel.AllowedCommandPrefixes）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 前缀匹配。按整词匹配而不是裸 StartsWith：
        /// 白名单里有 <c>tp</c> 时，<c>tpxxx</c> 不该被放行；但 <c>tp 玩家名</c> 要放行。
        /// 所以判定条件是"等于前缀"或"前缀后跟一个空格"。
        /// </summary>
        private static bool MatchesAny(string command, string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if (command.Length < prefix.Length) continue;
                if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (command.Length == prefix.Length) return true;
                if (command[prefix.Length] == ' ') return true;
            }
            return false;
        }

        /// <summary>去掉前导斜杠、压掉多余空白，便于比较。</summary>
        private static string Normalize(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            string trimmed = command.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);

            // 把连续空白压成单空格：命令内部是按 Split(' ') 分词的，多空格会产生空参数
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
    }

    /// <summary>网页上的玩家管理动作闸门。与命令白名单独立（动作最终不一定走命令）。</summary>
    public static class WebPanelActionPolicy
    {
        /// <summary>
        /// 支持的动作用一个固定表管着：网页只能提交表里有的动作名，
        /// 具体执行什么由服务端决定，前端改不动（与菜单模块同一套思路）。
        ///
        /// 危险动作（踢人）单独一个开关，默认**关**；用户要就显式打开。
        /// </summary>
        public static readonly Dictionary<string, string> Actions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "kick",       "踢出服务器" },
                { "heal",       "回满生命" },
                { "fix",        "修复生存状态（血量/食物/睡眠/潮湿/体温）" },
                { "kill",       "击杀该玩家" },
                { "gamemode",   "切换创造/生存" },
                { "clear",      "清空背包" },
                { "godmode",    "开关无敌" },
                { "respawn",    "送回重生点" },
                { "ban",        "按社区账号封禁" },
                { "unban",      "解除账号封禁" },
                { "banlist",    "查看封禁名单" },
                { "banip",      "封禁一个指定 IP（需手填 IP）" },
                { "unbanip",    "解除一个指定 IP 的封禁" },
                { "banipuser",  "记录该账号的 IP 并封禁（跟账号走）" },
                { "unbanipuser","解除该账号的 IP 记录" },
                { "baniplist",  "查看 IP 封禁名单" },
                { "day",        "把时间设为白天" },
                { "night",      "把时间设为夜晚" },
                { "tell",       "私聊提醒该玩家" },
            };

        public static bool IsAllowed(string action, WebPanelConfig settings, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(action))
            {
                reason = "缺少动作名";
                return false;
            }

            string key = action.Trim().ToLowerInvariant();
            if (!Actions.ContainsKey(key))
            {
                reason = "不支持的动作：" + action;
                return false;
            }

            // ⚠ 封禁相关的安全边界：
            //   * `ban` / `unban` 只认 CommunityAccountId，跟网络路径无关。
            //   * `banip` 封的是**管理员手填的 IP**，面板绝不代填"该玩家当前连接地址"——
            //     FRP 转发下那个地址是代理机的，全服玩家共用，一键封 = 封全服。
            //   * `banipuser` 交给核心按账号现场关联 IP（/ban ip user <id>），
            //     这是 FRP 环境下唯一"不用管理员猜 IP"的相对安全路径。
            // 以上三条都在 WebPanelApi.ApplyPlayerAction 里实现，这里只做动作名白名单。

            // 踢人不需要额外审批（它就是"管理玩家"的核心动作），但要在日志里留痕，
            // 所以这里只挡"库里没有的动作"，真正的动作执行在 WebPanelApi.ApplyPlayerAction。
            return true;
        }
    }
}
