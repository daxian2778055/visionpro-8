using System;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R44 相机10方案切换防呆回归：
///   同一通道重复值只允许切一次；不同通道互不影响；MarkHandled 表示“已与全局方案一致”，
///   防止其它通道切走后本通道再切回来（P0-2 互切环）。
/// </summary>
public class Cam10ChangeTrackerTests
{
    [Fact]
    public void FirstRequest_IsAccepted()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
    }

    [Fact]
    public void SameSourceSamePath_OnlyAcceptedOnce()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.False(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.False(tracker.ShouldSwitch("omron", @"d:\方案\a.vpp")); // Windows 路径大小写不敏感
    }

    [Fact]
    public void SameSourceDifferentPath_IsAcceptedAgain()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\B.vpp"));
    }

    [Fact]
    public void DifferentSources_AreIndependent()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.True(tracker.ShouldSwitch("modbustcp", @"D:\方案\B.vpp"));
        Assert.False(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.False(tracker.ShouldSwitch("modbustcp", @"D:\方案\B.vpp"));
    }

    [Fact]
    public void MarkHandled_PreventsLaterBounceBack()
    {
        var tracker = new Cam10ChangeTracker();
        tracker.MarkHandled("omron", @"D:\方案\A.vpp");
        Assert.False(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\B.vpp"));
    }

    [Fact]
    public void Forget_AllowsRetry()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        Assert.False(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
        tracker.Forget("omron");
        Assert.True(tracker.ShouldSwitch("omron", @"D:\方案\A.vpp"));
    }

    [Fact]
    public void EmptySourceOrPath_IsRejected()
    {
        var tracker = new Cam10ChangeTracker();
        Assert.False(tracker.ShouldSwitch("", @"D:\方案\A.vpp"));
        Assert.False(tracker.ShouldSwitch("omron", ""));
        Assert.False(tracker.ShouldSwitch("omron", null));
    }
}
