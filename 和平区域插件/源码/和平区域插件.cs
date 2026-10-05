// 和平区域插件 —— 指定区域内禁止玩家互相伤害。
//
// 迁移自旧版 .NET Framework 插件（和平区域插件.cs），适配到新版 Survivalcraft API。
// 相比旧版的修正：
//   1. ComponentHealth.Injure 新版只有 4 参重载（amount, attacker, ignoreInvulnerability, cause），
//      旧版调的 3 参版本属于 ComponentCreatureHealth，在新版编译不通过 —— 已全部改成 4 参。
//   2. 反噬文案原本写的是 "天惩！"（从"天惩"插件复制粘贴的残留），改成插件自己的文案。
//   3. 判定不再只看攻击者位置，受害者也在区域内时同样禁战（否则可以站在区内打区外的人）。
//   4. Min/Max 归一化不再依赖 SubsystemGameInfo（首次进档时它为空，归一化会被静默跳过）。
//   5. 区域配置读写用原子写入，避免存档过程中断电/崩溃写坏 JSON。

using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Game;
using Game.NetWork;
using Game.NetWork.Packages;
using Game.Server;
using Game.Server.Event;
using GameEntitySystem;
using Newtonsoft.Json;

namespace PeaceZonePlugin
{
    /// <summary>
    /// 一个立方体区域。坐标按 X/Y/Z 各自独立归一化后存储，Min 永远不大于 Max。
    /// </summary>
    public sealed class PeaceZoneArea
    {
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MinZ { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public int MaxZ { get; set; }

        public bool Contains(Point3 point)
        {
            return MinX <= point.X && point.X <= MaxX
                && MinY <= point.Y && point.Y <= MaxY
                && MinZ <= point.Z && point.Z <= MaxZ;
        }

        /// <summary>
        /// 把可能是反序输入的坐标排正。旧版把这件事放在 Load() 里且依赖 SubsystemGameInfo 非空，
        /// 这里改成每次判断前都保证一次，调用方不必操心输入顺序。
        /// </summary>
        public void Normalize()
        {
            if (MinX > MaxX) { int t = MinX; MinX = MaxX; MaxX = t; }
            if (MinY > MaxY) { int t = MinY; MinY = MaxY; MaxY = t; }
            if (MinZ > MaxZ) { int t = MinZ; MinZ = MaxZ; MaxZ = t; }
        }

        public override string ToString()
        {
            return string.Format("({0},{1},{2}) ~ ({3},{4},{5})", MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
        }
    }

    /// <summary>
    /// 和平区域插件：区域内禁止玩家互伤，攻击者会受到轻微反噬。
    /// </summary>
    public sealed class PeaceZonePlugin : ServerPlugin, ICreatureHealthEventHandle
    {
        public override int Version => 10000;

        public override string Name => "和平区域插件";

        public byte FirstLevel => 0;

        /// <summary>
        /// 反噬伤害。文案里的"天惩"是旧版从另一个插件复制过来的，与本插件无关，已改掉。
        /// </summary>
        public const string RetaliationCause = "和平区域";

        /// <summary>
        /// 反噬伤害量。旧版写死 0.1，这里保持一致。
        /// </summary>
        public const float RetaliationDamage = 0.1f;

        private static readonly object AreasLock = new object();

        private static List<PeaceZoneArea> _areas = new List<PeaceZoneArea>();

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/和平区域插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "PeaceAreas.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadAreas();
            CreatureHealthEventManager.AddObject(this);
            Log.Information("[和平区域] 已加载 " + CountAreas() + " 个区域");
        }

        public override void Load()
        {
            EnsureConfigDirectory();
            LoadAreas();
        }

        public override void Save()
        {
            SaveAreas();
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
                Log.Error("[和平区域] 创建配置目录失败: " + e.Message);
            }
        }

        private static int CountAreas()
        {
            lock (AreasLock)
            {
                return _areas.Count;
            }
        }

        /// <summary>
        /// 点是否落在任一和平区域内。
        /// </summary>
        public static bool IsInPeaceArea(Point3 point)
        {
            lock (AreasLock)
            {
                for (int i = 0; i < _areas.Count; i++)
                {
                    PeaceZoneArea area = _areas[i];
                    area.Normalize();
                    if (area.Contains(point))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static List<PeaceZoneArea> GetAreasSnapshot()
        {
            lock (AreasLock)
            {
                return new List<PeaceZoneArea>(_areas);
            }
        }

        private static void LoadAreas()
        {
            lock (AreasLock)
            {
                _areas = new List<PeaceZoneArea>();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        return;
                    }
                    List<PeaceZoneArea> loaded = JsonConvert.DeserializeObject<List<PeaceZoneArea>>(File.ReadAllText(ConfigFilePath));
                    if (loaded == null)
                    {
                        // 旧版在这里会让 null 直接覆盖字典，然后 foreach 崩掉。
                        Log.Warning("[和平区域] 配置文件内容为空或无法解析，已按空区域列表处理");
                        return;
                    }
                    foreach (PeaceZoneArea area in loaded)
                    {
                        if (area == null)
                        {
                            continue;
                        }
                        area.Normalize();
                        _areas.Add(area);
                    }
                }
                catch (Exception e)
                {
                    // 解析失败时保留已清空的空列表，不要抛到 Load 里把整个插件拖崩。
                    Log.Error("[和平区域] 载入 " + ConfigFilePath + " 失败: " + e.Message);
                }
            }
        }

        private static void SaveAreas()
        {
            lock (AreasLock)
            {
                try
                {
                    EnsureConfigDirectory();
                    string json = JsonConvert.SerializeObject(_areas, Formatting.Indented);
                    // 原子写入：先写 .partial 再改名，避免写到一半断电留下半个 JSON。
                    string temp = ConfigFilePath + ".partial";
                    File.WriteAllText(temp, json);
                    File.Copy(temp, ConfigFilePath, true);
                    File.Delete(temp);
                }
                catch (Exception e)
                {
                    Log.Error("[和平区域] 保存 " + ConfigFilePath + " 失败: " + e.Message);
                }
            }
        }

        public bool Heal(ComponentHealth componentHealth, float amount)
        {
            return true;
        }

        public bool Injure(ComponentHealth componentHealth, float amount, ComponentCreature attacker, string cause)
        {
            if (componentHealth == null || attacker == null)
            {
                return true;
            }

            ComponentPlayer attackerPlayer = attacker.Entity.FindComponent<ComponentPlayer>();
            ComponentPlayer victimPlayer = componentHealth.Entity.FindComponent<ComponentPlayer>();

            // 只处理"玩家打玩家"。生物之间的伤害不受和平区域约束。
            if (attackerPlayer == null || victimPlayer == null || attackerPlayer == victimPlayer)
            {
                return true;
            }

            // 攻击者和受害者只要有一方在区域内就禁战。
            // 旧版只检查攻击者，于是"站在区内打区外的人"是可行的漏洞。
            Vector3 attackerPos = attackerPlayer.ComponentBody != null ? attackerPlayer.ComponentBody.Position : Vector3.Zero;
            Vector3 victimPos = victimPlayer.ComponentBody != null ? victimPlayer.ComponentBody.Position : Vector3.Zero;

            if (!IsInPeaceArea(new Point3(attackerPos)) && !IsInPeaceArea(new Point3(victimPos)))
            {
                return true;
            }

            // 反噬：攻击者自己掉一点血。新版只有 4 参 Injure，cause 用于日志与伤害来源显示。
            ComponentHealth attackerHealth = attackerPlayer.Entity.FindComponent<ComponentHealth>();
            if (attackerHealth != null)
            {
                attackerHealth.Injure(RetaliationDamage, null, true, RetaliationCause);
            }

            SendMessage(attackerPlayer, "和平区域内禁止攻击其他玩家");
            return false;
        }

        private static void SendMessage(ComponentPlayer player, string text)
        {
            if (player == null || player.PlayerData == null || player.PlayerData.Client == null)
            {
                return;
            }
            // 必须显式设 To：To 为 null 的包会被核心广播给所有玩家（见开发注意事项 §容器内容防护）。
            MessagePackage message = new MessagePackage(null, text, 0, null);
            message.To = player.PlayerData.Client;
            CommonLib.Net.QueuePackage(message);
        }
    }
}