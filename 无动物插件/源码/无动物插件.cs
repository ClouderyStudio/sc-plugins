// 无动物插件 —— 清空生物刷新表，让世界里不生成任何动物。
//
// 迁移自旧版 .NET Framework 插件（无动物插件.cs），适配到新版 Survivalcraft API。
// ICreatureSpawnEventHandle 的 5 个方法在新版签名一致。
//
// 相比旧版的修正：
//   1. 旧版插件名叫"无动物测试"（作者自己标成实验品），且 Initialize() 里无条件清空生物表 ——
//      装上去就关掉全服生物，没有开关、没有提示。这里改成配置驱动：
//      Enabled 默认 false，必须显式打开才会清空；每次状态变化打日志。
//   2. 旧版把生物表清空后没有恢复途径（改配置要重启，且没有日志说明当前状态）。这里每次
//      InitCreatureTypes 都会记录当前是否生效，运维随时知道世界处于什么状态。
//
// ⚠️ 互斥提醒（重要）：
//   本插件与「动物生成条件修改」共用 ICreatureSpawnEventHandle.InitCreatureTypes 这个钩子，
//   两者都调用 creatureTypes.Clear()。同时启用时谁后执行谁生效 ——
//   若本插件后执行，会把「动物生成条件修改」精心配置的整张生物表清成空。
//   两者只能二选一。

using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Game;
using Game.Server;
using Game.Server.Event;
using GameEntitySystem;
using Newtonsoft.Json;

namespace NoAnimalPlugin
{
    public sealed class NoAnimalConfig
    {
        /// <summary>
        /// 是否禁用全部动物。默认 false —— 需要显式打开才会生效，
        /// 避免"装上插件 = 全服动物消失"这种不可逆的意外。
        /// </summary>
        public bool Enabled { get; set; } = false;
    }

    public sealed class NoAnimalPlugin : ServerPlugin, ICreatureSpawnEventHandle
    {
        public override int Version => 10000;

        public override string Name => "无动物插件";

        public byte FirstLevel => 0;

        private readonly object _configLock = new object();
        private NoAnimalConfig _config = new NoAnimalConfig();

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/无动物插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "NoAnimal.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadConfig();
            CreatureSpawnEventManager.AddObject(this);
            Log.Warning("[无动物] 已加载。当前状态: " + (GetConfig().Enabled
                ? "已禁用（世界不会生成任何动物）"
                : "未禁用（正常生成动物）。要禁用请把 NoAnimal.json 的 Enabled 设为 true 后重启/重载"));
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

        private NoAnimalConfig GetConfig()
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
                Log.Error("[无动物] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new NoAnimalConfig();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        SaveConfigLocked();
                        return;
                    }
                    NoAnimalConfig loaded = JsonConvert.DeserializeObject<NoAnimalConfig>(File.ReadAllText(ConfigFilePath));
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[无动物] 载入配置失败，按未启用处理: " + e.Message);
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
                Log.Error("[无动物] 保存配置失败: " + e.Message);
            }
        }

        /// <summary>当前是否处于"禁用动物"状态。</summary>
        public static bool IsDisabled
        {
            get
            {
                NoAnimalPlugin plugin = Instance;
                return plugin != null && plugin.GetConfig().Enabled;
            }
        }

        public static NoAnimalPlugin Instance { get; private set; }

        /// <summary>巡检 tick。本插件不处理它，但接口要求必须实现。</summary>
        public bool Update(SubsystemCreatureSpawn subsystemCreatureSpawn, float dt)
        {
            return true;
        }

        /// <summary>
        /// 生物刷新表初始化。只有 Enabled 为 true 时才清空。
        /// </summary>
        public void InitCreatureTypes(SubsystemCreatureSpawn subsystemCreatureSpawn, List<SubsystemCreatureSpawn.CreatureType> creatureTypes)
        {
            if (creatureTypes == null)
            {
                return;
            }
            if (!GetConfig().Enabled)
            {
                // 不接管：保留原生生物表。
                return;
            }
            int removed = creatureTypes.Count;
            creatureTypes.Clear();
            Log.Warning("[无动物] 已清空生物刷新表（原 " + removed + " 种生物不再生成）。"
                + "如需恢复：把 NoAnimal.json 的 Enabled 设为 false 并重启服务器。");
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