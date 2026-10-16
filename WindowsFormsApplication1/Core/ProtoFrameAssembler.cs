using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R42 无协议流触发串组帧器（纯字节、无编码/WinForms/网络依赖，Tests 直链同源回归）。
    ///   现场 socket 上只有「若干条精确匹配的触发串」、无分隔符/无长度前缀（见 Form3 R4/P2 注释），
    ///   而 TCP 会任意分段/合并：旧的「每收一段、整段等值比较」在触发串被拆开或合并时两头都匹配不上，
    ///   表现为静默丢触发（40fps 密集触发会把发生概率放大）。本类把收到的字节按已知触发串集合组帧：
    ///   - 段尾恰为某触发串前缀 → 暂存等待后续字节（缓冲长度必然小于最长触发串，不会无限囤积）；
    ///   - 合并（如 A1B1）→ 左端最长匹配依次切出各触发串；
    ///   - 不构成任何触发串的字节 → 原样作为残留段交回调用方（保持既有未知报文处理路径不变）；
    ///   - 约束/近似：配置集合内触发串宜互不为前缀（现场为定长指令码）；若存在前缀包含关系，
    ///     按「左端最长、立即切分」处理（不等待后续字节，避免真实的短串触发被无限拖延）。
    /// </summary>
    public class ProtoFrameAssembler
    {
        /// <summary>ch:R42 触发串数量上限（防配置异常时每次收包遍历过大）。</summary>
        public const int MaxPatterns = 64;

        private readonly List<byte> _buf = new List<byte>();

        /// <summary>清空暂存（连接断开/重连时调用，避免旧连接的半截数据串进新连接）。</summary>
        public void Reset()
        {
            _buf.Clear();
        }

        /// <summary>
        /// 追加本次收到的字节，返回本次可确定的帧（按到达顺序，每条为独立字节段，调用方自行解码）。
        /// patterns 为空时退化为「整段原样交回」（与旧行为一致）。
        /// </summary>
        public List<byte[]> Append(byte[] data, int count, IList<byte[]> patterns)
        {
            List<byte[]> outp = new List<byte[]>();
            if (data != null && count > 0)
            {
                int n = count <= data.Length ? count : data.Length;
                for (int i = 0; i < n; i++)
                    _buf.Add(data[i]);
            }
            if (patterns == null || patterns.Count == 0)
            {
                if (_buf.Count > 0)
                {
                    outp.Add(_buf.ToArray());
                    _buf.Clear();
                }
                return outp;
            }
            while (_buf.Count > 0)
            {
                // ① 左端完整匹配：取最长命中（等长多命中取先配置者）
                int bestLen = 0;
                foreach (byte[] pt in patterns)
                {
                    if (pt == null || pt.Length == 0 || pt.Length > _buf.Count || pt.Length <= bestLen)
                        continue;
                    if (PrefixOf(pt, 0))
                        bestLen = pt.Length;
                }
                if (bestLen > 0)
                {
                    outp.Add(Slice(0, bestLen));
                    _buf.RemoveRange(0, bestLen);
                    continue;
                }
                // ② 当前缓冲仍可能是某触发串的前缀 → 等待后续字节
                bool couldGrow = false;
                foreach (byte[] pt in patterns)
                {
                    if (pt == null || pt.Length <= _buf.Count)
                        continue;
                    if (PrefixOf(pt, 0))
                    {
                        couldGrow = true;
                        break;
                    }
                }
                if (couldGrow)
                    break;
                // ③ 找左端首个「完整命中」位置（从下标 1 起）：先把它前面的残留交回，再循环从头部切触发串
                int hitIdx = -1;
                for (int i = 1; i < _buf.Count; i++)
                {
                    foreach (byte[] pt in patterns)
                    {
                        if (pt == null || pt.Length == 0 || i + pt.Length > _buf.Count)
                            continue;
                        if (PrefixOf(pt, i))
                        {
                            hitIdx = i;
                            break;
                        }
                    }
                    if (hitIdx > 0)
                        break;
                }
                if (hitIdx > 0)
                {
                    outp.Add(Slice(0, hitIdx));
                    _buf.RemoveRange(0, hitIdx);
                    continue;
                }
                // ④ 无任何完整命中：尾部若含某触发串的严格前缀则保留该后缀（等后续字节拼全），其余交回
                int keep = 0;
                for (int s = 0; s < _buf.Count; s++)
                {
                    int len = _buf.Count - s;
                    foreach (byte[] pt in patterns)
                    {
                        if (pt == null || pt.Length <= len)
                            continue;
                        if (PrefixOf(pt, s) && len > keep)
                            keep = len;
                    }
                }
                if (keep >= _buf.Count)
                    break; // 整体都是某触发串前缀（② 已拦，兜底防死循环）
                if (keep > 0)
                {
                    outp.Add(Slice(0, _buf.Count - keep));
                    _buf.RemoveRange(0, _buf.Count - keep);
                    break;
                }
                outp.Add(_buf.ToArray());
                _buf.Clear();
            }
            return outp;
        }

        // 缓冲从 start 起是否与 pt 的前缀一致；pt 可超出缓冲长度（idx >= 缓冲长返回 true =
        // 「还看不出不匹配」的等待态），调用方用长度条件区分「完整命中」与「前缀等待」。
        private bool PrefixOf(byte[] pt, int start)
        {
            for (int k = 0; k < pt.Length; k++)
            {
                int idx = start + k;
                if (idx >= _buf.Count)
                    return true;
                if (_buf[idx] != pt[k])
                    return false;
            }
            return true;
        }

        private byte[] Slice(int start, int len)
        {
            byte[] a = new byte[len];
            for (int i = 0; i < len; i++)
                a[i] = _buf[start + i];
            return a;
        }
    }
}
