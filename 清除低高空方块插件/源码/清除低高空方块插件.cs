// 清除低高空方块插件 —— 禁止玩家改动基岩层与世界顶层的方块。
//
// 迁移自旧版 .NET Framework 插件（清除低高空方块.cs），适配到新版 Survivalcraft API。
// 相比旧版的修正：
//   1. 阈值 5 / 250 原来是硬编码的。新版世界高度上限可能与旧版不同，
//      这里改为配置文件可配，默认值不变，但支持 0 = 不限制该方向。
//   2. 补了管理员豁免。旧版 return false 会连服务端自己/管理员的合法修改一起拦掉
//      （比如主城建设指令、领地插件的批量放置）。
//   3. 拦截时记一行日志并可提示玩家，否则管理员会看到"点了没反应"而不知原因。

using System;
using System.IO;
using Engine;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using Game.Server;
using Game.Server.Event;
using Newtonsoft.Json;

namespace ClearLowLevelPlugin
{
    public sealed class ClearLowLevelConfig
    {
        /// <summary>低于（含）此高度的方块不可改。0 = 不限制下界。</summary>
        public int MinY { get; set; } = 5;

        /// <summary>高于（含）此高度的方块不可改。0 = 不限制上界。</summary>
        public int MaxY { get; set; } = 250;

        /// <summary>是否放行管理员（旧版没有这个豁免）。</summary>
        public bool AllowServerManager { get; set; } = true;

        /// <summary>拦截时是否给玩家屏幕提示。</summary>
        public bool NotifyPlayer { get; set; } = true;
    }

    public sealed class ClearLowLevelPlugin : ServerPlugin, IBlockChangeEventHandle
    {
        public override int Version => 10000;

        public override string Name => "清除低高空方块插件";

        public byte FirstLevel => 0;

        private readonly object _configLock = new object();
        private ClearLowLevelConfig _config = new ClearLowLevelConfig();

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/清除低高空方块插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "ClearLowLevel.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadConfig();
            BlockChangeEventManager.AddObject(this);
            Log.Information("[清除低高空方块] 已加载，锁定范围 " + DescribeRange());
        }

        public override void Load()
        {
            EnsureConfigDirectory();
            LoadConfig();
        }

        public override void Save()
        {
            SaveConfig();
        }

        private string DescribeRange()
        {
            ClearLowLevelConfig config = GetConfig();
            string lower = config.MinY <= 0 ? "不限制" : ("Y<=" + config.MinY.ToString());
            string upper = config.MaxY <= 0 ? "不限制" : ("Y>=" + config.MaxY.ToString());
            return lower + " / " + upper;
        }

        private ClearLowLevelConfig GetConfig()
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
                Log.Error("[清除低高空方块] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new ClearLowLevelConfig();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        SaveConfigLocked();
                        return;
                    }
                    ClearLowLevelConfig loaded = JsonConvert.DeserializeObject<ClearLowLevelConfig>(File.ReadAllText(ConfigFilePath));
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[清除低高空方块] 载入配置失败，改用默认值: " + e.Message);
                }
            }
        }

        private void SaveConfig()
        {
            lock (_configLock)
            {
                SaveConfigLocked();
            }
        }

        private void SaveConfigLocked()
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
                Log.Error("[清除低高空方块] 保存配置失败: " + e.Message);
            }
        }

        /// <summary>
        /// 当前锁定范围是否覆盖该高度。MinY / MaxY 为 0 表示该方向不限制。
        /// 未加载插件实例时返回 false（宁可不拦，也不要在插件没起来时把整张世界锁死）。
        /// </summary>
        public static bool IsHeightLocked(int y)
        {
            ClearLowLevelPlugin plugin = _instance;
            if (plugin == null)
            {
                return false;
            }
            ClearLowLevelConfig config = plugin.GetConfig();
            if (config.MinY > 0 && y <= config.MinY)
            {
                return true;
            }
            if (config.MaxY > 0 && y >= config.MaxY)
            {
                return true;
            }
            return false;
        }

        private static ClearLowLevelPlugin _instance;

        /// <summary>
        /// 方块改动判定。返回 false 表示拒绝这次改动。
        /// </summary>
        public bool ChangeCell(int x, int y, int z, int oldValue, int newValue, ComponentMiner componentMiner)
        {
            _instance = this;
            ClearLowLevelConfig config = GetConfig();

            bool lowLocked = config.MinY > 0 && y <= config.MinY;
            bool highLocked = config.MaxY > 0 && y >= config.MaxY;
            if (!lowLocked && !highLocked)
            {
                return true;
            }

            // 管理员豁免：旧版没有这条，指令/领地插件的合法放置会被一起拦掉。
            if (config.AllowServerManager && componentMiner != null)
            {
                ComponentPlayer player = componentMiner.ComponentPlayer;
                if (player != null && player.PlayerData != null && player.PlayerData.ServerManager)
                {
                    return true;
                }
            }

            string reason = lowLocked
                ? ("Y=" + y + " 低于下界 " + config.MinY)
                : ("Y=" + y + " 高于上界 " + config.MaxY);

            if (componentMiner != null)
            {
                ComponentPlayer player = componentMiner.ComponentPlayer;
                if (player != null)
                {
                    Log.Information("[清除低高空方块] 已拦截 " + DescribePlayer(player) + " 在 (" + x + "," + y + "," + z + ") 改方块（" + reason + "）");
                    if (config.NotifyPlayer)
                    {
                        Notify(player, "你不能在这个高度建造（" + reason + "）");
                    }
                }
            }
            else
            {
                Log.Information("[清除低高空方块] 已拦截 (" + x + "," + y + "," + z + ") 的方块改动（" + reason + "，无玩家来源）");
            }

            return false;
        }

        private static string DescribePlayer(ComponentPlayer player)
        {
            string name = player.PlayerData != null ? player.PlayerData.Name : "?";
            string account = "?";
            if (player.PlayerData != null && player.PlayerData.Client != null)
            {
                account = player.PlayerData.Client.CommunityAccountId;
            }
            return "[" + account + "]" + name;
        }

        private static void Notify(ComponentPlayer player, string text)
        {
            if (player == null || player.PlayerData == null || player.PlayerData.Client == null)
            {
                return;
            }
            MessagePackage message = new MessagePackage(null, text, 0, null);
            message.To = player.PlayerData.Client;
            CommonLib.Net.QueuePackage(message);
        }

        public void OnTerrainContentsGenerated(TerrainUpdater terrainUpdater, TerrainChunk chunk)
        {
        }
    }
}