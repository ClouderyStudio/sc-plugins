using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ScWebPanel
{
    /// <summary>
    /// 性能采样。**核心没有 TPS / MSPT**（`PerformanceManager` 只有渲染帧时间，服务端还未必准），
    /// 所以这里自己量：
    ///
    /// - 在模块每帧 <c>Update(dt)</c> 里读 `SubsystemGameInfo.TotalElapsedGameTimeDelta`，
    ///   它就是这一帧主循环推进的游戏时间（秒）。TPS = 1 / 这个值。
    /// - MSPT（每 tick 毫秒数）同样由它换算：`delta * 1000`。
    ///   注意这是"主循环一步推进了多少游戏时间"，**不是**"服务端处理一帧花了多少真实时间"。
    ///   在正常情况下两者近似（服务端不追赶时 dt≈真实帧间隔）；服务端一旦卡顿，dt 会变大，
    ///   于是 TPS 下降、MSPT 上升 —— 这正是我们要观察的信号。
    ///
    /// 只保留最近一段时间的样本，供前端画曲线。
    /// </summary>
    public sealed class WebPanelMetrics
    {
        /// <summary>保留多少秒的采样（前端画 1 分钟曲线足够）。</summary>
        private const int WindowSeconds = 120;

        /// <summary>采样间隔（秒）。每秒一条，曲线不至于太密。</summary>
        private const double SampleIntervalSeconds = 1.0;

        private readonly object _lock = new object();
        private readonly Queue<Sample> _samples = new Queue<Sample>();

        private double _accumulatedSeconds;
        private double _accumulatedTicks;

        /// <summary>平滑后的 TPS / MSPT（最近若干秒的平均）。瞬时值抖得没法看。</summary>
        private double _tps = 20.0;
        private double _mspt = 50.0;

        /// <summary>历史峰值，页面用它显示"最近最差是多少"。</summary>
        private double _minTps = 20.0;
        private double _maxMspt = 0.0;

        private readonly Stopwatch _uptime = Stopwatch.StartNew();

        private struct Sample
        {
            public double Time;      // 相对启动的秒数
            public double Tps;
            public double Mspt;
        }

        /// <summary>每帧调用（主线程）。</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;

            lock (_lock)
            {
                // dt 异常大通常是断点/暂停/存档卡顿，直接跳过这一帧，别让曲线出现离谱的尖刺
                if (dt > 5.0f) return;

                _accumulatedSeconds += dt;
                _accumulatedTicks += 1.0;

                if (_accumulatedSeconds < SampleIntervalSeconds) return;

                double ticks = _accumulatedTicks;
                double seconds = _accumulatedSeconds;
                _accumulatedSeconds = 0.0;
                _accumulatedTicks = 0.0;

                double tps = ticks / seconds;
                double mspt = seconds * 1000.0 / ticks;

                // 移动平均：0.3 新值 + 0.7 旧值，兼顾灵敏与平稳
                _tps = _tps * 0.7 + tps * 0.3;
                _mspt = _mspt * 0.7 + mspt * 0.3;

                if (_tps < _minTps) _minTps = _tps;
                if (_mspt > _maxMspt) _maxMspt = _mspt;

                _samples.Enqueue(new Sample { Time = _uptime.Elapsed.TotalSeconds, Tps = Math.Round(tps, 2), Mspt = Math.Round(mspt, 2) });
                while (_samples.Count > WindowSeconds) _samples.Dequeue();
            }
        }

        /// <summary>把累计的帧数清掉（存档/读档后游戏时间会跳，不清会产生一个假尖刺）。</summary>
        public void ResetAccumulator()
        {
            lock (_lock)
            {
                _accumulatedSeconds = 0.0;
                _accumulatedTicks = 0.0;
            }
        }

        public double Tps
        {
            get { lock (_lock) return Math.Round(_tps, 2); }
        }

        public double Mspt
        {
            get { lock (_lock) return Math.Round(_mspt, 2); }
        }

        public double MinTps
        {
            get { lock (_lock) return Math.Round(_minTps, 2); }
        }

        public double MaxMspt
        {
            get { lock (_lock) return Math.Round(_maxMspt, 2); }
        }

        public string ToJson()
        {
            var sb = new StringBuilder(2048);
            lock (_lock)
            {
                sb.Append("{\"success\":true");
                sb.Append(",\"tps\":").Append(Math.Round(_tps, 2));
                sb.Append(",\"mspt\":").Append(Math.Round(_mspt, 2));
                sb.Append(",\"minTps\":").Append(Math.Round(_minTps, 2));
                sb.Append(",\"maxMspt\":").Append(Math.Round(_maxMspt, 2));
                sb.Append(",\"uptimeSeconds\":").Append((long)_uptime.Elapsed.TotalSeconds);
                sb.Append(",\"samples\":[");

                bool first = true;
                foreach (var sample in _samples)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"t\":").Append(Math.Round(sample.Time, 1));
                    sb.Append(",\"tps\":").Append(sample.Tps);
                    sb.Append(",\"mspt\":").Append(sample.Mspt);
                    sb.Append('}');
                }

                sb.Append("]}");
            }
            return sb.ToString();
        }

        /// <summary>进程真实运行时长（本模块从启动到现在）。</summary>
        public TimeSpan Uptime => _uptime.Elapsed;
    }
}
