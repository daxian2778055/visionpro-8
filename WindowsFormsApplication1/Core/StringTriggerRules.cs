using System;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:字符串触发规则（ModbusTCP / ModbusRTU / Fins 三协议共用）。
    ///   字符串触发通道 = 数据绑定里“格式=string”且“高低位/用途”含“触发”的通道。
    ///   长度 N = N 个寄存器；一个 16 位寄存器 = 2 字节 = 2 个 ASCII 字符，所以整串最多 2N 字符。
    ///   读取整串用 ReadString(起始地址, N*2)（length 是字节数），旧的逐寄存器 ReadString(addr,1)
    ///   只读半个字节会错位/带 NUL，已废弃。
    ///   触发判定：值发生变化即触发（PLC 写 0x0000 也算一次触发）；触发后立即把该通道
    ///   每寄存器清零成 ASCII "00"（整串 2N 个 '0'），并同步登记“已处理值”，下一轮不再重复触发。
    /// </summary>
    public static class StringTriggerRules
    {
        public const int MaxStringLength = 100;   // 寄存器个数上限
        public const int CharsPerRegister = 2;    // 16 位寄存器 = 2 个 ASCII 字符
        public const char ClearChar = '0';

        /// <summary>ch:是否字符串触发通道。</summary>
        public static bool IsStringTriggerChannel(string format, string usage)
        {
            string f = (format ?? "").Trim();
            string u = (usage ?? "").Trim();
            return string.Equals(f, "string", StringComparison.OrdinalIgnoreCase)
                && u.IndexOf("触发", StringComparison.Ordinal) >= 0;
        }

        /// <summary>ch:通道寄存器个数（用于 ReadString 的字节数换算）夹取到合法区间。</summary>
        public static int ClampRegisterCount(int count)
        {
            if (count < 1) return 1;
            if (count > MaxStringLength) return MaxStringLength;
            return count;
        }

        /// <summary>ch:整串清零值：N 个寄存器 → 2N 个 ASCII '0'（长度3 → "000000"）。</summary>
        public static string BuildClearValue(int registerCount)
        {
            return new string(ClearChar, ClampRegisterCount(registerCount) * CharsPerRegister);
        }

        /// <summary>ch:单个寄存器的清零值："00"（实测 Write(addr,"00") 读回 "00"）。</summary>
        public static string BuildRegisterClear()
        {
            return new string(ClearChar, CharsPerRegister);
        }

        /// <summary>ch:读取整串要传给 ReadString 的字节数 = 寄存器个数 × 2。</summary>
        public static int ToByteLength(int registerCount)
        {
            return ClampRegisterCount(registerCount) * CharsPerRegister;
        }

        /// <summary>ch:收到的产品编码去掉 NUL 与首尾空白。</summary>
        public static string NormalizeProductCode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return raw.Replace("\0", "").Trim();
        }
    }
}
