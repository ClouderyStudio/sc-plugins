using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine;
using Game;
using Game.NetWork;
using Game.Server;

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
        /// 每个动作最终都落到一条真实命令上（复用既有实现），而不是自己再写一遍逻辑 ——
        /// 这样行为与游戏内敲命令完全一致，也不会绕过各模块自己的校验。
        /// </summary>
        public static string ApplyPlayerAction(string guidText, string action, out string error)
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

            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "kick":
                    return ExecuteViaCommand($"/kick {name}", $"已踢出 {name}", out error);

                case "kill":
                    return ExecuteViaCommand($"/kill {name}", $"已击杀 {name}", out error);

                case "heal":
                    return ExecuteViaCommand($"/heal {name}", $"已回满 {name} 的生命", out error);

                case "clear":
                    return ExecuteViaCommand($"/clear {name}", $"已清空 {name} 的背包", out error);

                case "respawn":
                    return ExecuteViaCommand($"/tp2 {name} spawn", $"已将 {name} 送回重生点", out error);

                case "gamemode":
                {
                    // 切换创造 <-> 生存
                    var current = Safe(() => CommonLib.GetEffectiveGameMode(player.PlayerData).ToString());
                    string target = string.Equals(current, "Creative", StringComparison.OrdinalIgnoreCase)
                        ? "Survival"
                        : "Creative";
                    return ExecuteViaCommand($"/gamemode {target.ToLowerInvariant()} {name}",
                        $"已将 {name} 切换为 {(target == "Creative" ? "创造" : "生存")}模式", out error);
                }

                case "godmode":
                {
                    var health = player.ComponentHealth;
                    if (health == null)
                    {
                        error = "该玩家没有生命组件";
                        return null;
                    }
                    bool now = !health.IsInvulnerable;
                    health.IsInvulnerable = now;
                    return now ? $"已开启 {name} 的无敌" : $"已关闭 {name} 的无敌";
                }

                default:
                    error = "不支持的动作：" + action;
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

            // 命令执行了但没报错 —— 有些命令失败也是走"回显一行提示"而不是抛异常，
            // 所以把回显一并带回前端，让人能看见真实结果。
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
    }
}
