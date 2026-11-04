using System;
using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R44 相机10方案切换配置防呆：同一台设备只允许一个 PLC 通道启用相机10方案切换。
    ///   Omron / ModbusTCP / ModbusRTU 三个配置窗在勾选“相机10”时调用本类检查另外两段 ini；
    ///   已有其它通道启用则拒绝本次勾选，避免现场配出“多通道各保持不同方案串”的互切条件。
    ///   纯托管、通过 reader 委托读 ini，Tests 可直链回归，不依赖 WinForms/ClassIni。
    /// </summary>
    public static class SchemeSwitchConfig
    {
        public const string OmronSection = "c10";
        public const string ModbusTcpSection = "c10_modbustcp";
        public const string ModbusRtuSection = "c10modbusrtu";

        public static readonly string[] Cam10Sections =
        {
            OmronSection,
            ModbusTcpSection,
            ModbusRtuSection
        };

        public static string DisplayName(string section)
        {
            if (string.Equals(section, OmronSection, StringComparison.OrdinalIgnoreCase))
                return "欧姆龙";
            if (string.Equals(section, ModbusTcpSection, StringComparison.OrdinalIgnoreCase))
                return "Modbus TCP";
            if (string.Equals(section, ModbusRtuSection, StringComparison.OrdinalIgnoreCase))
                return "Modbus RTU";
            return section;
        }

        /// <summary>
        /// 返回除 exceptSection 外仍启用相机10的通道名（“、”分隔）；没有返回 null。
        /// reader 参数：section, key, defaultValue。
        /// </summary>
        public static string FindOtherEnabledCam10(Func<string, string, string, string> read, string exceptSection)
        {
            if (read == null)
                return null;

            List<string> enabled = new List<string>();
            foreach (string section in Cam10Sections)
            {
                if (string.Equals(section, exceptSection, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsTrue(read(section, "fanhuien", "false")))
                    enabled.Add(DisplayName(section));
            }
            return enabled.Count == 0 ? null : string.Join("、", enabled.ToArray());
        }

        /// <summary>
        /// 返回同时启用相机10的通道名（“、”分隔）；0 或 1 个返回 null，多于 1 个才算冲突。
        /// 用于启动时对存量 ini 做一次告警（现场手改/老配置可能已经多开）。
        /// </summary>
        public static string FindAllEnabledCam10(Func<string, string, string, string> read)
        {
            if (read == null)
                return null;

            List<string> enabled = new List<string>();
            foreach (string section in Cam10Sections)
            {
                if (IsTrue(read(section, "fanhuien", "false")))
                    enabled.Add(DisplayName(section));
            }
            return enabled.Count > 1 ? string.Join("、", enabled.ToArray()) : null;
        }

        private static bool IsTrue(string value)
        {
            return string.Equals((value ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
