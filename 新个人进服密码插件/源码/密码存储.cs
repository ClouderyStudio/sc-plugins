using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Engine;
using Newtonsoft.Json;

namespace NewPersonalCode
{
    /// <summary>
    /// 个人密码的存储层。
    ///
    /// 设计要点：
    /// - key 用**玩家 GUID**（32 位无横线小写）。GUID 是核心在握手包里解析出来的身份，
    ///   比昵称可靠（改名不丢记录、同名不互串）。
    /// - 只存 <b>PBKDF2-SHA256</b> 哈希，盐每次改密码重新随机，**明文永不落盘**。
    /// - 存盘用"临时文件 + Move"，避免写一半断电留下半截 JSON 把全服密码弄丢。
    /// - 读坏了**宁可当空库也不要用半截数据挡人**，而且必须打日志说清楚——
    ///   否则服主会以为密码都还在，实际已经全部失效。
    /// </summary>
    internal static class ConnectionPasswordStore
    {
        private static readonly object _sync = new object();

        /// <summary>GUID(N 格式，小写无横线) → 密码条目。</summary>
        private static readonly Dictionary<string, PasswordEntry> _entries =
            new Dictionary<string, PasswordEntry>(StringComparer.OrdinalIgnoreCase);

        private static NewPersonalCodePlugin _host;
        private static bool _loaded;

        /// <summary>一条个人密码。只存哈希，明文永不落盘。</summary>
        [Serializable]
        internal sealed class PasswordEntry
        {
            /// <summary>PBKDF2 的盐（base64）。每次改密码重新随机。</summary>
            public string Salt { get; set; } = "";

            /// <summary>PBKDF2-SHA256 的结果（base64）。</summary>
            public string Hash { get; set; } = "";

            /// <summary>这个密码是什么时候设的（UTC，调试用）。</summary>
            public string CreatedUtc { get; set; } = "";
        }

        /// <summary>存盘结构。</summary>
        [Serializable]
        private sealed class PasswordFile
        {
            /// <summary>
            /// GUID(N) → 条目。刻意用"字典的键"而不是数组：GUID 天然唯一，
            /// 不会出现两条同人的记录；将来要加字段也不用改文件结构。
            /// </summary>
            public Dictionary<string, PasswordEntry> Players { get; set; } =
                new Dictionary<string, PasswordEntry>(StringComparer.OrdinalIgnoreCase);

            /// <summary>版本号，留给将来迁移用。</summary>
            public int Format { get; set; } = 1;
        }

        internal static void SetHost(NewPersonalCodePlugin host)
        {
            _host = host;
        }

        internal static string DataPath
        {
            get
            {
                string dir = _host?.DataDir;
                if (string.IsNullOrEmpty(dir)) dir = Storage.GetSystemPath("app:/Plugins/新个人进服密码插件");
                return Path.Combine(dir, "进服密码.json");
            }
        }

        /// <summary>把 GUID 转成字典 key。统一在这里转，避免各处大小写不一致。</summary>
        internal static string KeyOf(Guid guid) => guid.ToString("N");

        private static void EnsureLoaded()
        {
            lock (_sync)
            {
                if (_loaded) return;
            }
            LoadFromDisk();
        }

        internal static void LoadFromDisk()
        {
            string path;
            try { path = DataPath; }
            catch { return; }

            lock (_sync)
            {
                _loaded = true;
                _entries.Clear();
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                PasswordFile file = JsonConvert.DeserializeObject<PasswordFile>(text);
                if (file?.Players == null) return;

                int skipped = 0;
                lock (_sync)
                {
                    foreach (KeyValuePair<string, PasswordEntry> kv in file.Players)
                    {
                        if (kv.Value == null) { skipped++; continue; }
                        if (string.IsNullOrEmpty(kv.Value.Hash) || string.IsNullOrEmpty(kv.Value.Salt))
                        {
                            skipped++;
                            continue;
                        }
                        _entries[kv.Key] = kv.Value;
                    }
                }
                Log.Information($"[新进服密码] 已读入 {_entries.Count} 条个人密码（{path}）"
                                + (skipped > 0 ? $"，跳过 {skipped} 条空记录" : ""));
            }
            catch (Exception ex)
            {
                // 读坏了宁可当空库，也不要用半截数据去挡人；且必须说清楚
                Log.Error($"[新进服密码] 读取 {path} 失败，本次按《没有个人密码》处理：{ex.Message}");
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                string path = DataPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                PasswordFile file = new PasswordFile();
                lock (_sync)
                {
                    foreach (KeyValuePair<string, PasswordEntry> kv in _entries) file.Players[kv.Key] = kv.Value;
                }

                string temp = path + ".partial";
                File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented), Encoding.UTF8);
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] 写盘失败：" + ex.Message);
            }
        }

        // ==========================================
        // 哈希
        // ==========================================

        /// <summary>PBKDF2-SHA256 20 万轮——比单次 SHA256 慢得多，暴力猜的代价被放大。</summary>
        private static void Derive(string password, string saltBase64, out string hashBase64)
        {
            byte[] salt = string.IsNullOrEmpty(saltBase64)
                ? new byte[16]
                : Convert.FromBase64String(saltBase64);
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 200000, HashAlgorithmName.SHA256))
            {
                hashBase64 = Convert.ToBase64String(pbkdf2.GetBytes(32));
            }
        }

        /// <summary>定长比较，抹掉"比较耗时随首个不同字节变化"这种侧信道。</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            int diff = 0;
            for (int i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        internal static bool Verify(PasswordEntry entry, string input)
        {
            try
            {
                Derive(input, entry.Salt, out string hash);
                return FixedTimeEquals(hash, entry.Hash);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 默认密码的比较。默认密码是**明文存配置**的（服主自己填），所以按"支持明文或
        /// sha256:"的老规矩来，和常见的服务端口令配置保持一致，服主不用换习惯。
        /// </summary>
        internal static bool VerifyDefault(string configured, string input)
        {
            if (input == null) return false;
            string want = (configured ?? string.Empty).Trim();
            if (want.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                using (var sha = SHA256.Create())
                {
                    string hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "");
                    return hex.Equals(want.Substring(7).Trim(), StringComparison.OrdinalIgnoreCase);
                }
            }
            return FixedTimeEquals(want, input);
        }

        // ==========================================
        // 查询 / 修改
        // ==========================================

        /// <summary>取某人的密码条目；没设过返回 null。</summary>
        internal static PasswordEntry Find(Guid guid)
        {
            EnsureLoaded();
            lock (_sync)
            {
                PasswordEntry entry;
                return _entries.TryGetValue(KeyOf(guid), out entry) ? entry : null;
            }
        }

        /// <summary>某人有没有设个人密码。</summary>
        internal static bool Has(Guid guid) => Find(guid) != null;

        /// <summary>已设个人密码的人数。</summary>
        internal static int PersonalCount()
        {
            EnsureLoaded();
            lock (_sync) { return _entries.Count; }
        }

        /// <summary>
        /// 设 / 改个人密码。返回 <c>null</c> 表示成功，否则是**给玩家看的**失败原因。
        /// 长度、空格等校验都在这里做，命令层只负责把错误转给玩家。
        /// </summary>
        internal static string Set(Guid guid, string password, string confirm)
        {
            ConnectionPasswordConfig cfg = _host?.Config;
            if (cfg == null) return "服务器配置异常。";

            if (password == null) password = "";
            if (confirm == null) confirm = "";

            if (password != confirm) return "两次输入的密码不一致。";
            if (password.Length < cfg.MinLength) return $"密码太短，至少 {cfg.MinLength} 个字。";
            if (password.Length > cfg.MaxLength) return $"密码太长，最多 {cfg.MaxLength} 个字（客户端密码框就那么宽）。";
            if (password.IndexOf(' ') >= 0) return "密码里不能有空格 —— 客户端密码框只认一串连续字符。";

            EnsureLoaded();

            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            Derive(password, Convert.ToBase64String(salt), out string hash);

            var entry = new PasswordEntry
            {
                Salt = Convert.ToBase64String(salt),
                Hash = hash,
                CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            lock (_sync) { _entries[KeyOf(guid)] = entry; }
            SaveToDisk();
            return null;
        }

        /// <summary>删掉个人密码。返回是否真的删掉了一条。</summary>
        internal static bool Clear(Guid guid)
        {
            EnsureLoaded();
            bool removed;
            lock (_sync) { removed = _entries.Remove(KeyOf(guid)); }
            if (removed) SaveToDisk();
            return removed;
        }

        /// <summary>
        /// 自己测一次密码对不对（给 <c>/pwd ＜密码＞</c> 的"查看"用）。
        /// 先比对个人密码，没有个人密码时再比对全服默认密码。
        /// </summary>
        internal static bool CheckOwn(Guid guid, string input)
        {
            PasswordEntry entry = Find(guid);
            if (entry != null) return Verify(entry, input);

            ConnectionPasswordConfig cfg = _host?.Config;
            if (cfg == null || string.IsNullOrEmpty(cfg.DefaultPassword)) return false;
            return VerifyDefault(cfg.DefaultPassword, input);
        }

        /// <summary>这个人当前"该用哪个口径进门"：true = 个人密码，false = 默认密码。</summary>
        internal static bool OwnScopeIsPersonal(Guid guid) => Has(guid);

        /// <summary>全服默认密码是否可用（给状态文案用）。</summary>
        internal static bool DefaultUsable()
        {
            ConnectionPasswordConfig cfg = _host?.Config;
            return cfg != null && !string.IsNullOrEmpty(cfg.DefaultPassword);
        }

        /// <summary>重载配置后重读玩家库（文件可能被手改过）。</summary>
        internal static void ReloadFromDisk()
        {
            lock (_sync)
            {
                _loaded = false;
                _entries.Clear();
            }
            LoadFromDisk();
        }
    }
}
