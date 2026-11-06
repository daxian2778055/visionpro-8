using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:v1.3.54 串口无协议帧累积器（纯字节、无 WinForms 依赖，Tests 直链同源回归）。
    ///   一次解决三件事：分包半帧丢触发、GBK 双字节被切开、以及 "1"/"12" 前缀串跨读取误切。
    ///   语义（前缀锚定精确匹配，绝不做子串切分，避免"帧头+触发串+帧尾"里的子串误触发）：
    ///   - pending 开头完整等于某已知串 P，且 pending 已比 P 长（后面还有字节，P 已确定完整）→ 立即产出 P；
    ///   - pending 以某已知串 P 开头（P 完整命中），但 pending 同时仍是某更长已知串 Q 的前缀（如 "12" 之于 "1"/"123"）
    ///     → 暂不产出（P 可能是 Q 的分包前半），直到等出 Q、或连续 PrefixWaitMaxPolls 轮无新字节才产出 P（短串 settle，代价约 200ms）；
    ///   - pending 是某更长已知串的真前缀（尚未凑齐任何完整串）→ 继续等（未凑齐前绝不产出，也就不解码，避开 GBK 半字）；
    ///   - pending 既非任何串前缀、开头也无完整命中 → 不可能再成长为已知串，当前轮直接整段产出一帧
    ///     （交 getData，恢复"未知报文逐读透传"的旧语义；前缀锚定下等待没有收益）。</summary>
    public sealed class SerialFrameAccumulator
    {
        public const int MaxFrame = 1024;
        public const int PrefixWaitMaxPolls = 20;   // 前缀/短串 settle：连续 20 轮无新字节(≈200ms)后产出短串

        private readonly List<byte> _pending = new List<byte>();
        private int _idlePolls;

        public void Reset() { _pending.Clear(); _idlePolls = 0; }
        public int PendingCount { get { return _pending.Count; } }

        /// <summary>
        /// 投入本轮读到的字节（readCount 可为 0 表示本轮无新字节，用于空闲判定），返回本轮可派发的完整帧。
        /// patterns 为已知串（方案载图串 ∪ 相机 triggerZifu）的 GB2312 字节集合。
        /// ch:v1.3.55 P1-2：readCount 可达 4096（Form3 读缓冲）而 MaxFrame=1024，旧写法把 readBuf 尾部
        ///   超出上限的字节静默丢弃＝数据丢失。改为按 MaxFrame 分片：每片先入 pending 再 DrainPending，
        ///   DrainPending 产出/溢出释放空间后下一片继续，保证「已产出帧 + 剩余 pending == readCount」字节守恒。
        /// </summary>
        public List<byte[]> Append(int readCount, byte[] readBuf, List<byte[]> patterns)
        {
            var frames = new List<byte[]>();
            if (readCount <= 0)
            {
                _idlePolls++;                 // 空闲轮：仅推进 settle / 未知数据整段产出判定
                DrainPending(frames, patterns);
                return frames;
            }

            _idlePolls = 0;
            int offset = 0;
            while (offset < readCount)
            {
                int free = MaxFrame - _pending.Count;         // DrainPending 保证回到此处时 pending 未满
                int take = free < (readCount - offset) ? free : (readCount - offset);
                if (take <= 0)                                // 防御：绝不静默丢尾字节，pending 满则先整段产出腾空间
                {
                    frames.Add(_pending.ToArray());
                    _pending.Clear();
                    continue;
                }
                for (int k = 0; k < take; k++) _pending.Add(readBuf[offset + k]);
                offset += take;
                DrainPending(frames, patterns);
            }
            return frames;
        }

        // ch:v1.3.55 pending 消费循环（前缀锚定精确匹配，绝不做子串切分）。
        //   P1-1 根治：原等待条件要求 hit.Length == _pending.Count（短串恰吃满 pending 才等），
        //   于是 patterns "1"/"123" 分包成 "12"+"3" 时，"12" 里头部完整命中 "1" 但 pending 未吃满 → 提前派发 "1"，
        //   残留 "2" 与后续 "3" 拼不成 "123"，长串永远切不到。改为：只要 pending 是任一更长串的严格前缀就等
        //   （不再要求吃满），等出更长串或连续 PrefixWaitMaxPolls 轮无新字节(settle)后再派发头部短串。
        // ch:v1.3.55 P2-4：CR/LF 常被设备用作帧分隔符。旧逻辑只在 pending 头部锚定匹配，一帧读入
        //   "AB\r\nCD\r\n" 时第二帧被前面残留的 "\r\n" 挡住，永不单独派发（整段被当未知数据空闲 flush）。
        //   改为每次匹配前先剥离 pending 头部连续的 CR/LF（0x0D/0x0A），分隔符后的每一帧即可逐个头部命中并派发。
        //   安全：GBK 双字节的尾字节范围 0x40–0xFE、首字节 0x81–0xFE，均不含 0x0D/0x0A，剥离不会截断多字节字符；
        //   已知载图串也不以 CR/LF 开头，故剥离的字节只可能是分隔符或纯空白帧，交给原 IsWhitespaceOnly 也是跳过。
        private void DrainPending(List<byte[]> frames, List<byte[]> patterns)
        {
            while (_pending.Count > 0)
            {
                while (_pending.Count > 0 && (_pending[0] == 0x0D || _pending[0] == 0x0A))
                    _pending.RemoveAt(0);
                if (_pending.Count == 0) break;

                byte[] hit = LongestCompleteMatch(patterns);
                bool strictPrefix = IsStrictPrefixOfAny(patterns);

                if (hit != null)
                {
                    // pending(可能比 hit 长)仍是某更长串的前缀 → 未 settle、未溢出则继续等待，防跨读取误切
                    if (strictPrefix && _pending.Count < MaxFrame && _idlePolls < PrefixWaitMaxPolls)
                        break;
                    byte[] frame = _pending.GetRange(0, hit.Length).ToArray();
                    _pending.RemoveRange(0, hit.Length);
                    frames.Add(frame);
                    continue;
                }

                // 无完整命中：pending 是更长串的真前缀 → 等分包（未 settle、未溢出）
                if (strictPrefix && _pending.Count < MaxFrame && _idlePolls < PrefixWaitMaxPolls)
                    break;

                // ch:v1.3.56 R1 未知数据当前轮立即整段产出：头部已不可能再拼成任何已知串（不做子串切分），
                //   继续等待只会把多段未知报文/监视文本攒成一批（连续流要攒到 MaxFrame 才吐），
                //   使 getData/jieshou 与接收框失去"逐读透传"语义。此处仅剩三种情形：
                //   非前缀未知 / 前缀 settle 超时 / 已达 MaxFrame，均应立即产出。
                byte[] leftover = _pending.ToArray();
                _pending.Clear();
                frames.Add(leftover);
                break;
            }
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
