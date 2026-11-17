using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:存图保留天数清理（每次启动软件后台跑一次：删除早于 N 天的日期文件夹及其图片）。
    ///   只认“日期文件夹”——根目录下直接一层 root\日期，或多一层相机目录 root\相机目录\日期；
    ///   名字解析不出日期的目录一律不碰（相机目录、手工放的资料都安全）。
    ///   日期先按配置的格式解析，再兼容 yyyy-MM-dd / yyyyMMdd 等常见写法，
    ///   所以现场改过日期格式后，遗留的老目录也能被正常清掉。
    /// </summary>
    public static class SaveImageRetention
    {
        // ch:兼容历史/手改的日期文件夹名；全部按“整串匹配”解析
        private static readonly string[] FallbackDateFormats = new string[]
        {
            "yyyy-MM-dd", "yyyyMMdd", "yyyy_MM_dd", "yyyy.MM.dd", "yyyy年MM月dd日"
        };

        /// <summary>ch:兼容旧调用（不指定受保护的相机目录名）。</summary>
        public static int Cleanup(string root, string dateFormat, int keepDays, DateTime now, Action<string> log)
        {
            return Cleanup(root, dateFormat, keepDays, now, null, log);
        }

        /// <summary>
        /// ch:删除早于保留期的日期文件夹，返回删除个数。
        ///   keepDays&lt;=0 表示不自动删除；root 为空或不存在直接返回 0。
        ///   keepDays=30 且今天 11-15 → 保留 10-17..11-15，删 10-16 及更早。
        ///   protectedFolderNames：配置里的“相机目录”名。即使它长得很像日期（例如产品编号
        ///   20261101），也只清它下面的“日期”子目录，绝不把整个相机目录当日期目录删掉。
        /// </summary>
        public static int Cleanup(string root, string dateFormat, int keepDays, DateTime now,
            IEnumerable<string> protectedFolderNames, Action<string> log)
        {
            if (keepDays <= 0) return 0;
            if (keepDays > SaveImageSettings.MaxRetentionDays) keepDays = SaveImageSettings.MaxRetentionDays;

            string r = (root ?? "").Trim();
            if (r.Length == 0 || !Directory.Exists(r)) return 0;

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(r);
            }
            catch (Exception ex)
            {
                if (log != null) log("存图清理：根目录不是合法路径，跳过 " + r + " - " + ex.Message);
                return 0;
            }

            // ch:根目录直接指到盘符/共享根时，root\2024 这种普通目录也会被当成“日期目录”整棵删掉。
            //   这里拒绝清理，让用户把根目录指到专用文件夹（例如 E:\fuNok）。
            if (IsDriveOrShareRoot(fullRoot))
            {
                if (log != null) log("存图清理：根目录是磁盘/共享根目录，出于安全不自动清理，请把根目录指到专用文件夹：" + fullRoot);
                return 0;
            }

            HashSet<string> protectedNames = BuildProtectedSet(protectedFolderNames);
            DateTime cutoff = now.Date.AddDays(-(keepDays - 1));
            int removed = 0;

            foreach (string level1 in SafeGetDirectories(fullRoot, log))
            {
                if (IsReparsePoint(level1, log)) continue;

                string name1 = Path.GetFileName(level1);
                bool isCameraDir = protectedNames != null && protectedNames.Contains(name1);

                DateTime d1;
                if (!isCameraDir && TryParseDateFolder(name1, dateFormat, out d1))
                {
                    // root\日期
                    if (d1.Date < cutoff && TryDelete(level1, log)) removed++;
                    continue;
                }

                // root\相机目录\日期
                foreach (string level2 in SafeGetDirectories(level1, log))
                {
                    if (IsReparsePoint(level2, log)) continue;
                    DateTime d2;
                    if (!TryParseDateFolder(Path.GetFileName(level2), dateFormat, out d2)) continue;
                    if (d2.Date >= cutoff) continue;
                    if (TryDelete(level2, log)) removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// ch:判断路径是否就是盘符根（C:\）或共享根（\\server\share）。
        ///   这种根下面混着系统目录/用户资料，不能按“日期目录”规则自动删。
        /// </summary>
        public static bool IsDriveOrShareRoot(string fullPath)
        {
            try
            {
                string full = Path.GetFullPath((fullPath ?? "").Trim());
                string pathRoot = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(pathRoot)) return false;

                string a = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // ch:解析不了就按“不安全”处理，宁可不删
                return true;
            }
        }

        /// <summary>ch:文件夹名 → 日期（整串匹配，解析不出或年份离谱返回 false）。</summary>
        public static bool TryParseDateFolder(string name, string dateFormat, out DateTime date)
        {
            date = DateTime.MinValue;
            string n = (name ?? "").Trim();
            if (n.Length == 0) return false;

            string configured = (dateFormat ?? "").Trim();
            if (TryParseExact(n, configured, out date)) return true;

            // ch:日期格式里带 / \ : 等文件名非法字符时，建目录时已经被替换成 _；
            //   这里用同样的清洗规则再解析一次，否则这种格式配出来的目录永远清不掉。
            string sanitizedFormat = SaveImageSettingsStore.SanitizeSegment(configured);
            if (sanitizedFormat.Length > 0
                && !string.Equals(sanitizedFormat, configured, StringComparison.Ordinal)
                && TryParseExact(n, sanitizedFormat, out date))
                return true;

            for (int i = 0; i < FallbackDateFormats.Length; i++)
            {
                if (TryParseExact(n, FallbackDateFormats[i], out date)) return true;
            }
            date = DateTime.MinValue;
            return false;
        }

        private static bool TryParseExact(string name, string format, out DateTime date)
        {
            date = DateTime.MinValue;
            string f = (format ?? "").Trim();
            if (f.Length == 0) return false;
            try
            {
                if (!DateTime.TryParseExact(name, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return false;
            }
            catch (FormatException)
            {
                // ch:手工改坏的日期格式（例如 "%"）：只跳过这个格式，不能让整个清理线程中断
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            // ch:年份离谱（相机目录恰好叫 00000000/99999999 之类）不当作日期，宁可不删也不误删
            if (date.Year < 2000 || date.Year > 2100)
            {
                date = DateTime.MinValue;
                return false;
            }
            return true;
        }

        private static HashSet<string> BuildProtectedSet(IEnumerable<string> names)
        {
            if (names == null) return null;
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in names)
            {
                string s = SaveImageSettingsStore.SanitizeSegment(raw);
                if (s.Length > 0) set.Add(s);
            }
            return set.Count == 0 ? null : set;
        }

        private static string[] SafeGetDirectories(string path, Action<string> log)
        {
            try
            {
                return Directory.GetDirectories(path);
            }
            catch (Exception ex)
            {
                if (log != null) log("存图清理：枚举子目录失败 " + path + " - " + ex.Message);
                return new string[0];
            }
        }

        /// <summary>ch:目录联接/符号链接不进入、不删除，避免误删到别的盘。</summary>
        private static bool IsReparsePoint(string path, Action<string> log)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                if (log != null) log("存图清理：读取目录属性失败 " + path + " - " + ex.Message);
                return true;
            }
        }

        private static bool TryDelete(string path, Action<string> log)
        {
            try
            {
                Directory.Delete(path, true);
                if (log != null) log("存图清理：已删除 " + path);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("存图清理：删除失败 " + path + " - " + ex.Message);
                return false;
            }
        }
    }
}
