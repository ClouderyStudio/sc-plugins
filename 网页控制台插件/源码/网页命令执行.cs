using System;
using System.Collections.Generic;
using System.Text;
using Game.Server;

namespace ScWebPanel
{
    /// <summary>
    /// 执行一条服务端命令并把回显抓回来。
    ///
    /// 做法：核心允许替换 <c>CmdManager.GlobalCommandOutput</c>，而 `isTerminal: true` 的命令
    /// 会把结果写进这个 output。所以执行期间临时换成我们自己的收集器，执行完**必须还原**
    /// （放在 finally 里）——否则控制台里以后所有命令的回显都会消失。
    ///
    /// ⚠ 只能在主线程调用。命令会改地形/实体/玩家状态，在别的线程跑会崩服。
    /// ⚠ 调用方必须先过 <see cref="WebPanelCommandPolicy"/>。`isTerminal: true` 会跳过核心的
    ///   权限检查，把命令交给它是"我们已经自己判过了"的前提下的动作。
    /// </summary>
    public static class WebPanelCommandRunner
    {
        private const int MaxOutputChars = 32 * 1024;

        /// <summary>执行命令。返回回显文本；error 非空表示执行失败。</summary>
        public static string Execute(string command, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(command))
            {
                error = "命令不能为空";
                return null;
            }

            ICommandOutput previous = CmdManager.GlobalCommandOutput;
            var collector = new CollectingOutput();

            try
            {
                CmdManager.GlobalCommandOutput = collector;

                // netNode 传 null 时 HandleMessage 内部会退回 CommonLib.Net；
                // client 传 null 表示"不是某个玩家在执行"（终端语义）。
                bool handled = CmdManager.HandleMessage(
                    username: "WebConsole",
                    netNode: null,
                    client: null,
                    message: command.StartsWith("/", StringComparison.Ordinal) ? command : "/" + command,
                    isTerminal: true);

                if (!handled)
                {
                    error = "命令没有匹配到任何已注册的命令（可能名字写错，或该命令被配置关掉了）";
                    return null;
                }

                string text = collector.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 有些命令是"做了事但不出声"，这不是错误
                    text = "（命令已执行，无输出）";
                }
                return text;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                CmdManager.GlobalCommandOutput = previous;
            }
        }

        /// <summary>把命令回显收集到内存。核心默认的 BasicOutput 是直接写 Console，收不到。</summary>
        private sealed class CollectingOutput : ICommandOutput
        {
            private readonly StringBuilder _buffer = new StringBuilder(512);
            private readonly object _lock = new object();

            public void WriteConsole(string message)
            {
                Append(message, newline: false);
            }

            public void WriteConsole(IEnumerable<ConsolePart> parts)
            {
                AppendParts(parts, newline: false);
            }

            public void WriteConsoleLine(string message)
            {
                Append(message, newline: true);
            }

            public void WriteConsoleLine(IEnumerable<ConsolePart> parts)
            {
                AppendParts(parts, newline: true);
            }

            private void Append(string message, bool newline)
            {
                if (message == null) return;
                lock (_lock)
                {
                    if (_buffer.Length > MaxOutputChars) return;
                    _buffer.Append(message);
                    if (newline) _buffer.Append('\n');
                }
            }

            private void AppendParts(IEnumerable<ConsolePart> parts, bool newline)
            {
                if (parts == null) return;
                lock (_lock)
                {
                    if (_buffer.Length > MaxOutputChars) return;
                    foreach (var part in parts)
                    {
                        // 颜色信息在网页端另有渲染方式，这里只要文本
                        _buffer.Append(part.Text);
                    }
                    if (newline) _buffer.Append('\n');
                }
            }

            public override string ToString()
            {
                lock (_lock)
                {
                    string text = _buffer.ToString().TrimEnd('\n', '\r');
                    if (text.Length > MaxOutputChars)
                    {
                        text = text.Substring(0, MaxOutputChars) + "\n…（输出过长已截断）";
                    }
                    return text;
                }
            }
        }
    }
}
