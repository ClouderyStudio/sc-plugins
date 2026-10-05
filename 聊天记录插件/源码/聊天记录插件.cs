// 聊天记录插件 —— 把全服公共频道聊天落盘，便于事后追溯。
//
// 迁移自旧版 .NET Framework 插件（聊天记录插件.cs），适配到新版 Survivalcraft API。
// IMessageEventHandle.ReceiveMessage 的签名在新版完全一致。
//
// 相比旧版修掉的三个问题：
//   1. ⚠️⚠️ 旧版把**私聊**（type == 2）也明文落盘。私聊属于隐私内容，
//      默认不再记录（RecordPrivateChat 默认 false），需要时显式打开。
//   2. ⚠️ 旧版对正常消息把 External 设为 true —— 在核心的 foreach 遍历里，
//      任意一个 handler 改写 External 都会改变全局聊天处理流程。
//      这里显式把 External 置 false 并原样放行，不干扰其他插件。
//   3. 旧版日志文件无限增长、无轮转。这里按天分文件 + 单文件大小上限 + 保留天数。
//
// 消息类型：0 = 公共，1 = 队伍，2 = 私聊。

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Engine;
using Game;
using Game.NetWork;
using Game.Server;
using Game.Server.Event;
using Newtonsoft.Json;

namespace ChatRecordPlugin
{
    public sealed class ChatRecordConfig
    {
        /// <summary>是否记录公共频道。</summary>
        public bool RecordPublicChat { get; set; } = true;

        /// <summary>是否记录队伍频道。</summary>
        public bool RecordTeamChat { get; set; } = true;

        /// <summary>是否记录私聊。默认关闭 —— 私聊属隐私内容。</summary>
        public bool RecordPrivateChat { get; set; } = false;

        /// <summary>是否记录以 / 开头的命令。</summary>
        public bool RecordCommands { get; set; } = false;

        /// <summary>单个日志文件上限（字节），超过后不再追加。默认 8 MB。</summary>
        public long MaxFileBytes { get; set; } = 8L * 1024L * 1024L;

        /// <summary>日志保留天数，超期的文件在巡检时删除。</summary>
        public int KeepDays { get; set; } = 30;

        /// <summary>是否在游戏内提示玩家"消息会被记录"。</summary>
        public bool AnnounceRetention { get; set; } = true;
    }

    public sealed class ChatRecordPlugin : ServerPlugin, IMessageEventHandle
    {
        public override int Version => 10000;

        public override string Name => "聊天记录插件";

        public byte FirstLevel => 0;

        private const byte TypePublic = 0;
        private const byte TypeTeam = 1;
        private const byte TypePrivate = 2;

        private readonly object _configLock = new object();
        private readonly object _writeLock = new object();

        private ChatRecordConfig _config = new ChatRecordConfig();

        /// <summary>最近一条已记录的消息，用于去重（核心会对同一条消息多次派发）。</summary>
        private string _lastMessage;

        private static string PluginDirectory
        {
            get { return Storage.GetSystemPath("app:/Plugins/聊天记录插件"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(PluginDirectory, "ChatRecord.json"); }
        }

        public override void Initialize()
        {
            EnsureConfigDirectory();
            LoadConfig();
            MessageEventManager.AddObject(this);
            if (_config.AnnounceRetention)
            {
                Log.Information("[聊天记录] 已加载：记录公共/队伍频道，私聊默认不记录；日志按天分文件，保留 "
                    + _config.KeepDays + " 天");
            }
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

        private ChatRecordConfig GetConfig()
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
                Log.Error("[聊天记录] 创建配置目录失败: " + e.Message);
            }
        }

        private void LoadConfig()
        {
            lock (_configLock)
            {
                _config = new ChatRecordConfig();
                try
                {
                    if (!File.Exists(ConfigFilePath))
                    {
                        SaveConfigLocked();
                        return;
                    }
                    ChatRecordConfig loaded = JsonConvert.DeserializeObject<ChatRecordConfig>(File.ReadAllText(ConfigFilePath));
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[聊天记录] 载入配置失败，改用默认值: " + e.Message);
                }
                if (_config.MaxFileBytes <= 0)
                {
                    _config.MaxFileBytes = 8L * 1024L * 1024L;
                }
                if (_config.KeepDays < 1)
                {
                    _config.KeepDays = 1;
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
                Log.Error("[聊天记录] 保存配置失败: " + e.Message);
            }
        }

        private static string ChannelName(byte messageType)
        {
            switch (messageType)
            {
                case TypePublic: return "公";
                case TypeTeam: return "队";
                case TypePrivate: return "私";
                default: return "?";
            }
        }

        private bool ShouldRecord(byte messageType, string message)
        {
            ChatRecordConfig config = GetConfig();
            if (!string.IsNullOrEmpty(message) && message.StartsWith("/", StringComparison.Ordinal))
            {
                return config.RecordCommands;
            }
            switch (messageType)
            {
                case TypePublic: return config.RecordPublicChat;
                case TypeTeam: return config.RecordTeamChat;
                case TypePrivate: return config.RecordPrivateChat;
                default: return false;
            }
        }

        /// <summary>
        /// 聊天消息回调。
        /// </summary>
        /// <param name="playerName">发送者昵称。</param>
        /// <param name="From">发送者连接（社区 ID 从这里取；可能为 null）。</param>
        /// <param name="message">消息内容。</param>
        /// <param name="messageType">0=公共 1=队伍 2=私聊。</param>
        /// <param name="External">
        /// ⚠️ 必须置 false —— 置 true 表示"由外部插件接管这条消息"，
        /// 核心的 foreach 会因此改变全服聊天的处理流程（旧版这里置 true，是个隐患）。
        /// </param>
        public void ReceiveMessage(string playerName, NetNode netNode, Client From, string message, byte messageType, out bool External)
        {
            // 原样放行，不干预聊天本身。
            External = false;

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }
                if (!ShouldRecord(messageType, message))
                {
                    return;
                }

                string accountId = From != null ? From.CommunityAccountId : null;

                // 去重：核心可能对同一条消息派发多次，只记第一条。
                string line = FormatLine(playerName, accountId, message, messageType);
                if (line == _lastMessage)
                {
                    return;
                }
                _lastMessage = line;

                AppendLine(line);
            }
            catch (Exception e)
            {
                // 记录失败绝不能影响聊天本身。
                Log.Error("[聊天记录] 写入失败: " + e.Message);
            }
        }

        private static string FormatLine(string playerName, string accountId, string message, byte messageType)
        {
            // 用 24 小时制（HH:mm:ss）；旧版用 hh 是 12 小时制且不区分 AM/PM。
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string name = string.IsNullOrEmpty(playerName) ? "?" : playerName.Replace('\t', ' ').Replace('\n', ' ');
            string sender = string.IsNullOrEmpty(accountId) ? name : (name + "(" + accountId + ")");
            string body = message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return timestamp + "\t[" + ChannelName(messageType) + "]\t" + sender + "\t" + body;
        }

        private static string CurrentLogPath()
        {
            return Path.Combine(PluginDirectory, "ChatRecord-" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
        }

        private void AppendLine(string line)
        {
            ChatRecordConfig config = GetConfig();
            lock (_writeLock)
            {
                EnsureConfigDirectory();
                string path = CurrentLogPath();

                // 单文件超限就不再追加，避免磁盘被单个文件撑满。
                if (File.Exists(path) && config.MaxFileBytes > 0)
                {
                    FileInfo info = new FileInfo(path);
                    if (info.Length >= config.MaxFileBytes)
                    {
                        Log.Warning("[聊天记录] " + Path.GetFileName(path) + " 已达上限 "
                            + config.MaxFileBytes + " 字节，今日不再记录");
                        return;
                    }
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            PruneOldLogs(config);
        }

        /// <summary>删除超期的日志文件。</summary>
        private static void PruneOldLogs(ChatRecordConfig config)
        {
            try
            {
                if (!Storage.DirectoryExists(PluginDirectory))
                {
                    return;
                }
                DateTime cutoff = DateTime.Now.AddDays(-config.KeepDays);
                foreach (string file in Directory.GetFiles(PluginDirectory, "ChatRecord-*.txt"))
                {
                    try
                    {
                        // 从文件名里的日期判断，避免依赖文件的写入时间。
                        string stamp = Path.GetFileNameWithoutExtension(file);
                        if (stamp.Length < "ChatRecord-yyyy-MM-dd".Length)
                        {
                            continue;
                        }
                        string datePart = stamp.Substring("ChatRecord-".Length);
                        DateTime fileDate;
                        if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out fileDate)
                            && fileDate.Date < cutoff.Date)
                        {
                            File.Delete(file);
                            Log.Information("[聊天记录] 已删除超期日志 " + Path.GetFileName(file));
                        }
                    }
                    catch (Exception inner)
                    {
                        Log.Error("[聊天记录] 清理 " + file + " 失败: " + inner.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[聊天记录] 巡检日志目录失败: " + e.Message);
            }
        }

        public bool EditSignMessage(Point3 point, ComponentPlayer componentPlayer)
        {
            return true;
        }
    }
}