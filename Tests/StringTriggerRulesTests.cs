using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:字符串触发规则回归（直链 Core/StringTriggerRules.cs）。
///   钉死：只有 string+触发 才是字符串触发通道；清零串按长度生成 ASCII '0'；
///   产品编码去掉 NUL/空白（PLC 写 0x0000 时读出来是 NUL）。
/// </summary>
public class StringTriggerRulesTests
{
    [Theory]
    [InlineData("string", "触发", true)]
    [InlineData("STRING", "触发信号", true)]
    [InlineData("string", "反馈", false)]
    [InlineData("int", "触发", false)]
    [InlineData("", "触发", false)]
    [InlineData("string", "", false)]
    [InlineData(null, null, false)]
    public void IsStringTriggerChannel_OnlyStringAndTrigger(string format, string usage, bool expected)
    {
        Assert.Equal(expected, StringTriggerRules.IsStringTriggerChannel(format, usage));
    }

    [Theory]
    [InlineData(3, "000000")]   // 3 寄存器 × 2 字符
    [InlineData(1, "00")]
    [InlineData(6, "000000000000")]
    [InlineData(0, "00")]       // 夹到 1 寄存器
    [InlineData(-5, "00")]
    public void BuildClearValue_TwoCharsPerRegister(int registerCount, string expected)
    {
        Assert.Equal(expected, StringTriggerRules.BuildClearValue(registerCount));
    }

    [Fact]
    public void BuildClearValue_ClampsTooLongLength()
    {
        string s = StringTriggerRules.BuildClearValue(999);
        Assert.Equal(StringTriggerRules.MaxStringLength * StringTriggerRules.CharsPerRegister, s.Length);
        Assert.Equal(StringTriggerRules.ClearChar, s[0]);
    }

    [Fact]
    public void ByteLengthAndRegisterClear_MatchLayout()
    {
        Assert.Equal(6, StringTriggerRules.ToByteLength(3));      // 3 寄存器 → 6 字节
        Assert.Equal("00", StringTriggerRules.BuildRegisterClear()); // 单寄存器清零 = "00"
        Assert.Equal(2, StringTriggerRules.CharsPerRegister);
    }

    [Theory]
    [InlineData("SN12345", "SN12345")]
    [InlineData("\0A\0B\0", "AB")]
    [InlineData("  SN001  ", "SN001")]
    [InlineData("\0", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeProductCode_StripsNulAndWhitespace(string raw, string expected)
    {
        Assert.Equal(expected, StringTriggerRules.NormalizeProductCode(raw));
    }
}
