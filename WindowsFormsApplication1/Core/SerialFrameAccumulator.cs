using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:v1.3.54 串口无协议帧累积器（纯字节、无 WinForms 依赖，Tests 直链同源回归）。
    ///   一次解决三件事：分包半帧丢触发、GBK 双字节被切开、以及 "1"/"12" 前缀串跨读取误切。
    ///   语义（前缀锚定精确匹配，绝不做子串切分，避免"帧头+触发串+帧尾"里的子串误触发）：
    ///   - pending 开头完整等于某已知串 P，且 pending 比 P 长 → 立即产出 P（后面还有字节，P 已确定）；
    ///   - pending 恰等于 P，但 P 同时是某更长已知串 Q 的前缀 → 暂不产出（可能是 Q 的分包前半），
    ///     直到等出 Q、或连续 PrefixWaitMaxPolls 轮无新字节才产出 P（短串 settle，代价约 200ms）；
    ///   - pending 是某更长已知串的真前缀 → 继续等（未凑齐前绝不产出，也就不解码，避开 GBK 半字）；
    ///   - pending 既非任何串前缀、开头也无完整命中 → 未知数据，连续 IdleFlushPolls 轮无新字节后整段产出一帧（交 getData）。
    ///   产出帧后从 pending 移除对应字节，循环处理（支持一次读入多帧）。超 MaxFrame 硬截断并整段产出。
    /// </summary>
    public sealed class SerialFrameAccumulator
    {
        public const int MaxFrame = 1024;
        public const int IdleFlushPolls = 2;        // 未知数据：连续 2 轮无新字节即整段产出
        public const int PrefixWaitMaxPolls = 20;   // 前缀/短串 settle：连续 20 轮无新字节(≈200ms)后产出短串

        private readonly List<byte> _pending = new List<byte>();
        private int _idlePolls;

        public void Reset() { _pending.Clear(); _idlePolls = 0; }
        public int PendingCount { get { return _pending.Count; } }

        /// <summary>
        /// 投入本轮读到的字节（readCount 可为 0 表示本轮无新字节，用于空闲判定），返回本轮可派发的完整帧。
        /// patterns 为已知串（方案载图串 ∪ 相机 triggerZifu）的 GB2312 字节集合。
        /// </summary>
        public List<byte[]> Append(int readCount, byte[] readBuf, List<byte[]> patterns)
        {
            var frames = new List<byte[]>();
            if (readCount > 0)
            {
                int take = readCount;
                if (_pending.Count + take > MaxFrame)
                    take = Math.Max(0, MaxFrame - _pending.Count); // 硬上限：超出部分本轮不追加，下面按溢出处置
                for (int k = 0; k < take; k++) _pending.Add(readBuf[k]);
                _idlePolls = 0;
            }
            else
            {
                _idlePolls++;
            }

            while (_pending.Count > 0)
            {
                byte[] hit = LongestCompleteMatch(patterns);
                bool strictPrefix = IsStrictPrefixOfAny(patterns);

                if (hit != null)
                {
                    // 短串恰好吃满 pending，且它仍是某更长串的前缀 → 未 settle 则等待，避免 "1"/"12" 跨读取误切
                    if (hit.Length == _pending.Count && strictPrefix && _idlePolls < PrefixWaitMaxPolls)
                        break;
                    byte[] frame = _pending.GetRange(0, hit.Length).ToArray();
                    _pending.RemoveRange(0, hit.Length);
                    frames.Add(frame);
                    continue;
                }

                // 无完整命中：pending 是更长串的真前缀 → 等分包（未 settle、未溢出）
                if (strictPrefix && _pending.Count < MaxFrame && _idlePolls < PrefixWaitMaxPolls)
                    break;

                // 未知数据或 settle/溢出：整段产出一帧
                if (_idlePolls >= IdleFlushPolls || _pending.Count >= MaxFrame || (strictPrefix && _idlePolls >= PrefixWaitMaxPolls))
                {
                    byte[] frame = _pending.ToArray();
                    _pending.Clear();
                    frames.Add(frame);
                }
                break;
            }
            return frames;
        }

        private byte[] LongestCompleteMatch(List<byte[]> patterns)
        {
            byte[] best = null;
            for (int p = 0; p < patterns.Count; p++)
            {
                byte[] pat = patterns[p];
                if (pat == null || pat.Length == 0 || pat.Length > _pending.Count) continue;
                if (StartsWith(pat) && (best == null || pat.Length > best.Length)) best = pat;
            }
            return best;
        }

        private bool IsStrictPrefixOfAny(List<byte[]> patterns)
        {
            for (int p = 0; p < patterns.Count; p++)
            {
                byte[] pat = patterns[p];
                if (pat != null && pat.Length > _pending.Count && PatStartsWithPending(pat)) return true;
            }
            return false;
        }

        private bool StartsWith(byte[] pat)
        {
            for (int i = 0; i < pat.Length; i++)
                if (_pending[i] != pat[i]) return false;
            return true;
        }

        private bool PatStartsWithPending(byte[] pat)
        {
            for (int i = 0; i < _pending.Count; i++)
                if (pat[i] != _pending[i]) return false;
            return true;
        }
    }
}
