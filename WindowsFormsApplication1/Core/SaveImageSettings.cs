using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:定制存图（铭泰定子/转子）单相机配置。
    ///   Folder = 根目录下的一级目录，可留空（不留这一层）；多台相机填同一个名字即合并到同一目录
    ///            （例：相机1=顶部、相机4=底部，都填"啊啊啊"→ 同型号产品的图落进同一个文件夹）
    ///   Name   = 存图名称（不含扩展名），支持 {相机} {序号} {日期} {时间} {文件夹}
    ///   Slots  = 每个产品该相机拍几张（定子内壁/外壁 3，顶部/底部 1，转子 1）
    /// 说明：文件名固定 + 槽位循环，同一文件夹拍第 N+1 次时覆盖第 1 张，正是现场要求的
    ///   "拍多了自动覆盖、稳定满 8 张"。
    /// </summary>
    public sealed class CameraSaveSetting
    {
        public bool Enabled;
        public string Folder;
        public string Name;
        public int Slots;

        public CameraSaveSetting()
        {
            Enabled = false;
            Folder = "";
            Name = "";
            Slots = 1;
        }

        public CameraSaveSetting Clone()
        {
            CameraSaveSetting c = new CameraSaveSetting();
            c.Enabled = Enabled;
            c.Folder = Folder ?? "";
            c.Name = Name ?? "";
            c.Slots = Slots;
            return c;
        }
    }

    /// <summary>
    /// ch:定制存图总配置（存 code.ini [saveimage] 段）。
    /// </summary>
    public sealed class SaveImageSettings
    {
        public const int CameraCount = 8;
        public const string DefaultRoot = @"E:\fuNok";
        public const string DefaultDateFormat = "yyyy-MM-dd";
        public const int MinJpegQuality = 50;
        public const int MaxJpegQuality = 100;
        public const int MaxSlots = 20;
        public const int MaxRetentionDays = 3650;

        public bool Enabled;
        public string Root;
        public string DateFormat;
        public bool Jpeg;
        public int JpegQuality;
        /// <summary>ch:保留天数，0=不自动删除；启动时清理更早的“日期”文件夹。</summary>
        public int RetentionDays;
        public readonly CameraSaveSetting[] Cameras;

        public SaveImageSettings()
        {
            Enabled = false;
            Root = DefaultRoot;
            DateFormat = DefaultDateFormat;
            Jpeg = false;
            JpegQuality = 90;
            RetentionDays = 30;
            Cameras = new CameraSaveSetting[CameraCount];
            for (int i = 0; i < CameraCount; i++)
                Cameras[i] = new CameraSaveSetting();
        }

        public SaveImageSettings Clone()
        {
            SaveImageSettings s = new SaveImageSettings();
            s.Enabled = Enabled;
            s.Root = Root;
            s.DateFormat = DateFormat;
            s.Jpeg = Jpeg;
            s.JpegQuality = JpegQuality;
            s.RetentionDays = RetentionDays;
            for (int i = 0; i < CameraCount; i++)
                s.Cameras[i] = Cameras[i] == null ? new CameraSaveSetting() : Cameras[i].Clone();
            return s;
        }
    }

    /// <summary>
    /// ch:定制存图的 ini 读写与路径/文件名规则（纯托管，单测直链）。
    /// </summary>
    public static class SaveImageSettingsStore
    {
        private const string InvalidSegmentChars = "\\/:*?\"<>|";

        public static SaveImageSettings Load(Func<string, string, string> read)
        {
            SaveImageSettings s = new SaveImageSettings();
            if (read == null) return s;

            s.Enabled = ToBool(Clean(read("enabled", "0")));

            string root = Clean(read("root", SaveImageSettings.DefaultRoot));
            if (root.Length > 0) s.Root = root;

            string dateFormat = Clean(read("dateformat", SaveImageSettings.DefaultDateFormat));
            if (dateFormat.Length > 0) s.DateFormat = dateFormat;

            string format = Clean(read("format", "bmp")).ToLowerInvariant();
            s.Jpeg = format == "jpg" || format == "jpeg";
            s.JpegQuality = ClampInt(Clean(read("quality", "90")),
                SaveImageSettings.MinJpegQuality, SaveImageSettings.MaxJpegQuality, 90);
            s.RetentionDays = ClampInt(Clean(read("keepdays", "30")),
                0, SaveImageSettings.MaxRetentionDays, 30);

            for (int i = 0; i < SaveImageSettings.CameraCount; i++)
            {
                string prefix = "cam" + (i + 1).ToString(CultureInfo.InvariantCulture);
                CameraSaveSetting c = s.Cameras[i];
                c.Enabled = ToBool(Clean(read(prefix + "_enabled", "0")));
                c.Folder = Clean(read(prefix + "_dir", ""));
                c.Name = Clean(read(prefix + "_name", ""));
                c.Slots = ClampInt(Clean(read(prefix + "_slots", "1")), 1, SaveImageSettings.MaxSlots, 1);
            }
            return s;
        }

        public static void Save(SaveImageSettings s, Action<string, string> write)
        {
            if (s == null || write == null) return;
            write("enabled", s.Enabled ? "1" : "0");
            write("root", s.Root ?? "");
            write("dateformat", string.IsNullOrEmpty(s.DateFormat) ? SaveImageSettings.DefaultDateFormat : s.DateFormat);
            write("format", s.Jpeg ? "jpg" : "bmp");
            write("quality", s.JpegQuality.ToString(CultureInfo.InvariantCulture));
            write("keepdays", Math.Max(0, Math.Min(SaveImageSettings.MaxRetentionDays, s.RetentionDays))
                .ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < SaveImageSettings.CameraCount; i++)
            {
                string prefix = "cam" + (i + 1).ToString(CultureInfo.InvariantCulture);
                CameraSaveSetting c = s.Cameras != null && i < s.Cameras.Length && s.Cameras[i] != null
                    ? s.Cameras[i]
                    : new CameraSaveSetting();
                write(prefix + "_enabled", c.Enabled ? "1" : "0");
                write(prefix + "_dir", c.Folder ?? "");
                write(prefix + "_name", c.Name ?? "");
                write(prefix + "_slots", Math.Max(1, Math.Min(SaveImageSettings.MaxSlots, c.Slots)).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// ch:拼 根目录\相机目录\日期\触发字符串。相机目录为空则不留这一层。
        /// 根目录为空返回空串（调用方跳过存图并记日志）；日期格式非法时回退 yyyy-MM-dd。
        /// </summary>
        public static string BuildFolderPath(string root, string dateFormat, string folderName, DateTime now,
            string cameraFolder = null)
        {
            string r = (root ?? "").Trim();
            if (r.Length == 0) return "";

            string fmt = (dateFormat ?? "").Trim();
            if (fmt.Length == 0) fmt = SaveImageSettings.DefaultDateFormat;

            string dateText;
            try
            {
                dateText = now.ToString(fmt, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                dateText = now.ToString(SaveImageSettings.DefaultDateFormat, CultureInfo.InvariantCulture);
            }
            if (string.IsNullOrEmpty(dateText))
                dateText = now.ToString(SaveImageSettings.DefaultDateFormat, CultureInfo.InvariantCulture);

            string dateSeg = SanitizeSegment(dateText);
            if (dateSeg.Length == 0)
                dateSeg = now.ToString(SaveImageSettings.DefaultDateFormat, CultureInfo.InvariantCulture);

            string product = SanitizeSegment(folderName);
            if (product.Length == 0) product = "未命名产品";

            string camDir = SanitizeSegment(cameraFolder);
            if (camDir.Length > 0)
                return Path.Combine(r, camDir, dateSeg, product);
            return Path.Combine(r, dateSeg, product);
        }

        /// <summary>
        /// ch:按存图名称渲染文件名（不含扩展名）。名称为空时用"相机N"兜底；
        /// Slots 大于 1 且名称没写 {序号} 时自动追加 _序号，
        /// 保证"同一相机同一文件夹拍 3 张"得到 3 个不同文件，而不是互相覆盖只剩 1 张。
        /// </summary>
        public static string RenderFileName(CameraSaveSetting cam, int camIndex, string folderName, DateTime now, int slot)
        {
            if (slot < 1) slot = 1;
            int slots = cam != null ? Math.Max(1, cam.Slots) : 1;

            string name = cam != null ? (cam.Name ?? "").Trim() : "";
            if (name.Length == 0)
                name = "相机" + camIndex.ToString(CultureInfo.InvariantCulture);

            bool hasSlotToken = name.IndexOf("{序号}", StringComparison.Ordinal) >= 0
                || name.IndexOf("{slot}", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("{num}", StringComparison.OrdinalIgnoreCase) >= 0;
            if (slots > 1 && !hasSlotToken)
                name = name + "_{序号}";

            string rendered = ApplyTemplate(name, camIndex, slot, now, folderName);
            rendered = SanitizeSegment(rendered);
            if (rendered.Length == 0)
                rendered = "相机" + camIndex.ToString(CultureInfo.InvariantCulture) + "_" + slot.ToString(CultureInfo.InvariantCulture);
            return rendered;
        }

        private static string ApplyTemplate(string template, int camIndex, int slot, DateTime now, string folderName)
        {
            string s = template ?? "";
            s = s.Replace("{相机}", camIndex.ToString(CultureInfo.InvariantCulture));
            s = s.Replace("{cam}", camIndex.ToString(CultureInfo.InvariantCulture));
            s = s.Replace("{序号}", slot.ToString(CultureInfo.InvariantCulture));
            s = s.Replace("{slot}", slot.ToString(CultureInfo.InvariantCulture));
            s = s.Replace("{日期}", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            s = s.Replace("{date}", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            s = s.Replace("{时间}", now.ToString("HHmmss", CultureInfo.InvariantCulture));
            s = s.Replace("{time}", now.ToString("HHmmss", CultureInfo.InvariantCulture));
            s = s.Replace("{文件夹}", folderName ?? "");
            s = s.Replace("{folder}", folderName ?? "");
            return s;
        }

        /// <summary>ch:把一段文本变成合法的 Windows 文件名/目录名（非法字符与控制字符替换为 _）。</summary>
        public static string SanitizeSegment(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool bad = c < 32 || InvalidSegmentChars.IndexOf(c) >= 0;
                sb.Append(bad ? '_' : c);
            }
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r == "." || r == "..") return "";
            if (r.Length > 80)
                r = r.Substring(0, 80).Trim().TrimEnd('.');
            return r;
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace("\0", "").Trim();
        }

        private static bool ToBool(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s == "1"
                || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ClampInt(string s, int min, int max, int fallback)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return fallback;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }

    /// <summary>
    /// ch:定制存图的槽位计数器：按 (文件夹 + 相机) 各自从 1 数到 Slots 再回到 1。
    ///   同一产品文件夹内同一相机重复拍照时循环覆盖，不产生无限增长的文件名；
    ///   产品文件夹变了就是新的一组槽位。线程安全，供每相机检测线程并发调用。
    /// </summary>
    public sealed class SaveSlotCounter
    {
        private sealed class Entry
        {
            public int Cursor;
            public DateTime LastTouched;
        }

        private const int PruneThreshold = 4096;
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public int GetNextSlot(string folder, int camIndex, int slots, DateTime now)
        {
            if (slots < 1) slots = 1;
            string key = (folder ?? "") + "|" + camIndex.ToString(CultureInfo.InvariantCulture);
            lock (_entries)
            {
                Entry e;
                if (!_entries.TryGetValue(key, out e))
                {
                    e = new Entry();
                    _entries[key] = e;
                }
                int slot = (e.Cursor % slots) + 1;
                e.Cursor = (e.Cursor + 1) % slots;
                e.LastTouched = now;
                if (_entries.Count > PruneThreshold)
                    Prune(now);
                return slot;
            }
        }

        public int Count
        {
            get { lock (_entries) return _entries.Count; }
        }

        public void Reset()
        {
            lock (_entries) _entries.Clear();
        }

        private void Prune(DateTime now)
        {
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                if ((now - kv.Value.LastTouched).TotalHours >= 6)
                    stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++)
                _entries.Remove(stale[i]);
            if (_entries.Count > PruneThreshold)
                _entries.Clear();
        }
    }
}
