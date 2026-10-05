using System;
using System.Security.Cryptography;
using System.Text;

namespace ScWebPanel
{
    /// <summary>
    /// 网页控制台的密码校验与会话令牌生成。
    ///
    /// 口令支持两种写法（与 <c>AdminAuth.Password</c> 保持一致的约定，便于用户记忆）：
    /// - 明文：直接比对
    /// - <c>sha256:&lt;64位小写十六进制&gt;</c>：只存哈希，配置被看到也不泄露原口令
    ///
    /// ⚠ 比对用**固定时间比较**：普通 <c>string.Equals</c> 会在第一个不同的字符处返回，
    /// 理论上可以被按字节计时爆破。这里不省这点性能。
    /// </summary>
    public static class WebPanelCrypto
    {
        private const string Sha256Prefix = "sha256:";

        /// <summary>校验口令。配置里留空时一律返回 false（面板也不会启动）。</summary>
        public static bool VerifyPassword(string input, string configured)
        {
            if (string.IsNullOrEmpty(configured)) return false;
            if (input == null) input = string.Empty;

            if (configured.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = configured.Substring(Sha256Prefix.Length).Trim().ToLowerInvariant();
                string actual = Sha256Hex(input);
                return FixedTimeEquals(actual, expected);
            }

            return FixedTimeEquals(input, configured);
        }

        /// <summary>sha256 的十六进制小写形式（用于生成配置里要填的哈希）。</summary>
        public static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>生成会话令牌：32 字节 CSPRNG，base64url 编码。</summary>
        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base64Url(bytes);
        }

        /// <summary>等长比较，避免按字符提前返回泄露信息。长度不同直接失败。</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] left = Encoding.UTF8.GetBytes(a);
            byte[] right = Encoding.UTF8.GetBytes(b);
            if (left.Length != right.Length) return false;

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }

    /// <summary>
    /// 登录挑战值（challenge）。
    ///
    /// 解决的问题：<c>MaxLoginFailures</c> 是按 IP 记的，攻击者用代理池轮换 IP 就能无限次撞库；
    /// 而纯口令校验对"直接打 HTTP 的脚本"没有任何阻力。
    ///
    /// 做法很轻：<c>/api/challenge</c> 下发一个短时效随机串，客户端登录时原样回带。
    /// 服务端只校验"这个串是自己刚签发、还没过期、没被用过"，不校验内容本身 ——
    /// 所以它<b>挡不住</b>愿意先取一次挑战的脚本，但能挡住"直接暴力打 /api/login"的哑脚本，
    /// 并让每个令牌变成一次性、限时的。对真正的定向攻击者，它只是抬高了门槛。
    ///
    /// 它<b>不是</b>验证码，也替代不了强口令 —— 真正的防线仍然是密码强度。
    /// </summary>
    public static class WebPanelChallenge
    {
        private sealed class Entry
        {
            public string Value;
            public DateTime ExpiresUtc;
        }

        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.Dictionary<string, Entry> Pending =
            new System.Collections.Generic.Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>挑战值有效期（秒）。给够脚本正常往返的时间，又不至于长期可重放。</summary>
        public const int LifetimeSeconds = 120;

        /// <summary>
        /// 字典容量上限。/api/challenge 是无口令接口，如果不限速，攻击者（或单纯一个死循环脚本）
        /// 可以在 LifetimeSeconds 内塞进几十万条记录把内存吃光。这里做个硬闸：
        /// 到达上限就<b>先清掉全部过期条目再判断</b>，仍然满则拒绝签发（返回 null，调用方按"拿不到挑战值"处理）。
        /// 正常情况下离这个上限远得很，只有遭遇滥用时才会触发。
        /// </summary>
        public const int MaxPendingEntries = 4096;

        /// <summary>签发一个新挑战，归属 <paramref name="ownerKey"/>（通常是 remoteIp + UA 摘要）。字典已满时返回 null。</summary>
        public static string Issue(string ownerKey)
        {
            string token = WebPanelCrypto.NewToken();
            lock (Gate)
            {
                Prune();
                // 硬闸：防止无口令接口被刷爆内存。
                if (Pending.Count >= MaxPendingEntries) return null;
                Pending[token] = new Entry { Value = ownerKey, ExpiresUtc = DateTime.UtcNow.AddSeconds(LifetimeSeconds) };
            }
            return token;
        }

        /// <summary>
        /// 校验并<b>消费</b>一个挑战值。成功返回 true；<paramref name="ownerKey"/> 不匹配、
        /// 已过期、已用过、根本不存在，一律 false。
        /// </summary>
        public static bool Consume(string challenge, string ownerKey)
        {
            if (string.IsNullOrWhiteSpace(challenge)) return false;
            lock (Gate)
            {
                Prune();
                Entry entry;
                if (!Pending.TryGetValue(challenge.Trim(), out entry)) return false;
                // 一次性：不管这次校验结果如何都作废，避免拿同一个值反复试口令。
                //（这就是"已用过"的全部实现 —— 从字典里摘掉后不可能再取到第二次，所以不需要额外的 Used 标记。）
                Pending.Remove(challenge.Trim());
                if (entry.ExpiresUtc <= DateTime.UtcNow) return false;
                if (!string.IsNullOrEmpty(entry.Value) && !string.Equals(entry.Value, ownerKey, StringComparison.Ordinal)) return false;
                return true;
            }
        }

        /// <summary>清掉过期条目，防止字典无限增长（常驻进程，必须自己收）。</summary>
        private static void Prune()
        {
            DateTime now = DateTime.UtcNow;
            var dead = new System.Collections.Generic.List<string>();
            foreach (var kv in Pending)
            {
                if (kv.Value.ExpiresUtc <= now) dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) Pending.Remove(dead[i]);
        }
    }
}
