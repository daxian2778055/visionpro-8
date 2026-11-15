using System;
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

        /// <summary>
        /// ch:删除早于保留期的日期文件夹，返回删除个数。
        ///   keepDays&lt;=0 表示不自动删除；root 为空或不存在直接返回 0。
        ///   keepDays=30 且今天 11-15 → 保留 10-17..11-15，删 10-16 及更早。
        /// </summary>
        public static int Cleanup(string root, string dateFormat, int keepDays, DateTime now, Action<string> log)
        {
            if (keepDays <= 0) return 0;
            if (keepDays > SaveImageSettings.MaxRetentionDays) keepDays = SaveImageSettings.MaxRetentionDays;

            string r = (root ?? "").Trim();
            if (r.Length == 0 || !Directory.Exists(r)) return 0;

            DateTime cutoff = now.Date.AddDays(-(keepDays - 1));
            int removed = 0;

            foreach (string level1 in SafeGetDirectories(r, log))
            {
                if (IsReparsePoint(level1, log)) continue;

                DateTime d1;
                if (TryParseDateFolder(Path.GetFileName(level1), dateFormat, out d1))
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

        /// <summary>ch:文件夹名 → 日期（整串匹配，解析不出或年份离谱返回 false）。</summary>
        public static bool TryParseDateFolder(string name, string dateFormat, out DateTime date)
        {
            date = DateTime.MinValue;
            string n = (name ?? "").Trim();
            if (n.Length == 0) return false;

            if (TryParseExact(n, dateFormat, out date)) return true;
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
            if (!DateTime.TryParseExact(name, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return false;
            // ch:年份离谱（相机目录恰好叫 00000000/99999999 之类）不当作日期，宁可不删也不误删
            if (date.Year < 2000 || date.Year > 2100)
            {
                date = DateTime.MinValue;
                return false;
            }
            return true;
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
