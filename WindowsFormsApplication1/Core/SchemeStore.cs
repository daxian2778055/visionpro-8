using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R12 方案表数据层（纯托管，无 WinForms/ini 依赖，Tests 直链同源回归）。
    ///   四条通讯通道各自持有一份：载图串→方案路径的 N 行映射表（上限 100，默认 8=历史行为）。
    ///   ini 无 scheme_count 键时按老 8 键读取（现场 test.ini 原样兼容）；有则按 1..count 循环。
    ///   匹配方（qiehuan 轮询/接收线程）读 KeysArray/PathsArray 快照——数组发布后不再改动，
    ///   volatile 引用整体替换，轮询线程无需再逐控件跨线程取 Text（100 行时也不放大）。
    /// </summary>
    public class SchemeStore
    {
        public const int MaxSchemes = 100;
        public const int LegacyRows = 8;

        private readonly object _sync = new object();
        private List<string[]> _rows = new List<string[]>(); // [载图串, 方案路径]
        private volatile string[] _keys = new string[0];
        private volatile string[] _paths = new string[0];

        /// <summary>ini scheme_count 解析：缺失/非法→8（老行为），合法值夹取 [0,100]。</summary>
        public static int ResolveCount(string rawCount)
        {
            if (string.IsNullOrEmpty(rawCount))
                return LegacyRows;
            int v;
            if (!int.TryParse(rawCount.Trim(), out v))
                return LegacyRows;
            if (v < 0) return 0;
            if (v > MaxSchemes) return MaxSchemes;
            return v;
        }

        /// <summary>从 ini 读取委托装载 1..N 行；changeAt/pathAt 按 1 基索引取键。</summary>
        public void LoadFrom(Func<int, string> changeAt, Func<int, string> pathAt, string rawCount)
        {
            int n = ResolveCount(rawCount);
            List<string[]> rows = new List<string[]>();
            for (int i = 1; i <= n; i++)
            {
                string c = changeAt(i);
                string p = pathAt(i);
                rows.Add(new string[] { c ?? "", p ?? "" });
            }
            SetRows(rows);
        }

        /// <summary>整表替换并重建匹配快照（超过 100 行的部分截断并回报被弃行数由调用方决定）。</summary>
        public void SetRows(List<string[]> rows)
        {
            List<string[]> copy = new List<string[]>();
            if (rows != null)
            {
                for (int i = 0; i < rows.Count && i < MaxSchemes; i++)
                {
                    string[] r = rows[i];
                    copy.Add(new string[] { r == null || r[0] == null ? "" : r[0], r == null || r[1] == null ? "" : r[1] });
                }
            }
            string[] keys = new string[copy.Count];
            string[] paths = new string[copy.Count];
            for (int i = 0; i < copy.Count; i++)
            {
                keys[i] = copy[i][0];
                paths[i] = copy[i][1];
            }
            lock (_sync)
            {
                _rows = copy;
                _paths = paths;
                _keys = keys; // volatile 最后发布：快照可见时 keys/paths 已配套
            }
        }

        /// <summary>行副本（供 UI/保存合并用）。</summary>
        public List<string[]> GetRows()
        {
            lock (_sync)
            {
                List<string[]> copy = new List<string[]>();
                for (int i = 0; i < _rows.Count; i++)
                    copy.Add(new string[] { _rows[i][0], _rows[i][1] });
                return copy;
            }
        }

        public string[] KeysArray() { return _keys; }
        public string[] PathsArray() { return _paths; }

        /// <summary>去掉表尾连续空行；全空返回空表。</summary>
        public static List<string[]> TrimTrailingEmpty(List<string[]> rows)
        {
            if (rows == null) return new List<string[]>();
            List<string[]> copy = new List<string[]>();
            for (int i = 0; i < rows.Count; i++)
                copy.Add(rows[i]);
            while (copy.Count > 0 && string.IsNullOrEmpty(copy[copy.Count - 1][0]) && string.IsNullOrEmpty(copy[copy.Count - 1][1]))
                copy.RemoveAt(copy.Count - 1);
            return copy;
        }

        /// <summary>
        /// 全表载图串重复扫描：返回重复描述（含行号），无重复返回 null。空串不参与。
        /// 与 R43 保存校验语义一致：重复=无法确定切换目标，保存应拒绝。
        /// </summary>
        public static string FindDuplicates(List<string[]> rows)
        {
            if (rows == null) return null;
            Dictionary<string, string> seen = new Dictionary<string, string>();
            List<string> dups = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                string k = rows[i] == null ? null : rows[i][0];
                if (string.IsNullOrEmpty(k))
                    continue;
                string prev;
                if (seen.TryGetValue(k, out prev))
                    dups.Add("[" + k + "] 同时配置在 第" + prev + "行 与 第" + (i + 1) + "行");
                else
                    seen[k] = (i + 1).ToString();
            }
            return dups.Count > 0 ? string.Join("；", dups.ToArray()) : null;
        }
    }
}
