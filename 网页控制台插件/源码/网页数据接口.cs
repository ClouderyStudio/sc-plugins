using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using Game.Server;
using Game.Server.Plugins;

namespace ScWebPanel
{
    /// <summary>
    /// 网页控制台的数据接口层。**所有方法都只能在主线程调用**（由 <see cref="WebPanelPlugin"/>
    /// 把请求排进主线程执行），因为它们都要读 Project / Subsystem / 玩家实体。
    ///
    /// 关于几个"参考图里有、但本服拿不到"的字段，这里统一降级处理，并在返回值里带上说明，
    /// 免得前端以为是 bug：
    /// - **延迟**：核心不暴露玩家 ping，客户端也不上报。只有 LiteNetLib 的 <c>Peer.Ping</c> 能拿到，
    ///   而且不是所有连接都有值 ⇒ 拿不到就返回 null，前端显示"—"。
    /// - **维度**：这个版本的 Survivalcraft 是单世界，没有多维度概念 ⇒ 用世界名代替。
    /// - **生命值**：`ComponentHealth.Health` 是 0~1 的归一化值，**没有最大值字段**。
    ///   为了让界面符合直觉（参考图显示 20/20），这里按 20 折算显示。
    /// </summary>
    public static class WebPanelApi
    {
        /// <summary>血量折算基数。核心是 0~1，界面上按 20 显示更符合玩家的习惯。</summary>
        private const float HealthDisplayScale = 20f;

        // ==========================================
        // 总览
        // ==========================================

        public static string BuildOverview(WebPanelMetrics metrics, DateTime startedUtc)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"success\":true");
            sb.Append(",\"serverTime\":").Append(WebPanelJson.Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(",\"metrics\":").Append(metrics?.ToJson() ?? "{}");

            try
            {
                var project = GameManager.Project;
                if (project == null)
                {
                    sb.Append(",\"loaded\":false");
                    sb.Append(",\"message\":").Append(WebPanelJson.Quote("世界尚未加载"));
                    sb.Append('}');
                    return sb.ToString();
                }

                var gameInfo = project.FindSubsystem<SubsystemGameInfo>();
                var players = project.FindSubsystem<SubsystemPlayers>();
                var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>();
                var weather = project.FindSubsystem<SubsystemWeather>();

                sb.Append(",\"loaded\":true");

                // ---- 世界 ----
                sb.Append(",\"world\":{");
                sb.Append("\"name\":").Append(WebPanelJson.Quote(Safe(() => gameInfo?.WorldSettings?.Name) ?? "未知"));
                sb.Append(",\"seed\":").Append(SafeInt(() => gameInfo?.WorldSeed ?? 0));
                sb.Append(",\"gameMode\":").Append(WebPanelJson.Quote(Safe(() => gameInfo?.WorldSettings?.GameMode.ToString()) ?? "-"));
                sb.Append(",\"elapsedGameTime\":").Append(WebPanelJson.Quote(
                    WebPanelJson.FormatDuration(SafeDouble(() => gameInfo?.TotalElapsedGameTime ?? 0))));
                // 注意：SubsystemTimeOfDay.Day 是 double（不是 int），而 TimeOfDay 是 float
                sb.Append(",\"day\":").Append(SafeInt(() => (int)(timeOfDay?.Day ?? 0)));
                sb.Append(",\"timeOfDay\":").Append(SafeDouble(() => timeOfDay?.TimeOfDay ?? 0));
                sb.Append(",\"dayPhase\":").Append(WebPanelJson.Quote(DescribeDayPhase(SafeDouble(() => timeOfDay?.TimeOfDay ?? 0))));
                sb.Append(",\"precipitation\":").Append(SafeDouble(() => weather?.PrecipitationIntensity ?? 0));
                sb.Append(",\"fog\":").Append(SafeDouble(() => weather?.FogIntensity ?? 0));
                sb.Append(",\"weather\":").Append(WebPanelJson.Quote(DescribeWeather(weather)));
                sb.Append('}');

                // ---- 玩家 ----
                // WorldSettings 里叫 MaxOnlinePlayerCount（ushort），没有 MaxOnlinePlayers 这个成员
                int maxPlayers = SafeInt(() => project.FindSubsystem<SubsystemGameInfo>()?.WorldSettings?.MaxOnlinePlayerCount ?? 0);
                sb.Append(",\"players\":{");
                // ComponentPlayers 是 struct（ReadOnlyList<T>），不能用 ?. 取成员，必须先判空再解引用
                sb.Append("\"online\":").Append(players != null ? players.ComponentPlayers.Count : 0);
                sb.Append(",\"max\":").Append(maxPlayers);
                sb.Append('}');

                // ---- 实体 ----
                int bodies = SafeInt(() =>
                {
                    var subsystem = project.FindSubsystem<SubsystemBodies>();
                    return subsystem?.m_componentBodies?.Count ?? 0;
                });
                int pickables = SafeInt(() =>
                {
                    var subsystem = project.FindSubsystem<SubsystemPickables>();
                    return subsystem?.m_pickables?.Count ?? 0;
                });
                sb.Append(",\"entities\":{");
                sb.Append("\"bodies\":").Append(bodies);
                sb.Append(",\"items\":").Append(pickables);
                sb.Append(",\"total\":").Append(bodies + pickables);
                sb.Append('}');
            }
            catch (Exception ex)
            {
                sb.Append(",\"loaded\":false");
                sb.Append(",\"message\":").Append(WebPanelJson.Quote("读取世界状态失败：" + ex.Message));
            }

            // ---- 进程自身指标（不依赖游戏对象）----
            sb.Append(",\"process\":").Append(BuildProcessJson(startedUtc));
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildProcessJson(DateTime startedUtc)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            try
            {
                var process = Process.GetCurrentProcess();
                sb.Append("\"memoryMb\":").Append(Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1));
                sb.Append(",\"privateMb\":").Append(Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024.0, 1));
                sb.Append(",\"threads\":").Append(process.Threads.Count);
                sb.Append(",\"cpuSeconds\":").Append(Math.Round(process.TotalProcessorTime.TotalSeconds, 1));
                sb.Append(",\"startedAt\":").Append(WebPanelJson.Quote(startedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                sb.Append(",\"uptime\":").Append(WebPanelJson.Quote(WebPanelJson.FormatDuration((DateTime.UtcNow - startedUtc).TotalSeconds)));
                sb.Append(",\"machine\":").Append(WebPanelJson.Quote(Environment.MachineName));
                sb.Append(",\"cores\":").Append(Environment.ProcessorCount);
                sb.Append(",\"runtime\":").Append(WebPanelJson.Quote(Environment.Version.ToString()));
            }
            catch
            {
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ==========================================
        // 玩家列表
        // ==========================================

        public static string BuildPlayers()
        {
            var sb = new StringBuilder(2048);
            sb.Append("{\"success\":true,\"players\":[");

            try
            {
                var project = GameManager.Project;
                var subsystem = project?.FindSubsystem<SubsystemPlayers>();

                // ComponentPlayers 是 ReadOnlyList<ComponentPlayer>（值类型），
                // 不能对它用 ?.，也拿不到 Nullable 的 Count/索引器 —— 先判空子系统的引用再取值。
                if (subsystem != null)
                {
                    var componentPlayers = subsystem.ComponentPlayers;
                    bool first = true;
                    for (int i = 0; i < componentPlayers.Count; i++)
                    {
                        var player = componentPlayers[i];
                        if (player == null) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        AppendPlayer(sb, player);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("],\"error\":").Append(WebPanelJson.Quote(ex.Message));
                return sb.Append('}').ToString();
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendPlayer(StringBuilder sb, ComponentPlayer player)
        {
            var data = player.PlayerData;
            var health = player.ComponentHealth;

            sb.Append('{');
            sb.Append("\"name\":").Append(WebPanelJson.Quote(data?.Name ?? "?"));
            sb.Append(",\"guid\":").Append(WebPanelJson.Quote((data?.PlayerGUID ?? Guid.Empty).ToString()));

            // 社区账号 id：这是**唯一可靠**的封禁标识。FRP 转发下所有玩家外面看到的是同一个
            // 代理 IP，只有 CommunityAccountId 仍能唯一区分人。核心的 /ban add 用的就是它。
            string accountId = Safe(() => data?.Client?.CommunityAccountId) ?? "-1";
            sb.Append(",\"accountId\":").Append(WebPanelJson.Quote(accountId));
            sb.Append(",\"isBanned\":").Append(
                SafeBool(() => Game.Server.Plugins.BanUserPlugin.IsBan(accountId, null)) ? "true" : "false");

            // 连接地址：Peer.Address 是**服务端实际看到的**对端地址。
            // ⚠️ 走 FRP 转发时这里拿到的是代理机 IP（全服同一个），不是玩家真实公网 IP。
            // 所以界面必须把这一点标出来，不能让管理员误以为这是玩家自己的 IP。
            string peerIp = Safe(() => data?.Client?.Peer?.Address?.ToString()) ?? string.Empty;
            sb.Append(",\"peerIp\":").Append(WebPanelJson.Quote(peerIp));
            sb.Append(",\"peerIpBanned\":").Append(
                (!string.IsNullOrEmpty(peerIp) &&
                 SafeBool(() => Game.Server.Plugins.BanUserPlugin.IsBanIp(peerIp))) ? "true" : "false");

            // 血量：核心是 0~1，按 20 折算给界面
            float ratio = SafeFloat(() => health?.Health ?? 0f);
            sb.Append(",\"health\":").Append(Math.Round(ratio * HealthDisplayScale, 1));
            sb.Append(",\"healthMax\":").Append(HealthDisplayScale);
            sb.Append(",\"healthRatio\":").Append(Math.Round(ratio, 3));
            sb.Append(",\"invulnerable\":").Append(SafeBool(() => health?.IsInvulnerable ?? false) ? "true" : "false");

            // 位置：ComponentPlayer 本身没有 Position，位置在 ComponentCreature.ComponentBody 上。
            // Vector3 是值类型，进不了 Safe<T>（它约束 T : class），所以这里直接判组件引用。
            var body = player.ComponentBody;
            if (body != null)
            {
                Vector3 position = body.Position;
                sb.Append(",\"x\":").Append(Terrain.ToCell(position.X));
                sb.Append(",\"y\":").Append(Terrain.ToCell(position.Y));
                sb.Append(",\"z\":").Append(Terrain.ToCell(position.Z));
            }
            else
            {
                sb.Append(",\"x\":0,\"y\":0,\"z\":0");
            }

            // 等级 / 模式 / 管理员
            sb.Append(",\"level\":").Append(Math.Round(SafeDouble(() => data?.Level ?? 0), 1));
            // GetEffectiveGameMode 返回的是 GameMode 枚举（值类型），不能加 ?.
            sb.Append(",\"gameMode\":").Append(WebPanelJson.Quote(
                Safe(() => CommonLib.GetEffectiveGameMode(data).ToString()) ?? "-"));
            sb.Append(",\"isAdmin\":").Append(SafeBool(() => data?.ServerManager ?? false) ? "true" : "false");
            // "已二次验证的管理员"：本插件独立运行时拿不到基础插件的 /pw 验证状态
            // （那是基础插件 AdminAuthModule 的内存态），这里退化为"就是 ServerManager"，
            // 页面上的管理员标记与 isAdmin 同义。装了基础插件时可以把这里换回
            // Modules.AdminAuthModule.IsVerifiedAdmin(data)，换来更强的判定。
            sb.Append(",\"verifiedAdmin\":").Append(SafeBool(() => data?.ServerManager ?? false) ? "true" : "false");

            // 延迟：核心不暴露，拿不到就是 null
            int? ping = TryGetPing(data);
            sb.Append(",\"ping\":").Append(ping.HasValue ? ping.Value.ToString(CultureInfo.InvariantCulture) : "null");

            // 在线时长：Spawns 是游戏时间坐标，不是真实时间
            sb.Append(",\"spawnsCount\":").Append(SafeInt(() => data?.SpawnsCount ?? 0));
            sb.Append(",\"firstSpawnGameTime\":").Append(SafeDouble(() => data?.FirstSpawnTime ?? -1));
            sb.Append(",\"onlineGameTime\":").Append(WebPanelJson.Quote(DescribeOnlineTime(data)));

            // 背包摘要（前几格），列表页给个概览
            sb.Append(",\"inventoryCount\":").Append(SafeInt(() => CountInventoryItems(player)));

            sb.Append('}');
        }

        /// <summary>
        /// 玩家 ping。核心的 Client 上没有这个字段，只能摸 LiteNetLib 的 Peer.Ping。
        /// 拿不到就返回 null（前端显示"—"）——**不要瞎编一个数字**。
        /// </summary>
        private static int? TryGetPing(PlayerData data)
        {
            try
            {
                var client = data?.Client;
                if (client == null) return null;

                object peer = client.GetType().GetProperty("Peer")?.GetValue(client);
                if (peer == null) return null;

                object ping = peer.GetType().GetProperty("Ping")?.GetValue(peer);
                if (ping is int value) return value;
            }
            catch
            {
            }
            return null;
        }

        private static string DescribeOnlineTime(PlayerData data)
        {
            if (data == null) return "-";
            try
            {
                // FirstSpawnTime / LastSpawnTime 都是"游戏时间"，与 TotalElapsedGameTime 同一坐标系
                var gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>();
                if (gameInfo == null || data.FirstSpawnTime < 0) return "-";

                double seconds = gameInfo.TotalElapsedGameTime - data.FirstSpawnTime;
                return WebPanelJson.FormatDuration(seconds);
            }
            catch
            {
                return "-";
            }
        }

        private static int CountInventoryItems(ComponentPlayer player)
        {
            var inventory = player?.ComponentMiner?.Inventory;
            if (inventory == null) return 0;

            int total = 0;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                total += inventory.GetSlotCount(i);
            }
            return total;
        }

        // ==========================================
        // 背包
        // ==========================================

        public static string BuildInventory(string guidText)
        {
            var sb = new StringBuilder(4096);

            if (!Guid.TryParse(guidText, out Guid guid))
            {
                return "{\"success\":false,\"message\":" + WebPanelJson.Quote("玩家标识无效") + "}";
            }

            var player = FindPlayer(guid);
            if (player == null)
            {
                return "{\"success\":false,\"message\":" + WebPanelJson.Quote("该玩家不在线（背包数据只在内存里，离线玩家要看存档）") + "}";
            }

            var subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>();

            sb.Append("{\"success\":true");
            sb.Append(",\"name\":").Append(WebPanelJson.Quote(player.PlayerData?.Name ?? "?"));
            sb.Append(",\"guid\":").Append(WebPanelJson.Quote(guid.ToString()));
            // 当前游戏模式：前端据此决定"主背包"这一栏到底标成生存背包还是创造背包。
            sb.Append(",\"gameMode\":").Append(WebPanelJson.Quote(
                Safe(() => CommonLib.GetEffectiveGameMode(player.PlayerData).ToString()) ?? "-"));

            // 主背包
            sb.Append(",\"main\":");
            AppendInventorySlots(sb, player.ComponentMiner?.Inventory, subsystemTerrain);

            // 护甲
            sb.Append(",\"armor\":");
            AppendInventorySlots(sb, player.ComponentClothing, subsystemTerrain);

            // 创造背包（只有创造模式玩家才有内容）
            sb.Append(",\"creative\":");
            AppendInventorySlots(sb, Safe(() => player.Entity?.FindComponent<ComponentCreativeInventory>()),
                subsystemTerrain);

            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendInventorySlots(StringBuilder sb, IInventory inventory, SubsystemTerrain terrain)
        {
            if (inventory == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('{');
            sb.Append("\"slotsCount\":").Append(inventory.SlotsCount);
            sb.Append(",\"visibleSlots\":").Append(inventory.VisibleSlotsCount);
            sb.Append(",\"activeSlot\":").Append(inventory.ActiveSlotIndex);
            sb.Append(",\"slots\":[");

            bool first = true;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                int count;
                int value;
                try
                {
                    count = inventory.GetSlotCount(i);
                    value = inventory.GetSlotValue(i);
                }
                catch
                {
                    continue;
                }

                if (count <= 0) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append('{');
                sb.Append("\"index\":").Append(i);
                sb.Append(",\"value\":").Append(value);
                sb.Append(",\"count\":").Append(count);
                sb.Append(",\"contents\":").Append(Terrain.ExtractContents(value));
                sb.Append(",\"data\":").Append(Terrain.ExtractData(value));
                sb.Append(",\"name\":").Append(WebPanelJson.Quote(DescribeItem(value, terrain)));
                sb.Append('}');
            }

            sb.Append("]}");
        }

        /// <summary>把物品 value 变成可读名字。取不到就退化成"方块#编号"。</summary>
        private static string DescribeItem(int value, SubsystemTerrain terrain)
        {
            try
            {
                int contents = Terrain.ExtractContents(value);
                var blocks = BlocksManager.Blocks;
                if (blocks != null && contents >= 0 && contents < blocks.Length)
                {
                    var block = blocks[contents];
                    if (block != null)
                    {
                        string name = block.GetDisplayName(terrain, value);
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                        if (!string.IsNullOrWhiteSpace(block.DefaultDisplayName)) return block.DefaultDisplayName;
                    }
                }
                return "方块#" + contents;
            }
            catch
            {
                return "方块#" + Terrain.ExtractContents(value);
            }
        }

        // ==========================================
        // 命令表
        // ==========================================

        /// <summary>
        /// 可执行命令清单。遍历 <c>CmdManager.GetProcessCmdList()</c> 拿真实注册的命令，
        /// 再用白名单过滤 —— 这样网页上列的**就是真的能敲的**，不会出现"点了报错"。
        /// </summary>
        public static string BuildCommandList(WebPanelConfig settings)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"success\":true,\"commands\":[");

            try
            {
                // 返回的是 ReadOnlyList<AbstractProcessCmd>（值类型），不能与 null 比较，直接遍历即可
                var list = CmdManager.GetProcessCmdList();
                bool first = true;

                {
                    foreach (var cmd in list)
                    {
                        if (cmd == null) continue;
                        string name = Safe(() => cmd.Cmd);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (!WebPanelCommandPolicy.IsAllowed(name, settings, out _)) continue;

                        if (!first) sb.Append(',');
                        first = false;

                        sb.Append('{');
                        sb.Append("\"name\":").Append(WebPanelJson.Quote(name));
                        sb.Append(",\"introduce\":").Append(WebPanelJson.Quote(Safe(() => cmd.Introduce) ?? ""));
                        sb.Append(",\"authLevel\":").Append(SafeInt(() => cmd.AuthLevel));
                        sb.Append('}');
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("],\"error\":").Append(WebPanelJson.Quote(ex.Message));
                return sb.Append('}').ToString();
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // ==========================================
        // 玩家管理动作
        // ==========================================

        /// <summary>
        /// 执行一个玩家管理动作。**在主线程调用**。
        ///
        /// ⚠ 每个动作的命令行都是**对着核心反编译源码核过**的（见各 case 的注释）。
        /// 之前这里凭印象拼命令，踩了一串坑：
        /// - `/gamemode` 的参数顺序是 **玩家在前、模式在后**，写反了会报"模式无效: '玩家名'"；
        /// - `/kick`、`/kill` **必须带子命令**（`user`），只写名字只会打印帮助文本；
        /// - 核心的 `/clear` 是**清理世界掉落物/动物/方块实体**，跟"清背包"完全无关。
        /// 所以现在：能用命令的走命令（行为与游戏内一致），没有对应命令的直接调组件 API。
        /// </summary>
        public static string ApplyPlayerAction(string guidText, string action, out string error)
            => ApplyPlayerAction(guidText, action, null, out error);

        /// <summary>
        /// 执行一个玩家管理动作。
        /// </summary>
        /// <param name="ip">
        /// 仅 <c>banip</c> / <c>unbanip</c> 用得到：要封/要解的那个 IP 地址，由管理员在面板上
        /// **显式输入**。绝不允许调用方偷偷传"该玩家当前连接的地址" —— 见 banip 分支的注释。
        /// </param>
        public static string ApplyPlayerAction(string guidText, string action, string ip, out string error)
        {
            error = null;

            if (!Guid.TryParse(guidText, out Guid guid))
            {
                error = "玩家标识无效";
                return null;
            }

            var player = FindPlayer(guid);
            if (player == null)
            {
                error = "该玩家不在线";
                return null;
            }

            string name = player.PlayerData?.Name ?? guid.ToString();
            // 社区账号 id —— 封禁/解封都认它（FRP 下 IP 不可靠，见下面对 ban 的注释）。
            string accountId = Safe(() => player.PlayerData?.Client?.CommunityAccountId);

            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                // /kick user (玩家) (原因) —— 缺 user 子命令只会打印帮助文本，命令本身不算成功。
                case "kick":
                    return ExecuteViaCommand($"/kick user {name} 你已被管理员踢出服务器",
                        $"已踢出 {name}", out error);

                // /kill user (玩家) —— 同样必须带 user。
                case "kill":
                    return ExecuteViaCommand($"/kill user {name}", $"已击杀 {name}", out error);

                // /admin heal <玩家> —— 基础插件的管理命令（核心没有 heal）。
                case "heal":
                    return ExecuteViaCommand($"/admin heal {name}", $"已回满 {name} 的生命", out error);

                // 核心的 /clear 是清世界掉落物，**不是**清背包 ⇒ 直接调库存组件。
                case "clear":
                    return ClearPlayerInventory(player, name, out error);

                // 基础插件的 /admin fix：血量/食物/睡眠/潮湿/体温一次回到舒适值。
                case "fix":
                    return ExecuteViaCommand($"/admin fix {name}", $"已修复 {name} 的生存状态", out error);

                // /admin god <玩家>：切换无敌（核心的 IsInvulnerable 由它统管，走命令比直接改字段稳）。
                case "godmode":
                    return ExecuteViaCommand($"/admin god {name}", $"已切换 {name} 的无敌状态", out error);

                // ⚠ 核心没有 "/tp2 <玩家> spawn" 这条命令（tp2 是基础插件自定义的，签名不同）。
                //    "送回重生点"直接读世界的默认出生点再传送，不依赖任何命令。
                case "respawn":
                    return SendToSpawn(player, name, out error);

                // /gamemode <玩家|me> <模式> —— **玩家在前**。核心另有 /gmc、/gms 快捷方式，更稳。
                case "gamemode":
                {
                    // 取当前生效模式（核心把"创造模式飞行"等状态也算进去，用 GetEffectiveGameMode 更准）
                    string current = Safe(() => CommonLib.GetEffectiveGameMode(player.PlayerData).ToString());
                    bool toCreative = !string.Equals(current, "Creative", StringComparison.OrdinalIgnoreCase);

                    // 用快捷命令：/gmc <玩家> / /gms <玩家>，比 /gamemode 少一个容易写反的参数位。
                    string quick = toCreative ? "gmc" : "gms";
                    return ExecuteViaCommand($"/{quick} {name}",
                        $"已将 {name} 切换为{(toCreative ? "创造" : "生存")}模式", out error);
                }

                // ---- 封禁：以**社区账号 id**为准，不碰 IP ----
                // ⚠ 本服走 FRP 转发，服务端看到的远端 IP 是代理机的，所有玩家**共用同一个 IP**；
                //   一旦按 IP 封禁（/ban ip add）就会把整个服务器的人一起挡在门外。
                //   所以这里只提供"按账号封禁"（/ban add <账号id>），它与人一一对应、与网络路径无关。
                case "ban":
                    if (string.IsNullOrEmpty(accountId) || accountId == "-1")
                    {
                        error = $"拿不到 {name} 的社区账号 id（可能是离线/单机账号），无法按账号封禁；" +
                                "这种账号请改用游戏内控制台手动处理";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban add {accountId}",
                        $"已按账号封禁 {name}（账号 {accountId}）", out error);

                case "unban":
                    if (string.IsNullOrEmpty(accountId) || accountId == "-1")
                    {
                        error = "拿不到该玩家的社区账号 id，无法解封";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban remove {accountId}",
                        $"已解封 {name}（账号 {accountId}）", out error);

                // ---- 其它常用核心命令，统一走"借命令执行"，行为与游戏内完全一致 ----
                // /ban list —— 列出封禁名单（回显会带回来给前端看）
                case "banlist":
                    return ExecuteViaCommand("/ban list", "封禁名单", out error);

                // /ban ip list —— 列出 IP 封禁名单
                case "baniplist":
                    return ExecuteViaCommand("/ban ip list", "IP 封禁名单", out error);

                // ---- 封禁 IP：提供两条路，都不允许"一键按当前连接 IP 封" ----
                //
                // ⚠⚠ 本服走 FRP 转发时，服务端看到的 Peer.Address 是**代理机地址**，全体玩家共用；
                //     如果面板提供"按该玩家当前 IP 封禁"并一键执行，等于把整个服务器封掉。
                //     所以这里刻意把"封某个具体 IP"做成**需要管理员自己显式输入 IP**的动作，
                //     面板会先把候选 IP（该玩家连接的地址）显示出来供参考和确认，但绝不代填代提交。
                case "banip":
                {
                    // ip 由调用方在 payload 里显式给出，这里只做格式校验后转交核心
                    string targetIp = ip;
                    if (string.IsNullOrWhiteSpace(targetIp))
                    {
                        error = "请先填写要封禁的 IP 地址";
                        return null;
                    }
                    targetIp = targetIp.Trim();
                    if (!LooksLikeIp(targetIp))
                    {
                        error = "IP 格式不正确，只接受 IPv4 / IPv6（不要带端口）";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban ip add {targetIp}",
                        $"已封禁 IP {targetIp}（该地址上的在线玩家会被立即断开）", out error);
                }

                // /ban ip remove (ip)
                case "unbanip":
                {
                    string targetIp = (ip ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(targetIp) || !LooksLikeIp(targetIp))
                    {
                        error = "IP 格式不正确，只接受 IPv4 / IPv6（不要带端口）";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban ip remove {targetIp}",
                        $"已解封 IP {targetIp}", out error);
                }

                // /ban ip user (账号id) —— 核心的语义是"记下这个账号，把它**当前**的 IP 也加进封禁表"。
                // 这正是 FRP 环境下相对安全的做法：IP 是由核心按账号现场关联出来的，
                // 而不是面板拿一个可能全是代理机的地址去盲封。
                case "banipuser":
                    if (string.IsNullOrEmpty(accountId) || accountId == "-1")
                    {
                        error = $"拿不到 {name} 的社区账号 id，无法记录其 IP";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban ip user {accountId}",
                        $"已记录 {name}（账号 {accountId}）的 IP 并加入封禁表", out error);

                // /ban ip ruser (账号id) —— 解除"跟账号走的 IP 记录"
                case "unbanipuser":
                    if (string.IsNullOrEmpty(accountId) || accountId == "-1")
                    {
                        error = "拿不到该玩家的社区账号 id，无法解除其 IP 记录";
                        return null;
                    }
                    return ExecuteViaCommand($"/ban ip ruser {accountId}",
                        $"已解除 {name}（账号 {accountId}）的 IP 记录", out error);

                // /time set day|night —— 切昼夜
                case "day":
                    return ExecuteViaCommand("/time set day", "已把时间设为白天", out error);
                case "night":
                    return ExecuteViaCommand("/time set night", "已把时间设为夜晚", out error);

                // /admin tell <玩家> <消息> —— 私聊提示（前端会带上自定义文本时用另一个入口）
                case "tell":
                    return ExecuteViaCommand($"/admin tell {name} 管理员正在关注你",
                        $"已私聊提醒 {name}", out error);

                default:
                    error = "不支持的动作：" + action;
                    return null;
            }
        }

        /// <summary>
        /// 清空玩家背包。核心**没有**这条命令（`/clear` 是清世界掉落物），所以直接动库存组件。
        ///
        /// ⚠ <see cref="ComponentMiner.Inventory"/> 的类型是 <c>IInventory</c>，而且**创造模式下它可能
        /// 就指向创造背包**。所以这里不去按"生存/创造"分两份清，而是：
        /// 主库存 + 创造背包（若存在）各清一遍，保证两种模式下的东西都被清掉、不会漏。
        /// 护甲不动 —— 那是"装备"不是"背包"，扒装备是另一个独立动作。
        /// </summary>
        private static string ClearPlayerInventory(ComponentPlayer player, string name, out string error)
        {
            error = null;

            try
            {
                int cleared = 0;

                // IInventory 是接口，直接遍历它的槽位
                IInventory main = player.ComponentMiner?.Inventory;
                if (main != null)
                {
                    for (int i = 0; i < main.SlotsCount; i++)
                    {
                        int count = main.GetSlotCount(i);
                        if (count <= 0) continue;
                        main.RemoveSlotItems(i, count);
                        main.OnSlotChange(i);
                        cleared++;
                    }
                }

                // 创造背包独立存在（ComponentCreativeInventory），创造模式下才有内容
                var creative = player.Entity?.FindComponent<ComponentCreativeInventory>();
                if (creative != null)
                {
                    for (int i = 0; i < creative.SlotsCount; i++)
                    {
                        int count = creative.GetSlotCount(i);
                        if (count <= 0) continue;
                        creative.RemoveSlotItems(i, count);
                        cleared++;
                    }
                }

                // 清完必须同步一次，否则玩家屏幕上的快捷栏还显示着旧物品。
                // 只用核心自己的做法：ComponentInventoryPackage(背包, 当前槽) —— 见反编译源里
                // `QueuePackage(new ComponentInventoryPackage(ComponentMiner.Inventory, ...))`。
                // ⚠ To 必须显式设成该玩家的 Client：不设会**广播给所有人**。
                var client = player.PlayerData?.Client;
                if (client != null && main != null)
                {
                    CommonLib.Net.QueuePackage(new ComponentInventoryPackage(main, main.ActiveSlotIndex)
                    {
                        To = client
                    });
                }

                return cleared == 0
                    ? $"{name} 的背包本来就是空的"
                    : $"已清空 {name} 的背包（{cleared} 个格子）";
            }
            catch (Exception ex)
            {
                error = "清空背包时出错：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 把玩家送回世界出生点。核心没有现成命令（`/tp2` 是基础插件自定义的、签名不同），
        /// 所以照搬基础插件主城模块那套**已验证可行**的传送写法：
        /// 设位置 → 同步 <c>netPosition</c> → 发包让客户端跟上。坐骑要一起挪，否则会撕裂。
        /// </summary>
        private static string SendToSpawn(ComponentPlayer player, string name, out string error)
        {
            error = null;

            try
            {
                ComponentBody body = player.ComponentBody;
                if (body == null || !body.IsAddedToProject)
                {
                    error = "该玩家没有位置组件（可能正在加载）";
                    return null;
                }

                // 出生点在 SubsystemPlayers 上（**实例属性**，不是静态字段）
                var subsystem = GameManager.Project?.FindSubsystem<SubsystemPlayers>();
                if (subsystem == null)
                {
                    error = "服务器项目未就绪";
                    return null;
                }

                Vector3 spawn = subsystem.GlobalSpawnPosition;
                var target = new Vector3(spawn.X, spawn.Y + 0.5f, spawn.Z);   // 抬高半格，避免卡进地面

                // 坐骑和玩家一起挪（基础插件里踩过：不挪坐骑会把玩家"拽"回原地）
                ComponentBody mountBody = player.ComponentRider?.Mount?.ComponentBody;
                if (mountBody != null && mountBody.IsAddedToProject)
                {
                    Vector3 delta = target - body.Position;
                    mountBody.Position += delta;
                    mountBody.netPosition.SetNext(mountBody.Position);
                }

                body.Position = target;
                body.netPosition.SetNext(target);

                CommonLib.Net.QueuePackage(new ComponentPlayerPackage(
                    player, ComponentPlayerPackage.PlayerAction.PositionSet));

                return $"已将 {name} 送回出生点";
            }
            catch (Exception ex)
            {
                error = "传送失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 借一条命令来做事。这样做的代价是只有"已注册且名字对得上"的命令才会成功，
        /// 好处是行为与游戏内完全一致 —— 便宜且不容易写出第二套逻辑。
        /// </summary>
        private static string ExecuteViaCommand(string command, string successMessage, out string error)
        {
            string output = WebPanelCommandRunner.Execute(command, out error);
            if (error != null) return null;

            // 命令执行了但没报错 —— 有些命令失败也是走"回显一行提示"而不是抛异常
            // （比如 /kick 参数不对时只打印帮助文本），所以把回显一并带回前端让人看见真实结果。
            return string.IsNullOrWhiteSpace(output) ? successMessage : successMessage + "：" + output;
        }

        // ==========================================
        // 工具
        // ==========================================

        private static ComponentPlayer FindPlayer(Guid guid)
        {
            var subsystem = GameManager.Project?.FindSubsystem<SubsystemPlayers>();
            // 同样：ComponentPlayers 是值类型，先判子系统的引用
            if (subsystem == null) return null;
            var list = subsystem.ComponentPlayers;

            for (int i = 0; i < list.Count; i++)
            {
                var player = list[i];
                if (player?.PlayerData != null && player.PlayerData.PlayerGUID == guid) return player;
            }
            return null;
        }

        private static string DescribeDayPhase(double timeOfDay)
        {
            // TimeOfDay 是 0~1 的昼夜进度。分界点按核心的昼夜比例粗分。
            if (timeOfDay < 0.25) return "清晨";
            if (timeOfDay < 0.5) return "白天";
            if (timeOfDay < 0.75) return "傍晚";
            return "夜晚";
        }

        private static string DescribeWeather(SubsystemWeather weather)
        {
            try
            {
                if (weather == null) return "未知";
                float rain = weather.PrecipitationIntensity;
                if (rain > 0.6f) return "大雨";
                if (rain > 0.15f) return "小雨";
                if (weather.FogIntensity > 0.3f) return "有雾";
                return "晴朗";
            }
            catch
            {
                return "未知";
            }
        }

        // ---- 到处 try/catch 的取值助手：面板绝不能因为某个子系统缺失就整个 500 ----

        private static T Safe<T>(Func<T> getter) where T : class
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static int SafeInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0;
            }
        }

        private static double SafeDouble(Func<double> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0.0;
            }
        }

        private static float SafeFloat(Func<float> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0f;
            }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 粗校验一个字符串像不像 IP 地址（IPv4 或 IPv6），**不接受带端口**。
        ///
        /// 为什么不用 <c>IPAddress.TryParse</c>：它会接受 "1"、"12345" 这类裸数字当成
        /// IPv4 简写（等价于 0.0.0.1 / 0.0.48.57），拿这种值去封禁纯属误伤。
        /// 这里宁可严一点：只认"点分四段 0-255"或"含冒号的十六进制"。
        /// </summary>
        private static bool LooksLikeIp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length > 45) return false;

            // IPv6：含冒号，且只由十六进制字符与冒号组成
            if (text.IndexOf(':') >= 0)
            {
                foreach (char c in text)
                {
                    bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                              (c >= 'A' && c <= 'F') || c == ':';
                    if (!ok) return false;
                }
                return true;
            }

            // IPv4：恰好四段，每段 0-255，没有前导零以外的花样
            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9') return false;
                }
                if (!int.TryParse(part, out int value) || value < 0 || value > 255) return false;
            }
            return true;
        }
    }
}
