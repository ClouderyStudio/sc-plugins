// 火柴记录插件 —— 记录谁在什么位置、朝什么方向点燃了火焰。
//
// 迁移自旧版 .NET Framework 插件（火柴记录插件.cs），适配到新版 Survivalcraft API。
// 相比旧版的修正：
//   1. 新版 IFireEventHandle 有 3 个方法，旧版只实现了 2 个 —— 缺少
//      OnFireGeneration(int,int,int,ref float)，在新版编译不通过。这里补上空实现。
//      注意它的语义：返回 false 可以阻止这一格的火被点燃，这里默认放行（保持旧版"只记录不阻止"）。
//   2. 旧版插件名叫"火柴记录"，但走的是 IFireEventHandle —— 这是"点燃火焰"事件，
//      不是玩家手里那根火柴物品。名字保留（对外兼容），但在注释里说清实际监听的是什么。
//   3. 日志原来直接输出社区 ID + 昵称 + 精确坐标且无开关。这里改成可配置，
//      并把高频的坐标写进日志，低频的默认关闭，避免日志被刷爆。

using System;
using System.IO;
using Engine;
using Game;
using Game.Server;
using Game.Server.Event;
using Newtonsoft.Json;

namespace MatchRecordPlugin
{
    public sealed class MatchRecordConfig
    {
        /// <summary>是否记录"点燃方向"（Fire：朝一个方向点火）。</summary>
        public bool LogFire { get; set; } = false;

        /// <summary>是否记录"点燃方块"（FireTerrain：对某个格子点火）。</summary>
        public bool LogFireTerrain { get; set; } = true;

        /// <summary>是否记录火焰蔓延（OnFireGeneration）。这个每帧可能调用多次，默认关闭。</summary>
        public bool LogFireGeneration { get; set; } = false;

        /// <summary>是否在日志里带上玩家昵称。昵称属于个人信息，可按需关闭。</summary>
        public bool IncludePlayerName { get; set; } = true;

        /// <summary>火焰蔓延日志最多每多少秒记一条（防止刷屏）。</summary>
        public float FireGenerationLogInterval { get; set; } = 30f;

        /// <summary>是否允许阻止火焰点燃（返回 false 拦住）。默认不阻止。</summary>
        public bool BlockFire { get; set; } = false;
    }

    public sealed class MatchRecordPlugin : ServerPlugin, IFireEventHandle
    {
        public override int Version => 10000;

        public override string Name => "火柴记录";

        public byte FirstLevel => 0;

        private readonly object _configLock = new object();
        private MatchRecordConfig _config = new MatchRecordConfig();

        /// <summary>距上次记录火焰蔓延的秒数，用于限流。</summary>
        private float _fireGenerationTimer;

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/火柴记录插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "MatchRecord.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadConfig();
            FireEventManager.AddObject(this);
            Log.Information("[火柴记录] 已加载（仅记录，不阻止）");
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

        private MatchRecordConfig GetConfig()
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
                Log.Error("[火柴记录] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new MatchRecordConfig();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        SaveConfigLocked();
                        return;
                    }
                    MatchRecordConfig loaded = JsonConvert.DeserializeObject<MatchRecordConfig>(File.ReadAllText(ConfigFilePath));
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[火柴记录] 载入配置失败，改用默认值: " + e.Message);
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
                Log.Error("[火柴记录] 保存配置失败: " + e.Message);
            }
        }

        public void Update(float dt)
        {
            _fireGenerationTimer += dt;
        }

        private static string Describe(ComponentMiner componentMiner, MatchRecordConfig config)
        {
            ComponentPlayer player = componentMiner == null ? null : componentMiner.ComponentPlayer;
            if (player == null || player.PlayerData == null)
            {
                return "[未绑定玩家的点火]";
            }
            string account = "?";
            if (player.PlayerData.Client != null)
            {
                account = player.PlayerData.Client.CommunityAccountId;
            }
            // 昵称属于个人信息，配置为 false 时只留社区 ID。
            string name = config.IncludePlayerName && player.PlayerData.Name != null ? player.PlayerData.Name : "?";
            return "[" + account + "]" + name;
        }

        /// <summary>
        /// 朝一个方向点火。旧版同名方法只实现到两个，这里保持"默认放行"。
        /// </summary>
        public bool Fire(Ray3 ray, ComponentMiner componentMiner)
        {
            MatchRecordConfig config = GetConfig();
            if (config.LogFire && ray != null)
            {
                Vector3 position = ray.Position;
                Vector3 direction = ray.Direction;
                Log.Information(string.Format("[火柴记录] {0} 在 ({1},{2},{3}) 朝 ({4},{5},{6}) 点燃火焰",
                    Describe(componentMiner, config),
                    MathUtils.Round(position.X * 10f) / 10f,
                    MathUtils.Round(position.Y * 10f) / 10f,
                    MathUtils.Round(position.Z * 10f) / 10f,
                    MathUtils.Round(direction.X * 100f) / 100f,
                    MathUtils.Round(direction.Y * 100f) / 100f,
                    MathUtils.Round(direction.Z * 100f) / 100f));
            }
            return !config.BlockFire;
        }

        /// <summary>
        /// 对某个格子点火。
        /// </summary>
        public bool FireTerrain(CellFace cellFace, ComponentMiner componentMiner)
        {
            MatchRecordConfig config = GetConfig();
            if (config.LogFireTerrain && cellFace != null)
            {
                Log.Information(string.Format("[火柴记录] {0} 点燃了 ({1},{2},{3}) 的方块",
                    Describe(componentMiner, config),
                    cellFace.X, cellFace.Y, cellFace.Z));
            }
            return !config.BlockFire;
        }

        /// <summary>
        /// 火焰蔓延判定。新版接口新增的方法，旧版没有实现 —— 在新版编译不通过。
        /// 返回 false 可以阻止这一格起火。
        /// </summary>
        public bool OnFireGeneration(int x, int y, int z, ref float spreadability)
        {
            MatchRecordConfig config = GetConfig();
            if (config.LogFireGeneration && _fireGenerationTimer >= config.FireGenerationLogInterval)
            {
                _fireGenerationTimer = 0f;
                Log.Information("[火柴记录] 火焰蔓延至 (" + x + "," + y + "," + z + ")，蔓延度 " + MathUtils.Round(spreadability * 100f) / 100f);
            }
            return !config.BlockFire;
        }
    }
}