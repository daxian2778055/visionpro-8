using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

public class MonitorGridLayoutTests
{
    private static bool[] Enabled(params int[] cameraNumbers)
    {
        var enabled = new bool[8];
        foreach (int n in cameraNumbers) enabled[n - 1] = true;
        return enabled;
    }

    [Fact]
    public void FourCameras_UseExpectedSlots_Camera4BottomRight()
    {
        MonitorGridLayout layout = MonitorGridLayout.Create(Enabled(1, 2, 3, 4));

        Assert.Equal(4, layout.EnabledCount);
        Assert.Equal(new[] { 0, 1, 3, 4 }, layout.Slots);
        Assert.False(layout.SpanSingleCamera);
        Assert.Equal(1, layout.Slots[3] % 3); // 相机4：右下（列1、行1）
        Assert.Equal(1, layout.Slots[3] / 3);
    }

    [Fact]
    public void FourCameras_NonContiguousStillPackByEnabledOrder()
    {
        MonitorGridLayout layout = MonitorGridLayout.Create(Enabled(1, 2, 4, 8));

        Assert.Equal(4, layout.EnabledCount);
        Assert.Equal(new[] { 0, 1, 3, 4 }, layout.Slots);
    }

    [Fact]
    public void ThreeCameras_LeaveBottomRightEmpty_ForRuntimeFourthCamera()
    {
        MonitorGridLayout three = MonitorGridLayout.Create(Enabled(1, 2, 3));
        Assert.Equal(new[] { 0, 1, 3 }, three.Slots);

        MonitorGridLayout four = MonitorGridLayout.Create(Enabled(1, 2, 3, 4));
        Assert.Equal(new[] { 0, 1, 3, 4 }, four.Slots);
    }

    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 0 })]
    [InlineData(2, new[] { 0, 1 })]
    [InlineData(5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(6, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(7, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData(8, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    public void CountBoundaries_UseExpectedSlotSets(int count, int[] expected)
    {
        var cameraNumbers = new int[count];
        for (int i = 0; i < count; i++) cameraNumbers[i] = i + 1;

        MonitorGridLayout layout = MonitorGridLayout.Create(Enabled(cameraNumbers));

        Assert.Equal(count, layout.EnabledCount);
        Assert.Equal(expected, layout.Slots);
        Assert.Equal(count <= 1, layout.SpanSingleCamera);
    }

    [Fact]
    public void Create_RejectsWrongCameraStateLength()
    {
        Assert.Throws<System.ArgumentException>(() => MonitorGridLayout.Create(new bool[7]));
    }
}
