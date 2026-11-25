using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

public class CameraCountStateTests
{
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(99, 8)]
    public void Normalize_ClampsToOneThroughEight(int input, int expected)
    {
        Assert.Equal(expected, CameraCountState.Normalize(input));
    }

    [Fact]
    public void CreateEnabled_FourCameras_EnablesOnlyOneThroughFour()
    {
        bool[] enabled = CameraCountState.CreateEnabled(4);

        Assert.Equal(new[] { true, true, true, true, false, false, false, false }, enabled);
        Assert.Equal(4, CameraCountState.CountEnabled(enabled));
    }

    [Fact]
    public void CreateEnabled_EightCameras_EnablesAll()
    {
        bool[] enabled = CameraCountState.CreateEnabled(8);

        Assert.Equal(8, CameraCountState.CountEnabled(enabled));
        Assert.All(enabled, value => Assert.True(value));
    }
}
