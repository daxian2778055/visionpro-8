using System;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R12 方案切换载图串匹配器（纯函数，无 WinForms/网络依赖，Tests 直链同源回归）。
    ///   统一四条通讯通道（无协议 Form3 / Modbus TCP / Omron / Modbus RTU）的匹配语义为三条规则：
    ///   - 精确等值匹配，配置为空的载图串不参与（旧 if 链在"配置文本框为空且输入为空串"时会误命中第一路）；
    ///   - 仅唯一命中生效；同一输入命中多路 = 配置冲突，拒绝切换并回报命中数
    ///     （旧 if 链先到先得，串重复时静默切到错误方案）；
    ///   - 表长任意；方案数量上限由配置侧把关（MaxSchemes=100，本轮 8 固定控件，表格化 UI 后放开）。
    /// </summary>
    public static class SchemeMatcher
    {
        /// <summary>ch:R12 方案数上限（100 量级；防配置异常时每次匹配遍历过大）。</summary>
        public const int MaxSchemes = 100;
        public const int NoHit = -1;
        public const int Conflict = -2;

        /// <summary>
        /// 在 keys 中精确匹配 input。返回：唯一命中=该下标；多命中=Conflict（hits 给出次数）；无命中/空输入=NoHit。
        /// </summary>
        public static int Match(string[] keys, string input, out int hits)
        {
            hits = 0;
            if (keys == null || string.IsNullOrEmpty(input))
                return NoHit;
            int first = NoHit;
            for (int i = 0; i < keys.Length; i++)
            {
                if (string.IsNullOrEmpty(keys[i]))
                    continue;
                if (keys[i] == input)
                {
                    hits++;
                    if (first == NoHit)
                        first = i;
                }
            }
            if (hits > 1)
                return Conflict;
            return first;
        }
    }
}
