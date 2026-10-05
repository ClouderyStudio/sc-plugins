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
}
