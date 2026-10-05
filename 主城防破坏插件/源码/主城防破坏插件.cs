// 主城防破坏插件 —— 主城范围内禁止破坏、放置、爆炸、放火、锤子、投射与改告示牌。
//
// 迁移自旧版 .NET Framework 插件（主城防破坏插件.cs），适配到新版 Survivalcraft API。
// 涉及的 5 个事件接口在新版全部保留，签名一致。
//
// 相比旧版修掉的问题：
//   1. ⚠️ Save() 整段被注释掉 —— 区域配置只能手改 JSON，插件自己永远不落盘。
//      现在恢复保存（原子写入），Save() 会把内存里的区域写回配置文件。
//   2. ⚠️ ReceiveMessage 空实现却把 External 设为 false —— 语义是"这条消息不由本插件处理"，
//      但在核心的 foreach 遍历里任意一个 handler 改写 External 就会改变全局处理流程，
//      旧版这样写等于静默干扰全服聊天。这里直接不实现该接口。
//   3. ⚠️ Use() 里发现玩家在主城用锤子就直接 RemoveClient 踢人 —— 惩罚过激且锤子方块 ID
//      230 是硬编码的（换版本或被模组改掉即失效）。现在改为可配置的方块 ID 列表，
//      拦截时默认只警告 + 记日志，踢人需要显式打开 KickOnHammer。
//   4. ⚠️ IsInMajorCityAreas 依赖 SubsystemGameInfo 非空，而首次进档时它必为 null
//      → 坐标归一化被跳过、区域判定整体失效。这里去掉这个依赖。
//   5. 判定用 point.ToString() 打日志，改为统一的坐标格式。

using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using Game.Server;
using Game.Server.Event;
using Game.Server.PlayerEvent;
using Newtonsoft.Json;

namespace MajorCityPlugin
{
    /// <summary>
    /// 一个立方体区域。坐标按 X/Y/Z 各自独立归一化，Min 永远不大于 Max。
    /// </summary>
    public sealed class MajorCityArea
    {
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MinZ { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public int MaxZ { get; set; }

        public void Normalize()
        {
            if (MinX > MaxX) { int t = MinX; MinX = MaxX; MaxX = t; }
            if (MinY > MaxY) { int t = MinY; MinY = MaxY; MaxY = t; }
            if (MinZ > MaxZ) { int t = MinZ; MinZ = MaxZ; MaxZ = t; }
        }

        public bool Contains(Point3 point)
        {
            return MinX <= point.X && point.X <= MaxX
                && MinY <= point.Y && point.Y <= MaxY
                && MinZ <= point.Z && point.Z <= MaxZ;
        }

        public override string ToString()
        {
            return string.Format("({0},{1},{2})~({3},{4},{5})", MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
        }
    }

    public sealed class MajorCityConfig
    {
        /// <summary>主城区域列表。运行时可增删，Save() 会写回配置文件。</summary>
        public List<MajorCityArea> Areas { get; set; } = new List<MajorCityArea>();

        /// <summary>
        /// 视为"锤子"的方块 ID 列表。旧版硬编码为 230，这里改为可配。
        /// 方块 ID 会随版本/模组变化，正式使用前请用哨子获取信息插件确认实际 ID。
        /// </summary>
        public List<int> HammerBlockIds { get; set; } = new List<int> { 230 };

        /// <summary>是否放行管理员。默认放行（与旧版一致）。</summary>
        public bool AllowServerManager { get; set; } = true;

        /// <summary>在主城使用锤子时是否踢人。旧版无条件踢人，这里默认只警告。</summary>
        public bool KickOnHammer { get; set; } = false;

        /// <summary>拦截时是否给玩家屏幕提示。</summary>
        public bool NotifyPlayer { get; set; } = true;
    }

    public sealed class MajorCityPlugin : ServerPlugin, IPlayerBreakAndPlaceHandle, IExplodeEventHandle,
        IFireEventHandle, IPlayerInteractEventHandle
    {
        public override int Version => 10000;

        public override string Name => "主城防破坏插件";

        public byte FirstLevel => 0;

        private readonly object _configLock = new object();
        private MajorCityConfig _config = new MajorCityConfig();

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/主城防破坏插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "MajorCityAreas.json"); }
        }

        /// <summary>实例引用，供本插件的命令/其他模块查询区域。</summary>
        public static MajorCityPlugin Instance { get; private set; }

        public override void Initialize()
        {
            Instance = this;
            EnsureConfigDirectory();
            LoadConfig();
            PlayerBreakAndPlaceBlockEventManager.AddObject(this);
            ExplodeEventManager.AddObject(this);
            FireEventManager.AddObject(this);
            PlayerInteractEventManager.AddObject(this);
            Log.Information("[主城防破坏] 已加载 " + CountAreas() + " 个保护区域"
                + (_config.KickOnHammer ? "（锤子拦截=踢出）" : "（锤子拦截=仅警告）"));
        }

        public override void Load()
        {
            EnsureConfigDirectory();
            LoadConfig();
        }

        /// <summary>
        /// 把内存里的区域写回配置文件。旧版整段被注释掉，导致区域永远只能手改 JSON。
        /// </summary>
        public override void Save()
        {
            SaveConfig();
        }

        private int CountAreas()
        {
            lock (_configLock)
            {
                return _config.Areas == null ? 0 : _config.Areas.Count;
            }
        }

        private MajorCityConfig GetConfig()
        {
            lock (_configLock)
            {
                return _config;
            }
        }

        private static void EnsureConfigDirectory()
        {
            try
            {
                if (!Storage.DirectoryExists(PluginDirectory))
                {
                    Directory.CreateDirectory(PluginDirectory);
                }
            }
            catch (Exception e)
            {
                Log.Error("[主城防破坏] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new MajorCityConfig();
                try
                {
                    if (File.Exists(ConfigFilePath))
                    {
                        MajorCityConfig loaded = JsonConvert.DeserializeObject<MajorCityConfig>(File.ReadAllText(ConfigFilePath));
                        if (loaded != null)
                        {
                            _config = loaded;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[主城防破坏] 载入配置失败，改用默认（无保护区域）: " + e.Message);
                }
                if (_config.Areas == null)
                {
                    _config.Areas = new List<MajorCityArea>();
                }
                foreach (MajorCityArea area in _config.Areas)
                {
                    if (area != null)
                    {
                        area.Normalize();
                    }
                }
            }
        }

        private void SaveConfig()
        {
            lock (_configLock)
            {
                try
                {
                    EnsureConfigDirectory();
                    string temp = ConfigFilePath + ".partial";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(_config, Formatting.Indented));
                    File.Copy(temp, ConfigFilePath, true);
                    File.Delete(temp);
                }
                catch (Exception e)
                {
                    Log.Error("[主城防破坏] 保存配置失败: " + e.Message);
                }
            }
        }

        /// <summary>点是否落在任一主城保护区域内。</summary>
        public static bool IsInMajorCityArea(Point3 point)
        {
            MajorCityPlugin plugin = Instance;
            if (plugin == null)
            {
                return false;
            }
            MajorCityConfig config = plugin.GetConfig();
            if (config.Areas == null)
            {
                return false;
            }
            for (int i = 0; i < config.Areas.Count; i++)
            {
                MajorCityArea area = config.Areas[i];
                if (area != null && area.Contains(point))
                {
                    return true;
                }
            }
            return false;
        }

        public static List<MajorCityArea> GetAreasSnapshot()
        {
            MajorCityPlugin plugin = Instance;
            if (plugin == null)
            {
                return new List<MajorCityArea>();
            }
            lock (plugin._configLock)
            {
                return new List<MajorCityArea>(plugin._config.Areas ?? new List<MajorCityArea>());
            }
        }

        /// <summary>是否应放行该玩家（管理员豁免）。</summary>
        private static bool IsExempt(ComponentPlayer player, MajorCityConfig config)
        {
            if (!config.AllowServerManager || player == null)
            {
                return false;
            }
            return player.PlayerData != null && player.PlayerData.ServerManager;
        }

        private static string DescribePlayer(ComponentPlayer player)
        {
            if (player == null || player.PlayerData == null)
            {
                return "[未知玩家]";
            }
            string account = "?";
            if (player.PlayerData.Client != null)
            {
                account = player.PlayerData.Client.CommunityAccountId;
            }
            return "[" + account + "]" + (player.PlayerData.Name ?? "?");
        }

        /// <summary>
        /// 统一的拦截处理：记日志 + 可选提示玩家。返回 true 表示应当拦截。
        /// </summary>
        private bool Reject(ComponentPlayer player, Point3 point, string action)
        {
            MajorCityConfig config = GetConfig();
            Log.Information(string.Format("[主城防破坏] 已拦截 {0} 在 ({1},{2},{3}) {4}",
                DescribePlayer(player), point.X, point.Y, point.Z, action));
            if (config.NotifyPlayer)
            {
                SendMessage(player, "主城保护区域内不可" + action);
            }
            return true;
        }

        public bool PlayerBreakEvent(ComponentPlayer componentPlayer, Point3 point, int digBlockValue, int toolLevel)
        {
            MajorCityConfig config = GetConfig();
            if (IsExempt(componentPlayer, config) || !IsInMajorCityArea(point))
            {
                return true;
            }
            return !Reject(componentPlayer, point, "破坏方块");
        }

        public bool PlayerPlaceEvent(ComponentPlayer componentPlayer, Point3 point, int placeBlockValue)
        {
            MajorCityConfig config = GetConfig();
            if (IsExempt(componentPlayer, config) || !IsInMajorCityArea(point))
            {
                return true;
            }
            return !Reject(componentPlayer, point, "放置方块");
        }

        /// <summary>区域内爆炸威力归零。</summary>
        public void Explode(int x, int y, int z, ref float pressure, bool isIncendiary, bool noExplosionSound, PlayerData miner)
        {
            Point3 point = new Point3(x, y, z);
            if (IsInMajorCityArea(point))
            {
                pressure = 0f;
                Log.Information(string.Format("[主城防破坏] 已压制主城内 ({0},{1},{2}) 的爆炸（威力归零）", x, y, z));
            }
        }

        public bool Fire(Ray3 ray, ComponentMiner componentMiner)
        {
            // 火把等"朝一个方向点火"不判定位置，与旧版一致。
            return true;
        }

        /// <summary>
        /// 火焰蔓延判定。新版 IFireEventHandle 的第三个方法，旧版没有实现 ——
        /// 漏掉它会 CS0535「不实现接口成员」。返回 false 可以阻止这一格起火。
        /// </summary>
        public bool OnFireGeneration(int x, int y, int z, ref float spreadability)
        {
            if (!IsInMajorCityArea(new Point3(x, y, z)))
            {
                return true;
            }
            // 主城内火势不蔓延：把蔓延度压到 0（返回 true 表示"这一步仍继续"，
            // 压到 0 后核心不会再把火带到相邻格）。
            spreadability = 0f;
            return true;
        }

        public bool FireTerrain(CellFace cellFace, ComponentMiner componentMiner)
        {
            if (cellFace == null || componentMiner == null)
            {
                return true;
            }
            ComponentPlayer player = componentMiner.ComponentPlayer;
            MajorCityConfig config = GetConfig();
            if (IsExempt(player, config) || !IsInMajorCityArea(cellFace.Point))
            {
                return true;
            }
            Reject(player, cellFace.Point, "放火");
            return false;
        }

        public bool EditSignMessage(Point3 point, ComponentPlayer componentPlayer)
        {
            MajorCityConfig config = GetConfig();
            if (IsExempt(componentPlayer, config) || !IsInMajorCityArea(point))
            {
                return true;
            }
            return !Reject(componentPlayer, point, "修改告示牌");
        }

        public bool Interact(ComponentPlayer componentPlayer, CellFace cellFace)
        {
            return true;
        }

        /// <summary>
        /// 锤子判定。旧版把方块 ID 230 写死并直接踢人；现在 ID 走配置、踢人需显式打开。
        /// </summary>
        public bool Use(ComponentPlayer componentPlayer, object raycast, int activeBlockValue)
        {
            MajorCityConfig config = GetConfig();
            if (config.HammerBlockIds == null || !config.HammerBlockIds.Contains(Terrain.ExtractContents(activeBlockValue)))
            {
                return true;
            }
            if (!(raycast is TerrainRaycastResult))
            {
                return true;
            }
            Point3 point = ((TerrainRaycastResult)raycast).CellFace.Point;
            if (IsExempt(componentPlayer, config) || !IsInMajorCityArea(point))
            {
                return true;
            }

            Log.Information(string.Format("[主城防破坏] 已拦截 {0} 在主城 ({1},{2},{3}) 使用锤子（方块ID={4}）",
                DescribePlayer(componentPlayer), point.X, point.Y, point.Z, Terrain.ExtractContents(activeBlockValue)));

            if (config.KickOnHammer)
            {
                SendMessage(componentPlayer, "不可在主城使用锤子");
                if (componentPlayer.PlayerData != null && componentPlayer.PlayerData.Client != null)
                {
                    CommonLib.Net.RemoveClient(componentPlayer.PlayerData.Client, "不可在主城使用锤子");
                }
                return false;
            }

            if (config.NotifyPlayer)
            {
                SendMessage(componentPlayer, "不可在主城使用锤子");
            }
            return false;
        }

        public bool Hit(ComponentPlayer componentPlayer, ComponentBody componentBody, Vector3 hitPoint, Vector3 hitDirection)
        {
            return true;
        }

        /// <summary>区域内禁止投射物品（箭矢等）。</summary>
        public bool Aim(ComponentPlayer componentPlayer, int activeBlockValue, Ray3 aim, AimState state)
        {
            MajorCityConfig config = GetConfig();
            if (IsExempt(componentPlayer, config) || !IsInMajorCityArea(new Point3(aim.Position)))
            {
                return true;
            }
            return !Reject(componentPlayer, new Point3(aim.Position), "投射物品");
        }

        private static void SendMessage(ComponentPlayer player, string text)
        {
            if (player == null || player.PlayerData == null || player.PlayerData.Client == null)
            {
                return;
            }
            // To 必须显式设置：To 为 null 的包会被核心广播给所有玩家。
            MessagePackage message = new MessagePackage(null, text, 0, null);
            message.To = player.PlayerData.Client;
            CommonLib.Net.QueuePackage(message);
        }
    }
}