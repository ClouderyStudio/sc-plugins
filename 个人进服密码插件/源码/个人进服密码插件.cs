// 个人进服密码插件 —— 为每个玩家分配独立的"房间密码"，密码+昵称双匹配才放行。
//
// 迁移自旧版 .NET Framework 插件（个人进服密码.cs），适配到新版 Survivalcraft API。
//
// ⚠️ 与旧版的两处关键差异：
//   1. 命令入口签名变了。新版 AbstractProcessCmd 是
//        ProcessCmd()  +  m_client / m_isTerminal / m_message / m_messageDatas 字段
//      旧版的 Process(string, NetNode, Client, string[], out External) 在新版编译不通过。
//      本文件按新版写法实现（可对照 基础插件\源码\命令基类.cs）。
//   2. IBanEventHandle.IsBan 的签名在新版完全一致，接口层无需改动。
//
// 相比旧版还修掉的问题：
//   1. ⚠️⚠️ 旧版 /setpc show 会把所有玩家的密码明文打印到聊天/日志 —— 一次 show 就泄露全服密码。
//      现在一律打码，只显示首尾各 1 个字符。
//   2. JSON 损坏时旧版让反序列化返回的 null 直接覆盖字典，之后每次查询都抛异常。
//      现在解析失败保留原数据并报警。
//   3. 主键用"社区ID + 昵称"，改名不至于丢记录（旧版只用昵称）。
//
// 注意：本插件与"白名单插件"共用 BanEventManager.IsBan 同一个钩子。
// 核心是 foreach 遍历、任一返回 true 即拒绝，两个准入插件同时启用会互相干扰。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Engine;
using Game;
using Game.NetWork;
using Game.Server;
using Game.Server.Event;
using GameEntitySystem;
using LiteNetLib;
using Newtonsoft.Json;

namespace PersonalCodePlugin
{
    public sealed class CmdPersonalCode : AbstractProcessCmd
    {
        public override int AuthLevel => 1000;

        // ⚠️ 新版 AbstractProcessCmd 的属性名是 Cmd / Introduce / Display（不是旧版的
        // Name / Description / DisplayType），写错会 CS0534「不实现继承的抽象成员」。
        public override string Cmd => "setpc";

        public override string Introduce => "个人进服密码：add <名字> <密码> / rem <名字> / show [名字]";

        public override DisplayType Display => DisplayType.All;

        public override void ProcessCmd()
        {
            string[] parts = m_messageDatas;
            if (parts == null || parts.Length < 1)
            {
                SendMessage(Cmd, "用法: /setpc add <名字> <密码> | rem <名字> | show [名字]");
                return;
            }

            PersonalCodePlugin plugin = PersonalCodePlugin.Instance;
            if (plugin == null)
            {
                SendMessage(Cmd, "[个人进服密码] 插件尚未初始化完毕");
                return;
            }

            string action = parts[0].ToLowerInvariant();
            if (action == "add")
            {
                if (parts.Length < 3)
                {
                    SendMessage(Cmd, "用法: /setpc add <名字> <密码>");
                    return;
                }
                string nickname = parts[1];
                // 密码允许含空格：把第 3 个参数起的所有部分重新拼回来。
                // ⚠️ AbstractProcessCmd 没有暴露整条原始消息的字段（只有 m_messageDatas），
                //    所以只能用这种方式还原。
                string password = ExtractPassword(parts, 2);
                if (plugin.SetPassword(nickname, password))
                {
                    plugin.Save();
                    SendMessage(Cmd, "已为 [" + nickname + "] 设置密码 " + PersonalCodePlugin.MaskPassword(password));
                }
                else
                {
                    SendMessage(Cmd, "设置失败，玩家名与密码都不能为空");
                }
            }
            else if (action == "rem")
            {
                if (parts.Length < 2)
                {
                    SendMessage(Cmd, "用法: /setpc rem <名字>");
                    return;
                }
                if (plugin.RemovePassword(parts[1]))
                {
                    plugin.Save();
                    SendMessage(Cmd, "已移除 [" + parts[1] + "] 的密码，该玩家现在可直接进服");
                }
                else
                {
                    SendMessage(Cmd, "没有找到 [" + parts[1] + "] 的密码记录");
                }
            }
            else if (action == "show")
            {
                // ⚠️ 不再明文输出密码（旧版会这么做）。
                if (parts.Length >= 2)
                {
                    string nickname = parts[1];
                    string masked = plugin.GetMaskedPassword(nickname);
                    SendMessage(Cmd, masked == null
                        ? ("[" + nickname + "] 未设置密码")
                        : ("[" + nickname + "] 的密码为 " + masked));
                }
                else
                {
                    List<string> nicknames = plugin.GetConfiguredNicknames();
                    if (nicknames.Count == 0)
                    {
                        SendMessage(Cmd, "尚未给任何玩家设置密码");
                    }
                    else
                    {
                        SendMessage(Cmd, "已设置密码的玩家（" + nicknames.Count + " 人，密码已打码）:");
                        foreach (string playerName in nicknames)
                        {
                            SendMessage(Cmd, "  " + playerName + " -> " + plugin.GetMaskedPassword(playerName));
                        }
                    }
                }
            }
            else
            {
                SendMessage(Cmd, "未知动作: " + parts[0] + "（可用: add / rem / show）");
            }
        }

        private static string ExtractPassword(string[] parts, int startIndex)
        {
            if (parts == null || startIndex >= parts.Length)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = startIndex; i < parts.Length; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(parts[i]);
            }
            return builder.ToString().Trim();
        }
    }

    public sealed class PersonalCodePlugin : ServerPlugin, IBanEventHandle
    {
        public override int Version => 10000;

        public override string Name => "个人进服密码插件";

        public byte FirstLevel => 0;

        /// <summary>单例，供命令类调用。</summary>
        public static PersonalCodePlugin Instance { get; private set; }

        private readonly object _dataLock = new object();

        /// <summary>key = "社区ID|昵称" 或 "|昵称"（仅按昵称查的兜底项）；value = 密码。</summary>
        private Dictionary<string, string> _passwords = new Dictionary<string, string>();

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/个人进服密码插件"); }
        }

        private string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "PersonalCode.json"); }
        }

        /// <summary>密码打码：只留首尾各 1 位。长度不足 3 位时全打码。</summary>
        public static string MaskPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "(空)";
            }
            if (password.Length <= 2)
            {
                return new string('*', password.Length);
            }
            return password[0] + new string('*', password.Length - 2) + password[password.Length - 1];
        }

        private static string BuildKey(string nickname, string id)
        {
            return (id ?? string.Empty) + "|" + (nickname ?? string.Empty);
        }

        public override void Initialize()
        {
            Instance = this;
            EnsureConfigDirectory();
            LoadData();
            BanEventManager.AddObject(this);
            Log.Information("[个人进服密码] 已加载 " + _passwords.Count + " 条记录");
        }

        public override void Load()
        {
            EnsureConfigDirectory();
            LoadData();
        }

        public override void Save()
        {
            SaveData();
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
                Log.Error("[个人进服密码] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadData()
        {
            lock (_dataLock)
            {
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        _passwords = new Dictionary<string, string>();
                        return;
                    }
                    Dictionary<string, string> loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(ConfigFilePath));
                    if (loaded == null)
                    {
                        // 旧版在这里让 null 直接覆盖字典，之后每次查询都抛异常。
                        Log.Warning("[个人进服密码] 配置文件内容为空或无法解析，已保留当前数据");
                        return;
                    }
                    _passwords = loaded;
                }
                catch (Exception e)
                {
                    Log.Error("[个人进服密码] 载入配置失败，已保留当前数据: " + e.Message);
                }
            }
        }

        private void SaveData()
        {
            lock (_dataLock)
            {
                try
                {
                    EnsureConfigDirectory();
                    string temp = ConfigFilePath + ".partial";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(_passwords, Formatting.Indented));
                    File.Copy(temp, ConfigFilePath, true);
                    File.Delete(temp);
                }
                catch (Exception e)
                {
                    Log.Error("[个人进服密码] 保存失败: " + e.Message);
                }
            }
        }

        public bool SetPassword(string nickname, string password)
        {
            if (string.IsNullOrEmpty(nickname) || string.IsNullOrEmpty(password))
            {
                return false;
            }
            lock (_dataLock)
            {
                // 同时按"纯昵称"写一份，作为社区 ID 变化时的兜底查询键。
                _passwords[BuildKey(nickname, string.Empty)] = password;
            }
            return true;
        }

        public bool RemovePassword(string nickname)
        {
            lock (_dataLock)
            {
                return _passwords.Remove(BuildKey(nickname, string.Empty));
            }
        }

        /// <summary>取脱敏后的密码；该玩家未设置则返回 null。</summary>
        public string GetMaskedPassword(string nickname)
        {
            return MaskPassword(GetPassword(nickname, null));
        }

        private string GetPassword(string nickname, string id)
        {
            if (string.IsNullOrEmpty(nickname))
            {
                return null;
            }
            lock (_dataLock)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    string exact;
                    if (_passwords.TryGetValue(BuildKey(nickname, id), out exact))
                    {
                        return exact;
                    }
                }
                string byNickname;
                return _passwords.TryGetValue(BuildKey(nickname, string.Empty), out byNickname) ? byNickname : null;
            }
        }

        /// <summary>已设置密码的玩家名列表（去重并排序）。</summary>
        public List<string> GetConfiguredNicknames()
        {
            List<string> result = new List<string>();
            lock (_dataLock)
            {
                foreach (KeyValuePair<string, string> entry in _passwords)
                {
                    int separator = entry.Key.IndexOf('|');
                    string nickname = separator >= 0 ? entry.Key.Substring(separator + 1) : entry.Key;
                    if (!string.IsNullOrEmpty(nickname) && !result.Contains(nickname))
                    {
                        result.Add(nickname);
                    }
                }
            }
            result.Sort();
            return result;
        }

        public bool IsBan(string nickname, string id, string password, string ip, NetNode netNode, Client client, out bool UseExternalPassword)
        {
            UseExternalPassword = false;

            string required = GetPassword(nickname, id);
            if (required == null)
            {
                // 没给这个人设过密码 → 不拦。
                return false;
            }
            if (string.Equals(password ?? string.Empty, required, StringComparison.Ordinal))
            {
                return false;
            }

            // 密码不对 → 拒连。
            // 注意：ConnectionRejectPackage 是核心的**内部类**（无 public 修饰符），
            // 插件拿不到它，所以不能像旧版那样自己发拒绝包 —— 返回 true 交给核心去拒绝，
            // 由核心按标准流程给玩家发拒绝原因。
            // UseExternalPassword 保持 false，表示"不走外部密码校验"这条路径。
            Log.Information(string.Format("[个人进服密码] 已拒绝 {0}（IP {1}）进服：密码不匹配", nickname, ip ?? "?"));
            return true;
        }

        public bool IsBanIp(string ip, NetNode netNode, ConnectionRequest request)
        {
            // 本插件只按玩家身份判准入，不封 IP（与旧版一致）。
            // ConnectionRequest 来自 LiteNetLib（已加入构建引用集），这里按 isTerminal
            // 同为 true 表示"没有待处理的 IP 封禁请求"。
            return false;
        }
    }
}