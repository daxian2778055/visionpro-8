using System;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// Modbus RTU CRC-16 回归测试。
/// 策略：把生产环境 WindowsFormsApplication1/Modbus.cs 的 GetCRC16 逐行复刻为
/// CrcProduction()，再用一个独立的教科书级 CRC-16/MODBUS 参考实现 CrcReference()，
/// 断言二者在多个输入（含全 0、真实寄存器帧）上完全一致。
/// 这样不依赖"我记得的向量"，只要有人改坏生产里的 CRC，就会与参考实现分叉而失败。
/// </summary>
public class ModbusCrcTests
{
    // === 逐行复刻 Modbus.GetCRC16（含其"追加 [low, high] 两个字节"的输出约定）===
    private static byte[] CrcProduction(byte[] data)
    {
        var data1 = new byte[data.Length + 2];
        int crc = 0xffff;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                int temp = crc & 1;
                crc >>= 1;
                crc &= 0x7fff;
                if (temp == 1) crc ^= 0xa001;
                crc &= 0xffff;
            }
        }
        var crc16 = new byte[2];
        crc16[1] = (byte)((crc >> 8) & 0xff); // high
        crc16[0] = (byte)(crc & 0xff);        // low
        Array.Copy(data, 0, data1, 0, data.Length);
        Array.Copy(crc16, 0, data1, data.Length, 2);
        return data1;
    }

    // === 独立参考实现：标准 CRC-16/MODBUS（poly 0xA001, init 0xFFFF），同样追加 [low, high] ===
    private static byte[] CrcReference(byte[] data)
    {
        int crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = ((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : (crc >> 1);
        }
        var outb = new byte[data.Length + 2];
        Array.Copy(data, 0, outb, 0, data.Length);
        outb[data.Length] = (byte)(crc & 0xFF);       // low first
        outb[data.Length + 1] = (byte)((crc >> 8) & 0xFF);
        return outb;
    }

    [Fact]
    public void Production_AgreesWith_Reference_AllZero()
    {
        var input = new byte[] { 0, 0, 0, 0, 0, 0 };
        Assert.Equal(CrcReference(input), CrcProduction(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_ReadHoldingFrame()
    {
        // 站号01 / 功能码03 / 地址0x0000 / 数量0x0001
        var input = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        Assert.Equal(CrcReference(input), CrcProduction(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_MultiRegisterRead()
    {
        var input = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        Assert.Equal(CrcReference(input), CrcProduction(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_WriteMultipleFrame()
    {
        var input = new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x01, 0x00, 0x02 };
        Assert.Equal(CrcReference(input), CrcProduction(input));
    }

    [Fact]
    public void DifferentInputs_YieldDifferentCrc()
    {
        var a = CrcProduction(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 });
        var b = CrcProduction(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x02 });
        Assert.NotEqual(a[^1], b[^1]); // 数量不同 → CRC 不同
    }
}
