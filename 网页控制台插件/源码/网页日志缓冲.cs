using System;
using System.Collections.Generic;
using System.Text;
using Engine;

namespace ScWebPanel
{
    /// <summary>
    /// 日志环形缓冲。挂到 <c>Engine.Log</c> 的 sink 上（<c>Log.AddLogSink</c>），
    /// 终端页读它的内容。
    ///
    /// 为什么用环形缓冲而不是列表：日志量不小（存档、审计、玩家进出都在写），
    /// 无上限的列表跑几天就能吃掉几百 MB。这里只保留最近 N 行。
    ///
    /// 线程安全：<c>Log.Write</c> 会在**任意线程**调用 <see cref="Log"/>，而 HTTP 线程会读，
    /// 所以内部必须自己加锁。
    /// </summary>
    public sealed class WebPanelLogSink : ILogSink
    {
        private readonly object _lock = new object();
        private readonly Queue<WebPanelLogLine> _lines = new Queue<WebPanelLogLine>();

        /// <summary>单调递增的序号。前端拿它做增量拉取（since=N 只取比 N 新的）。</summary>
        private long _nextId = 1;

        /// <summary>保留多少行。超出丢最旧的。</summary>
        public int Capacity { get; set; } = 2000;

        /// <summary>
        /// 有新日志进来时触发（SSE 推送靠它唤醒泵线程）。
        /// ⚠️ 会在**任意线程**触发（谁调 Log.Write 就是谁的线程），订阅方自己保证线程安全，
        /// 且回调里别做重活——这里只置一个信号。
        /// </summary>
        public event Action LogAdded;

        public void Log(LogType type, string message)
        {
            if (message == null) return;

            lock (_lock)
            {
                _lines.Enqueue(new WebPanelLogLine
                {
                    Id = _nextId++,
                    TimeUtc = DateTime.UtcNow,
                    Level = DescribeLevel(type),
                    Text = message
                });

                // 一次多清一点，避免每行都做一次出队
                int overflow = _lines.Count - Capacity;
                while (overflow-- > 0 && _lines.Count > 0) _lines.Dequeue();
            }

            // 在锁外触发：订阅方可能在回调里读本对象，锁内触发会死锁
            try
            {
                LogAdded?.Invoke();
            }
            catch
            {
                // 订阅方的异常绝不能影响写日志
            }
        }

        private static string DescribeLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Warning: return "warn";
                case LogType.Information: return "info";
                case LogType.Verbose: return "verbose";
                default: return "debug";
            }
        }

        /// <summary>取比 <paramref name="sinceId"/> 新的所有行，序列化成 JSON。</summary>
        public string ToJson(long sinceId)
        {
            return ToJson(sinceId, out _);
        }

        /// <summary>
        /// 取比 <paramref name="sinceId"/> 新的所有行，并把"当时最新的序号"一并返回。
        /// SSE 推流必须用这个重载：游标要在**同一次加锁**里读出来，
        /// 否则写完数据后又进了一条日志，下一次就会把它跳过（漏行）。
        /// </summary>
        public string ToJson(long sinceId, out long latestId)
        {
            var sb = new StringBuilder(4096);
            long latest;
            int count;

            lock (_lock)
            {
                latest = _nextId - 1;
                count = _lines.Count;

                sb.Append("{\"success\":true,\"latest\":");
                sb.Append(latest);
                sb.Append(",\"count\":");
                sb.Append(count);
                sb.Append(",\"lines\":[");

                bool first = true;
                foreach (var line in _lines)
                {
                    if (line.Id <= sinceId) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":");
                    sb.Append(line.Id);
                    sb.Append(",\"time\":");
                    sb.Append(WebPanelJson.Quote(line.TimeUtc.ToLocalTime().ToString("HH:mm:ss")));
                    sb.Append(",\"level\":");
                    sb.Append(WebPanelJson.Quote(line.Level));
                    sb.Append(",\"text\":");
                    sb.Append(WebPanelJson.Quote(line.Text));
                    sb.Append('}');
                }

                sb.Append("]}");
            }

            latestId = latest;
            return sb.ToString();
        }

        /// <summary>当前最新序号（前端第一次拉取时用它定位起点）。</summary>
        public long LatestId
        {
            get
            {
                lock (_lock) return _nextId - 1;
            }
        }
    }

    public struct WebPanelLogLine
    {
        public long Id;

        public DateTime TimeUtc;

        public string Level;

        public string Text;
    }
}
