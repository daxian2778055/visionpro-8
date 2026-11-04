using System;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R45 方案表 ini 段解析：每条通讯协议一份独立方案表，互不覆盖。
    ///   无协议通道(Form3)从历史的 [change]/[path] 迁到 [change_serial]/[path_serial]；
    ///   Omron 继续使用 [change]/[path]（现场存量配置零迁移）。
    ///   新段没有 count 键时回退读旧段：升级后第一次打开 Form3 仍能看到原方案表；
    ///   保存只写新段、绝不回写旧段，否则会再次覆盖 Omron 的表。
    /// </summary>
    public static class SerialSchemeSection
    {
        public const string Change = "change_serial";
        public const string Path = "path_serial";
        public const string LegacyChange = "change";
        public const string LegacyPath = "path";

        /// <summary>
        /// 解析本次应读的方案表段。read 参数：(section, key, defaultValue)。
        /// 返回 true=使用新段；false=回退旧段（新段不存在或没有 count 键）。
        /// </summary>
        public static bool Resolve(Func<string, string, string, string> read,
            out string changeSection, out string pathSection, out string count)
        {
            if (read == null)
                throw new ArgumentNullException("read");

            string ownCount = read(Change, "count", "");
            if (!string.IsNullOrWhiteSpace(ownCount))
            {
                changeSection = Change;
                pathSection = Path;
                count = ownCount;
                return true;
            }

            changeSection = LegacyChange;
            pathSection = LegacyPath;
            count = read(LegacyChange, "count", "");
            return false;
        }
    }
}
