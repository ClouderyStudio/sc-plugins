using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Engine;
using Game;
using Game.Server;
using Game.Server.Plugins;

namespace NewPersonalCode
{
    /// <summary>
    /// 宿主插件。整个 DLL 里**唯一**的 <see cref="ServerPlugin"/>
    /// （<c>ServerManager.LoadPluginsDll</c> 会实例化 DLL 里每一个 ServerPlugin 子类，
    /// 所以其余逻辑都做成普通静态类，不再继承 ServerPlugin）。
    ///
    /// 职责：生命周期（Initialize / Load / Save / Update）、配置、
    /// 把 <see cref="ConnectionPasswordStore"/> 与 <see cref="ConnectionPasswordGate"/> 串起来。
    /// </summary>
    public sealed class NewPersonalCodePlugin : ServerPlugin
    {
        public override string Name => "新个人进服密码插件";

        public override int Version => 10000;

        public byte FirstLevel => 0;

        public static NewPersonalCodePlugin Instance { get; private set; }

        /// <summary>配置。重载时整体替换，取一次引用即为快照。</summary>
        public ConnectionPasswordConfig Config { get; private set; } = new ConnectionPasswordConfig();

        /// <summary>插件数据目录。</summary>
        public string DataDir => Storage.GetSystemPath("app:/Plugins/新个人进服密码插件");

        /// <summary>配置文件路径。</summary>
        public string ConfigPath => Path.Combine(DataDir, "进服密码配置.json");

        /// <summary>
        /// 首次运行落一份带注释的默认配置。核心不会自动生成插件配置，
        /// 而"配置长什么样"只能靠翻文档的话，装插件的人第一件事就是卡住。
        /// </summary>
        public override void Initialize()
        {
            Instance = this;
            try
            {
                Directory.CreateDirectory(DataDir);
                bool fresh = !File.Exists(ConfigPath);
                Config = ConfigStore.Load(ConfigPath);
                if (fresh)
                {
                    ConfigStore.Save(ConfigPath, Config);
                }

                ConnectionPasswordStore.SetHost(this);
                ConnectionPasswordGate.Install();

                Log.Information($"[新进服密码] 已加载；配置文件 {ConfigPath}");

                if (fresh)
                {
                    Log.Information("[新进服密码] 首次运行，已生成默认配置。它**默认是关的** —— "
                                    + "把 Enabled 改成 true 再重启，进服密码才会生效。");
                    return;
                }

                if (!Config.Enabled)
                {
                    Log.Information("[新进服密码] 未启用（Enabled=false）：客户端不弹密码框，进服不校验。");
                    return;
                }

                ConnectionPasswordGate.ApplyPatches();

                int personal = ConnectionPasswordStore.PersonalCount();
                string fallback = string.IsNullOrEmpty(Config.DefaultPassword)
                    ? "未设（没设个人密码的人直接放行 = 可选保护模式）"
                    : "已设（没设个人密码的人用它进门）";
                Log.Information($"[新进服密码] 已启用：个人密码 {personal} 条，默认密码{fallback}，"
                                + $"长度限制 {Config.MinLength}-{Config.MaxLength}，"
                                + $"连错本设备 {Config.MaxFailuresBeforeKick} 次 / 同IP {Config.MaxFailuresBeforeIp} 次断线"
                                + "（5 分钟未再失败即清零）。");
            }
            catch (Exception ex)
            {
                // 这一步失败必须让服务器照常起来，只是本插件不可用
                Log.Error("[新进服密码] 初始化失败，本次不可用：" + ex);
            }
        }

        public override void Load()
        {
            try
            {
                Config = ConfigStore.Load(ConfigPath);
                ConnectionPasswordStore.ReloadFromDisk();
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] Load 异常：" + ex);
            }
        }

        public override void Save()
        {
            try
            {
                ConfigStore.Save(ConfigPath, Config);
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] Save 异常：" + ex);
            }
        }

        /// <summary>
        /// 每帧回调。只做一件事：每 5 分钟打一行进出统计，
        /// 让服主不用翻日志也能看见"这半小时拦了几个人"。
        /// </summary>
        public override void Update(float dt)
        {
            try
            {
                if (Config == null || !Config.LogStats) return;
                if (!ConnectionPasswordGate.DueForReport()) return;
                ConnectionPasswordGate.ReportStats();
            }
            catch (Exception ex)
            {
                Log.Error("[新进服密码] Update 异常：" + ex.Message);
            }
        }

        /// <summary>重载配置（命令层改完字段后调它落盘）。</summary>
        public void SaveConfig()
        {
            Config.Clamp();
            ConfigStore.Save(ConfigPath, Config);
        }
    }
}
