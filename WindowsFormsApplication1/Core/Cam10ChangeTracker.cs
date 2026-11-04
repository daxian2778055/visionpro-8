using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R44 相机10方案切换防呆：同一通道只在“载图串解析出的方案路径”变化时才允许再次切换。
    ///   背景：Omron/ModbusTCP/ModbusRTU 的轮询每个周期都会派发 cam10 事件；原实现只与全局
    ///   path_1 比较，两个 PLC 通道各保持不同方案串时会 A/B 无限互切。本类按通道记住上次已
    ///   受理/已同步的路径，重复值直接忽略，从根上切断互切环。线程安全，供三条通讯线程并发调用。
    /// </summary>
    public sealed class Cam10ChangeTracker
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _lastPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 返回 true=该通道的方案路径相对上次已变化，可以发起切换；false=重复值，应忽略。
        /// 第一次调用（无历史）视为变化，保证启动后 PLC 当前方案能生效。
        /// 调用方契约：返回 true 后若切换未受理（闩被占/前置失败）或排空中止，必须 Forget(source)，
        ///   否则同一方案串在本会话内不会再重试（R46 修复的静默丢失路径）。
        /// </summary>
        public bool ShouldSwitch(string source, string requestedPath)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(requestedPath))
                return false;

            lock (_sync)
            {
                string last;
                if (_lastPath.TryGetValue(source, out last) &&
                    string.Equals(last, requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _lastPath[source] = requestedPath;
                return true;
            }
        }

        /// <summary>
        /// 记录“该通道当前值已经与全局方案一致”，不触发切换。
        /// 用于 path_1 == 本通道 lujing 的分支，避免其它通道切走后本通道再切回来。
        /// </summary>
        public void MarkHandled(string source, string path)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(path))
                return;

            lock (_sync)
            {
                _lastPath[source] = path;
            }
        }

        /// <summary>忘记某通道的历史（切换被受理后失败/中止时可允许其下轮重试）。</summary>
        public void Forget(string source)
        {
            if (string.IsNullOrEmpty(source))
                return;

            lock (_sync)
            {
                _lastPath.Remove(source);
            }
        }

        /// <summary>清空全部通道历史（用于测试或整机重置）。</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _lastPath.Clear();
            }
        }
    }
}
