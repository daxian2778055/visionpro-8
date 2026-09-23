using System;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R14 Modbus RTU CRC-16 算法本体（原 Modbus.GetCRC16 的实现原样抽出）。
    ///   抽成不依赖串口 / WinForms / HslCommunication 的纯静态类，Tests\ModbusCrcTests.cs 直接
    ///   链接同一份源文件做回归 —— 测的是生产正在跑的那份代码，而不是"逐行复刻"
    ///   （复刻版只在写下的那一刻一致，之后生产被改动它不会跟着变，测试等于空转）。
    /// </summary>
    public static class ModbusCrc
    {
        /// <summary>
        /// CRC 校验，参数 data 为 byte 数组
        /// </summary>
        /// <param name="data">校验数据，字节数组</param>
        /// <returns>原数据 + 追加的两个校验字节，帧尾顺序为 [低8位, 高8位]</returns>
        public static byte[] Append(byte[] data)
        {
            byte[] data1 = new byte[data.Length + 2];
            //crc计算赋初始值
            int crc = 0xffff;
            for (int i = 0; i < data.Length; i++)
            {
                crc = crc ^ data[i];
                for (int j = 0; j < 8; j++)
                {
                    int temp;
                    temp = crc & 1;
                    crc = crc >> 1;
                    crc = crc & 0x7fff;
                    if (temp == 1)
                    {
                        crc = crc ^ 0xa001;
                    }
                    crc = crc & 0xffff;
                }
            }
            //CRC寄存器的高低位进行互换
            byte[] crc16 = new byte[2];
            //CRC寄存器的高8位放到数组下标 1
            crc16[1] = (byte)((crc >> 8) & 0xff);
            //CRC寄存器的低8位放到数组下标 0
            crc16[0] = (byte)(crc & 0xff);
            Array.Copy(data, 0, data1, 0, data.Length);
            Array.Copy(crc16, 0, data1, data.Length, crc16.Length);
            return data1;
        }
    }
}
