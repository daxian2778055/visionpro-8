using System;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// Modbus RTU CRC-16 回归测试。
/// ch:R14 起改为直接测生产代码：Tests/UnitTests.csproj 链接了
/// WindowsFormsApplication1/Core/ModbusCrc.cs —— 也就是生产 Modbus.GetCRC16 正在调用的那份实现。
/// 断言它与一份独立的教科书级 CRC-16/MODBUS 参考实现(CrcReference)一致。
/// 原实现是"逐行复刻 CrcProduction()"：复刻只在写下的那一刻与生产一致，
/// 之后生产被改动它不会跟着变，测试形同虚设。
/// </summary>
public class ModbusCrcTests
{
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
        Assert.Equal(CrcReference(input), ModbusCrc.Append(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_ReadHoldingFrame()
    {
        // 站号01 / 功能码03 / 地址0x0000 / 数量0x0001
        var input = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        Assert.Equal(CrcReference(input), ModbusCrc.Append(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_MultiRegisterRead()
    {
        var input = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        Assert.Equal(CrcReference(input), ModbusCrc.Append(input));
    }

    [Fact]
    public void Production_AgreesWith_Reference_WriteMultipleFrame()
    {
        var input = new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x01, 0x00, 0x02 };
        Assert.Equal(CrcReference(input), ModbusCrc.Append(input));
    }

    [Fact]
    public void DifferentInputs_YieldDifferentCrc()
    {
        var a = ModbusCrc.Append(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 });
        var b = ModbusCrc.Append(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x02 });
        Assert.NotEqual(a[^1], b[^1]); // 数量不同 → CRC 不同
    }

    [Fact]
    public void Append_KeepsOriginalBytes_AndAppendsTwo()
    {
        // ch:R14 补一条真代码的结构性断言：原帧必须原样保留，且只多两个校验字节
        var input = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        var got = ModbusCrc.Append(input);
        Assert.Equal(input.Length + 2, got.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], got[i]);
    }

    // 注：不为 null 入参写断言 —— 生产 Modbus.GetCRC16 对 null 原本就抛 NRE（无入参校验），
    //     若为了测试好看去补 ArgumentNullException，等于在这轮"纯重构"里悄悄改了生产行为。
}
