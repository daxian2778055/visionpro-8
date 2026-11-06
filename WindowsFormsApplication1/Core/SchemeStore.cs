using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R12 方案表数据层（纯托管，无 WinForms/ini 依赖，Tests 直链同源回归）。
    ///   四条通讯通道各自持有一份：载图串→方案路径的 N 行映射表（上限 100，默认 8=历史行为）。
    ///   ini 无 count 键时按老 8 键读取（现场 test.ini 原样兼容）；有则按 1..count 循环。
    ///   ch:R13-1 匹配方读的是「单一 volatile 快照」：keys/paths 打包在同一不可变对象里一次取回，
    ///   杜绝"旧 keys + 新 paths"撕裂（缩表时 paths[hit] 越界、中间行变更时静默切错路径）。
    /// </summary>
    public class SchemeStore
    {
        public const int MaxSchemes = 100;
        public const int LegacyRows = 8;

        /// <summary>ch:R13-1 配对快照：发布后内容不再改动，读取方一次引用即得配套 keys/paths。</summary>
        private sealed class Snapshot
        {
            public readonly string[] Keys;
            public readonly string[] Paths;
            public Snapshot(string[] keys, string[] paths) { Keys = keys; Paths = paths; }
        }

        private readonly object _sync = new object();
        private List<string[]> _rows = new List<string[]>();
        private volatile Snapshot _snap = new Snapshot(new string[0], new string[0]);

        /// <summary>ini count 键解析：缺失/非法→8（老行为），合法值夹取 [0,100]。</summary>
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

        /// <summary>整表替换并发布新快照；返回因超过 100 行上限被丢弃的行数。</summary>
        public int SetRows(List<string[]> rows)
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
                _snap = new Snapshot(keys, paths); // ch:R13-1 单引用发布，keys/paths 永不撕裂
            }
            return rows == null ? 0 : Math.Max(0, rows.Count - MaxSchemes);
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

        /// <summary>
        /// ch:R13-1 匹配唯一入口：一次取快照做精确匹配。
        /// 返回 true=唯一命中（path 即该行方案路径）；hits&gt;1=配置冲突；hits==0=未命中。
        /// </summary>
        public bool TryResolve(string input, out string path, out int hits)
        {
            Snapshot s = _snap;
            int hit = SchemeMatcher.Match(s.Keys, input, out hits);
            path = hit >= 0 ? s.Paths[hit] : null;
            return hit >= 0;
        }

        /// <summary>ch:R13-2 组帧/粘包诊断用键引用（与 TryResolve 同一快照，成对读路径请走 TryResolve）。</summary>
        public string[] KeysArray() { return _snap.Keys; }

        /// <summary>去掉表尾连续空行（null 行按空行处理）；全空返回空表。</summary>
        public static List<string[]> TrimTrailingEmpty(List<string[]> rows)
        {
            if (rows == null) return new List<string[]>();
            List<string[]> copy = new List<string[]>();
            for (int i = 0; i < rows.Count; i++)
                copy.Add(rows[i]);
            while (copy.Count > 0 && RowIsEmpty(copy[copy.Count - 1]))
                copy.RemoveAt(copy.Count - 1);
            return copy;
        }

        private static bool RowIsEmpty(string[] r)
        {
            return r == null || (string.IsNullOrEmpty(r[0]) && string.IsNullOrEmpty(r[1]));
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

        /// <summary>
        /// ch:v1.3.54 前缀冲突扫描：某载图串是另一载图串的开头前缀（如 "1"/"12"、"相机1"/"相机10"）。
        ///   无分隔符协议下前缀关系在分包时会误切（收到 "1" 无法判断是短串还是长串前半），运行期靠
        ///   SerialFrameAccumulator 的 settle 等待兜底，但配置层应尽早拒绝。返回冲突描述（含两条串+来源），无冲突返回 null。
        ///   extraKeys 为各相机 triggerZifu 等外部已知串，一并纳入前缀交叉检查（可为 null）。
        /// ch:v1.3.55 P2-1：旧实现只查 rows→rows 与 rows→extra(长方向)，漏 extra→rows(短 extra 是行串前缀)
        ///   与 extra↔extra。改为把 rows 与 extra 合并成一个「带来源标签」的键集，两两全序检查严格前缀关系，四向覆盖。
        ///   注：仅串口/Form3 这类前缀锚定通道应在保存时据此拒绝；精确相等匹配通道(TCP/PLC)不受前缀歧义影响，不调用本方法。
        /// </summary>
        public static string FindPrefixConflicts(List<string[]> rows, string[] extraKeys)
        {
            var keys = new List<string>();
            var labels = new List<string>();
            CollectPrefixKeys(rows, extraKeys, keys, labels);

            List<string> conflicts = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                string a = keys[i];
                for (int j = 0; j < keys.Count; j++)
                {
                    if (i == j) continue;
                    string b = keys[j];
                    if (b.Length > a.Length && b.StartsWith(a, StringComparison.Ordinal))
                        conflicts.Add("[" + a + "](" + labels[i] + ") 是 [" + b + "](" + labels[j] + ") 的前缀");
                }
            }
            return conflicts.Count > 0 ? string.Join("；", conflicts.ToArray()) : null;
        }

        /// <summary>
        /// ch:v1.3.56 R2 返回参与前缀关系的全部键（前缀对的两端都返回）。
        ///   供 Form3 装载/保存后建立"歧义触发串黑名单"：这些串到达时一律拒绝自动切换（fail-safe），
        ///   避免存量 ini 里的 "1"/"12" 在分包拼接时静默切错方案。返回空列表表示无前缀冲突。
        /// </summary>
        public static List<string> FindPrefixConflictKeys(List<string[]> rows, string[] extraKeys)
        {
            var keys = new List<string>();
            var labels = new List<string>();
            CollectPrefixKeys(rows, extraKeys, keys, labels);

            var hit = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string a = keys[i];
                for (int j = 0; j < keys.Count; j++)
                {
                    if (i == j) continue;
                    string b = keys[j];
                    if (b.Length > a.Length && b.StartsWith(a, StringComparison.Ordinal))
                    {
                        hit.Add(a);
                        hit.Add(b);
                    }
                }
            }
            return new List<string>(hit);
        }

        // ch:v1.3.56 R2 行串与外部触发串合并收集，标签带来源（相机序号便于现场定位）
        private static void CollectPrefixKeys(List<string[]> rows, string[] extraKeys, List<string> keys, List<string> labels)
        {
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    string a = rows[i] == null ? null : rows[i][0];
                    if (string.IsNullOrEmpty(a)) continue;
                    keys.Add(a);
                    labels.Add("第" + (i + 1) + "行");
                }
            }
            if (extraKeys != null)
            {
                for (int k = 0; k < extraKeys.Length; k++)
                {
                    string e = extraKeys[k];
                    if (string.IsNullOrEmpty(e)) continue;
                    keys.Add(e);
                    labels.Add("相机" + (k + 1) + "触发串");
                }
            }
        }
    }
}
