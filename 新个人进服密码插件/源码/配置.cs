using System;
using System.IO;
using Engine;
using Newtonsoft.Json;

namespace NewPersonalCode
{
    /// <summary>
    /// 插件配置。整个插件只有这一个可调结构，落盘在
    /// <c>Plugins/新个人进服密码插件/进服密码配置.json</c>。
    ///
    /// 与旧版「个人进服密码插件」最大的区别：旧版是"服主给每个玩家设一个密码"，
    /// 本插件的密码是**玩家自己设**的（<c>/pwd set</c>），服主只需要决定"要不要开"、
    /// "要不要设一个全服默认密码"。
    /// </summary>
    public sealed class ConnectionPasswordConfig
    {
        /// <summary>
        /// 总开关。关掉时插件完全不接管：客户端不弹密码框、进服不校验，
        /// 服务器行为与核心原生完全一致。
        ///
        /// 默认 <c>false</c>——照抄基础插件的"保守默认"：安全增强不应该在装上的那一刻
        /// 就改变全服的登录流程，否则服主可能把自己锁在门外。
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 全服默认密码，**明文**写在这里（服主自己填）。支持 <c>sha256:&lt;64位十六进制&gt;</c> 只放哈希。
        ///
        /// 三种组合的语义：
        /// - 留空（默认）＝ <b>可选个人保护模式</b>：没设个人密码的玩家直接放行，设了的才拦。
        ///   适合"只想防 token 被盗用"的服——给在意的人加一道锁，不打扰其他人。
        /// - 非空 ＝ <b>全员门槛模式</b>：没设个人密码的人必须用这个默认密码进门。
        ///   适合"地址泄露了想临时挂个口令"的场景。
        /// - 无论哪种，**个人密码永远优先**：设了自己密码的人不会被默认密码挡下。
        /// </summary>
        public string DefaultPassword { get; set; } = "";

        /// <summary>
        /// 最高权限密码（<b>可选</b>）。设了之后 <c>/pwd default</c> 与 <c>/pwd status</c>
        /// 两条命令必须先用 <c>/pw &lt;密码&gt;</c> 验证通过才能用。
        ///
        /// 为什么需要它：管理员身份不能信 <c>PlayerData.ServerManager</c>——客户端可以在
        /// 进服的 <c>PlayerDataPackage</c> 里**自称管理员**，也可能被外挂通过自定义包写 true。
        /// 唯一客户端碰不到的秘密就是密码。留空时该闸门关闭（退回"看 ServerManager"的旧模型），
        /// 单机服 / 自建服不需要关心它。
        /// </summary>
        public string AdminPassword { get; set; } = "";

        /// <summary>一次 <c>/pw</c> 验证的有效期（分钟）。0 = 本次连接内一直有效（断线即失效）。</summary>
        public int AdminSessionMinutes { get; set; } = 120;

        /// <summary>
        /// 同一个玩家（按 GUID 计）连续输错几次就断开**这一条**连接。
        /// 默认 <b>5</b>：3 次太紧，玩家手滑两下就被踢体验很差，而真撞库的人试 5 次也照样进不来。
        /// 0 = 不踢，只回错误提示。
        /// </summary>
        public int MaxFailuresBeforeKick { get; set; } = 5;

        /// <summary>
        /// 同一个 <b>IP</b> 在窗口内累计错几次就断开。
        /// 默认 <b>8</b>。0 = 不按 IP 限。
        ///
        /// 为什么必须同时按 IP 计：扫码 / 第三方入口进来的客户端 GUID 全是
        /// <c>00000000-0000-0000-0000-000000000001</c>——**所有人共用同一个 key**，
        /// 只按 GUID 计数会互相连坐（一个人输错就把同入口的人一起踢）。
        /// </summary>
        public int MaxFailuresBeforeIp { get; set; } = 8;

        /// <summary>密码最短长度。低于这个值拒绝设置。</summary>
        public int MinLength { get; set; } = 4;

        /// <summary>
        /// 密码最长长度。客户端的密码框（<c>TextBoxDialog</c>）硬编码上限是 16，
        /// 写大了玩家也输不进来，所以这里封顶 16。
        /// </summary>
        public int MaxLength { get; set; } = 16;

        /// <summary>是否每 5 分钟往日志打一行"放行 / 拒绝"统计。</summary>
        public bool LogStats { get; set; } = true;

        public void Clamp()
        {
            if (DefaultPassword == null) DefaultPassword = "";
            if (AdminPassword == null) AdminPassword = "";

            if (AdminSessionMinutes < 0) AdminSessionMinutes = 0;

            if (MaxFailuresBeforeKick < 0) MaxFailuresBeforeKick = 0;
            if (MaxFailuresBeforeKick > 100) MaxFailuresBeforeKick = 100;
            if (MaxFailuresBeforeIp < 0) MaxFailuresBeforeIp = 0;
            if (MaxFailuresBeforeIp > 1000) MaxFailuresBeforeIp = 1000;

            // 客户端密码框是 TextBoxDialog(..., 16, ...)，硬上限 16，写大了也输不进来
            if (MaxLength < 1) MaxLength = 1;
            if (MaxLength > 16) MaxLength = 16;
            if (MinLength < 1) MinLength = 1;
            if (MinLength > MaxLength) MinLength = MaxLength;
        }
    }

    /// <summary>
    /// 配置读写。写盘走临时文件再 Move，避免写一半断电留下半截 JSON
    /// （核心读 JSON 失败会整套退回默认值，那等于把服主配的东西全丢了）。
    /// </summary>
    internal static class ConfigStore
    {
        private static readonly object _sync = new object();

        public static ConnectionPasswordConfig Load(string path)
        {
            lock (_sync)
            {
                ConnectionPasswordConfig config = null;
                try
                {
                    if (File.Exists(path))
                    {
                        config = JsonConvert.DeserializeObject<ConnectionPasswordConfig>(File.ReadAllText(path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[新进服密码] 配置读取失败，本次用默认值：" + ex.Message);
                }

                if (config == null) config = new ConnectionPasswordConfig();
                config.Clamp();
                return config;
            }
        }

        public static void Save(string path, ConnectionPasswordConfig config)
        {
            lock (_sync)
            {
                try
                {
                    config.Clamp();
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    string temp = path + ".partial";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(config, Formatting.Indented));
                    File.Move(temp, path, true);
                }
                catch (Exception ex)
                {
                    Log.Error("[新进服密码] 配置写盘失败：" + ex.Message);
                }
            }
        }
    }
}
