using System;
using System.Collections.Generic;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R44 相机10方案切换配置防呆回归：
///   同一台设备只允许一个 PLC 通道启用 cam10；勾选时检查另外两段 ini。
/// </summary>
public class SchemeSwitchConfigTests
{
    private static Func<string, string, string, string> Reader(Dictionary<string, string> values)
    {
        return (section, key, def) =>
        {
            string k = section + "/" + key;
            string v;
            return values.TryGetValue(k, out v) ? v : def;
        };
    }

    [Fact]
    public void FindOtherEnabled_ReturnsOtherChannel()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "c10/fanhuien", "true" }
        });

        Assert.Equal("欧姆龙", SchemeSwitchConfig.FindOtherEnabledCam10(read, SchemeSwitchConfig.ModbusTcpSection));
    }

    [Fact]
    public void FindOtherEnabled_IgnoresCurrentSection()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "c10_modbustcp/fanhuien", "true" }
        });

        Assert.Null(SchemeSwitchConfig.FindOtherEnabledCam10(read, SchemeSwitchConfig.ModbusTcpSection));
    }

    [Fact]
    public void FindOtherEnabled_NoEnabledChannel_ReturnsNull()
    {
        var read = Reader(new Dictionary<string, string>());
        Assert.Null(SchemeSwitchConfig.FindOtherEnabledCam10(read, SchemeSwitchConfig.OmronSection));
    }

    [Fact]
    public void FindOtherEnabled_TrueIsTrimmedAndCaseInsensitive()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "c10modbusrtu/fanhuien", " TRUE " }
        });

        Assert.Equal("Modbus RTU", SchemeSwitchConfig.FindOtherEnabledCam10(read, SchemeSwitchConfig.OmronSection));
    }

    [Fact]
    public void FindAllEnabled_TwoChannels_ReturnsConflict()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "c10/fanhuien", "true" },
            { "c10_modbustcp/fanhuien", "true" }
        });

        Assert.Equal("欧姆龙、Modbus TCP", SchemeSwitchConfig.FindAllEnabledCam10(read));
    }

    [Fact]
    public void FindAllEnabled_OneChannel_ReturnsNull()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "c10/fanhuien", "true" }
        });

        Assert.Null(SchemeSwitchConfig.FindAllEnabledCam10(read));
    }
}
