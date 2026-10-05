// 清理插件 —— 定期清理过多的掉落物与生物。
//
// 迁移自旧版 .NET Framework 插件（清理插件.cs），适配到新版 Survivalcraft API。
//
// 相比旧版修掉的三个问题：
//   1. ⚠️⚠️ 旧版在掉落物超阈值时直接 m_pickables.Clear() —— 把世界里所有掉落物一次性清空，
//      玩家刚捡起/刚存下的东西会凭空消失。这里改成"只清理放置时间超过阈值的掉落物"，
//      刚掉出来的东西一律保留。可用 PickablesMaxAgeSeconds 配置（<=0 = 关闭该清理）。
//   2. ⚠️ 旧版用 Parameters["Frequency"] 直接取值，配置文件缺任何一个键就每帧抛
//      KeyNotFoundException 刷爆日志。这里改用强类型配置类 + 默认值兜底。
//   3. 旧版在 foreach 里 new Random() 判 50%（每次都是同一个种子序列，且实际存活率不是 50%）。
//      这里用 System.Random 单实例，并把比例做成可配置。
//
// 互斥提醒：本插件与"无动物插件"、"动物生成条件修改"共用 ICreatureSpawnEventHandle，
// 且后两者会清空生物刷新表。若生物表被清空，本插件统计到的生物数会恒为 0。

using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Game;
using Game.NetWork;
using Game.Server;
using Game.Server.Event;
using GameEntitySystem;
using Newtonsoft.Json;

namespace ClearEntitiesPlugin
{
    public sealed class ClearConfig
    {
        /// <summary>巡检间隔（游戏分钟）。</summary>
        public float Frequency { get; set; } = 5f;

        /// <summary>超过多少个掉落物时打一条警告。</summary>
        public int PickablesWarnCount { get; set; } = 100;

        /// <summary>超过多少个掉落物时触发清理。</summary>
        public int PickablesClearCount { get; set; } = 1000;

        /// <summary>
        /// 掉落物存活时长上限（游戏秒）。只有"放置时间早于 当前时间 - 该值"的掉落物才会被清理。
        /// <= 0 表示不按时间清理（此时只在超阈值时清最旧的一批）。
        /// </summary>
        public float PickablesMaxAgeSeconds { get; set; } = 300f;

        /// <summary>超过多少只生物时打一条警告。</summary>
        public int CreaturesWarnCount { get; set; } = 30;

        /// <summary>超过多少只生物时触发清理。</summary>
        public int CreaturesClearCount { get; set; } = 100;

        /// <summary>清理生物时保留的比例（0~1）。1 = 不清理任何生物。</summary>
        public float CreaturesKeepRatio { get; set; } = 0.5f;

        /// <summary>清理时是否在聊天栏提示。</summary>
        public bool AddMessage { get; set; } = true;

        /// <summary>警告日志是否每次都打（false = 每周期最多一条）。</summary>
        public bool LogWarnings { get; set; } = true;
    }

    public sealed class ClearEntitiesPlugin : ServerPlugin, ICreatureSpawnEventHandle
    {
        public override int Version => 10000;

        public override string Name => "生物和掉落物清理";

        public byte FirstLevel => 0;

        private readonly object _configLock = new object();
        private ClearConfig _config = new ClearConfig();

        /// <summary>复用同一个随机源，避免每只生物 new 一次导致的分布偏差。
        /// ⚠️ 必须写成 System.Random —— Engine 与 Game 里各有一个 Random 类，直接写 Random 会 CS0104 歧义。</summary>
        private readonly System.Random _random = new System.Random();

        private SubsystemPickables _subsystemPickables;
        private SubsystemGameWidgets _subsystemGameWidgets;
        private SubsystemGameInfo _subsystemGameInfo;

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/清理插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "Clear.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadConfig();
            CreatureSpawnEventManager.AddObject(this);
            Log.Information("[清理] 已加载，巡检间隔 " + GetConfig().Frequency + " 游戏分钟，掉落物超时 "
                + GetConfig().PickablesMaxAgeSeconds + " 游戏秒");
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

        private ClearConfig GetConfig()
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
                Log.Error("[清理] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new ClearConfig();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        SaveConfigLocked();
                        return;
                    }
                    ClearConfig loaded = JsonConvert.DeserializeObject<ClearConfig>(File.ReadAllText(ConfigFilePath));
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[清理] 载入配置失败，改用默认值: " + e.Message);
                }
                Sanitize(_config);
            }
        }

        /// <summary>把非法/缺失的值拉回可用区间（旧版缺键会直接抛异常）。</summary>
        private static void Sanitize(ClearConfig config)
        {
            if (config.Frequency <= 0f)
            {
                config.Frequency = 5f;
            }
            if (config.PickablesWarnCount < 0)
            {
                config.PickablesWarnCount = 0;
            }
            if (config.PickablesClearCount < 0)
            {
                config.PickablesClearCount = 0;
            }
            if (config.CreaturesWarnCount < 0)
            {
                config.CreaturesWarnCount = 0;
            }
            if (config.CreaturesClearCount < 0)
            {
                config.CreaturesClearCount = 0;
            }
            if (config.CreaturesKeepRatio < 0f)
            {
                config.CreaturesKeepRatio = 0f;
            }
            if (config.CreaturesKeepRatio > 1f)
            {
                config.CreaturesKeepRatio = 1f;
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
                Log.Error("[清理] 保存配置失败: " + e.Message);
            }
        }

        /// <summary>
        /// 定期巡检。挂在这个接口上只是为了拿到 Update 的 tick。
        /// </summary>
        public bool Update(SubsystemCreatureSpawn subsystemCreatureSpawn, float dt)
        {
            ClearConfig config = GetConfig();
            if (subsystemCreatureSpawn == null || subsystemCreatureSpawn.m_subsystemTime == null)
            {
                return true;
            }
            if (!subsystemCreatureSpawn.m_subsystemTime.PeriodicGameTimeEvent(config.Frequency, 0f))
            {
                return true;
            }

            if (_subsystemPickables == null)
            {
                _subsystemPickables = subsystemCreatureSpawn.Project.FindSubsystem<SubsystemPickables>();
            }
            if (_subsystemGameWidgets == null)
            {
                _subsystemGameWidgets = subsystemCreatureSpawn.Project.FindSubsystem<SubsystemGameWidgets>();
            }
            if (_subsystemGameInfo == null)
            {
                _subsystemGameInfo = subsystemCreatureSpawn.Project.FindSubsystem<SubsystemGameInfo>();
            }

            CleanPickables(config);
            CleanCreatures(subsystemCreatureSpawn, config);
            return true;
        }

        /// <summary>
        /// 清理超时掉落物。旧版在这里 Clear() 整个列表，会把玩家刚掉出来的东西一起清掉。
        /// </summary>
        private void CleanPickables(ClearConfig config)
        {
            if (_subsystemPickables == null || config.PickablesClearCount <= 0)
            {
                return;
            }

            // Pickables 是 Engine.ReadOnlyList（struct），不能与 null 比较，也不该当 List 改。
            Engine.ReadOnlyList<Pickable> pickables = _subsystemPickables.Pickables;
            int total = pickables.Count;
            if (total <= config.PickablesClearCount)
            {
                if (total > config.PickablesWarnCount && config.LogWarnings)
                {
                    Log.Warning("[清理] 当前掉落物数量 " + total + "，超过警告阈值 " + config.PickablesWarnCount);
                }
                return;
            }

            // 游戏内累计时间（秒）。Pickable.CreationTime 与它同一时间基准。
            double now = _subsystemGameInfo != null ? _subsystemGameInfo.TotalElapsedGameTime : 0.0;
            int removed = 0;
            int target = total - config.PickablesClearCount;

            for (int i = pickables.Count - 1; i >= 0 && removed < target; i--)
            {
                Pickable pickable = pickables[i];
                if (pickable == null)
                {
                    continue;
                }
                // 配置了存活上限时，只清理放置时间足够久的老掉落物。
                if (config.PickablesMaxAgeSeconds > 0f)
                {
                    double age = now - pickable.CreationTime;
                    if (age < config.PickablesMaxAgeSeconds)
                    {
                        // 还没到时限，跳过（继续往更早的找）。
                        continue;
                    }
                }
                // 标记删除：由核心在自己的 Update 里真正移除，不直接改 m_pickables。
                pickable.ToRemove = true;
                removed++;
            }

            if (removed > 0)
            {
                Log.Information("[清理] 掉落物总数 " + total + " 超过阈值 " + config.PickablesClearCount
                    + "，已标记清理 " + removed + " 个（保留 " + (total - removed) + " 个较新掉落物）");
                Broadcast("掉落物数量过多，已清理 " + removed + " 个过期掉落物");
            }
        }

        /// <summary>生物超额时按配置比例随机移除（不含玩家实体）。</summary>
        private void CleanCreatures(SubsystemCreatureSpawn subsystemCreatureSpawn, ClearConfig config)
        {
            if (config.CreaturesClearCount <= 0 || config.CreaturesKeepRatio >= 1f)
            {
                return;
            }

            List<Entity> creatures = new List<Entity>();
            foreach (Entity entity in subsystemCreatureSpawn.Project.Entities)
            {
                if (entity != null
                    && entity.FindComponent<ComponentCreature>() != null
                    && entity.FindComponent<ComponentPlayer>() == null)
                {
                    creatures.Add(entity);
                }
            }

            int total = creatures.Count;
            if (total <= config.CreaturesClearCount)
            {
                if (total > config.CreaturesWarnCount && config.LogWarnings)
                {
                    Log.Warning("[清理] 当前生物数量 " + total + "，超过警告阈值 " + config.CreaturesWarnCount);
                }
                return;
            }

            // 保留比例之外的数量才是要清掉的。
            int keep = (int)(total * config.CreaturesKeepRatio);
            int target = total - keep;
            if (target <= 0)
            {
                return;
            }

            // 打乱后取前 target 个移除，避免每只都独立判定导致实际比例偏离配置。
            for (int i = creatures.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                Entity tmp = creatures[i];
                creatures[i] = creatures[j];
                creatures[j] = tmp;
            }

            int removed = 0;
            for (int i = 0; i < target; i++)
            {
                Entity entity = creatures[i];
                // Entity 上没有 IsDisposed / m_isEmpty；判定"实体仍在世界里"用 IsAddedToProject。
                if (entity == null || !entity.IsAddedToProject)
                {
                    continue;
                }
                subsystemCreatureSpawn.Project.RemoveEntity(entity, true);
                removed++;
            }

            if (removed > 0)
            {
                Log.Information("[清理] 生物总数 " + total + " 超过阈值 " + config.CreaturesClearCount
                    + "，已清理 " + removed + " 只（保留比例 " + config.CreaturesKeepRatio + "）");
                Broadcast("生物数量过多，已清理 " + removed + " 只生物");
            }
        }

        private void Broadcast(string text)
        {
            ClearConfig config = GetConfig();
            if (!config.AddMessage || _subsystemGameWidgets == null)
            {
                return;
            }
            if (CommonLib.WorkType != WorkType.Server)
            {
                return;
            }
            _subsystemGameWidgets.AddMessage(text);
        }

        public void InitCreatureTypes(SubsystemCreatureSpawn subsystemCreatureSpawn, List<SubsystemCreatureSpawn.CreatureType> creatureTypes)
        {
            // 不接管生物刷新表。
        }

        public void OnEntityAdded(SubsystemCreatureSpawn subsystemCreatureSpawn, Entity entity)
        {
        }

        public void OnEntityRemoved(SubsystemCreatureSpawn subsystemCreatureSpawn, Entity entity)
        {
        }

        public void OnPlayerSpawned(PlayerData playerData, Entity playerEntity, Vector3 position)
        {
        }
    }
}