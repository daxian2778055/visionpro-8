using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// 999 故障判定逻辑的回归测试。
/// 复刻 Form1.getrecord 的判定语义：
///   faultThisFrame = runError || inputAssignFailed
///   数据通道(TCP/串口/Omron/ModbusTCP/RTU): faultThisFrame ? "999" : 原值
///   IO 通道: faultThisFrame ? NG 脉冲 : 正常 OK/NG
/// 验证"故障帧绝不把旧结果当成正常值发出去"。
/// </summary>
public class FaultFrameTests
{
    private static bool FaultThisFrame(bool runError, bool inputAssignFailed) => runError || inputAssignFailed;

    private static string DataChannelValue(bool runError, bool inputAssignFailed, string normal)
        => FaultThisFrame(runError, inputAssignFailed) ? "999" : normal;

    [Theory]
    [InlineData(true, false, "Accept")]  // 脚本报错
    [InlineData(false, true, "Accept")]  // 传图失败
    [InlineData(true, true, "Accept")]   // 两者
    [InlineData(false, false, "Reject")]
    [InlineData(false, false, "123")]
    public void DataChannel_Emits999_OnlyOnFaultFrame(bool runError, bool inputAssignFailed, string normal)
    {
        var got = DataChannelValue(runError, inputAssignFailed, normal);
        if (FaultThisFrame(runError, inputAssignFailed))
            Assert.Equal("999", got);
        else
            Assert.Equal(normal, got);
    }

    [Fact]
    public void DataChannel_NormalFrame_PassesThroughOriginalValue()
    {
        Assert.Equal("Reject", DataChannelValue(runError: false, inputAssignFailed: false, normal: "Reject"));
        Assert.Equal("42", DataChannelValue(false, false, "42"));
    }

    [Fact]
    public void IoPulse_FaultFrame_AlwaysNg()
    {
        // 故障帧 IO 一律放 NG（拦截），不看正常判定
        Assert.False(IoIsOkPulse(runError: true, inputAssignFailed: false));
        Assert.False(IoIsOkPulse(false, true));
        // 非故障帧才可能 OK
        Assert.True(IoIsOkPulse(false, false));
    }

    private static bool IoIsOkPulse(bool runError, bool inputAssignFailed)
        => !FaultThisFrame(runError, inputAssignFailed);

    [Fact]
    public void FaultFlag_CoveresBothRunErrorAndInputAssignFailed()
    {
        Assert.True(FaultThisFrame(true, false));
        Assert.True(FaultThisFrame(false, true));
        Assert.False(FaultThisFrame(false, false));
    }
}
