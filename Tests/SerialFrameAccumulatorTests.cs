using System.Collections.Generic;
using System.Text;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:v1.3.54 串口帧累积器回归（直链 Core\SerialFrameAccumulator.cs）。
///   钉住四类真实场景：分包重组、GBK 多字节切开重组、"1"/"12" 前缀串跨读取不误切、未知数据空闲整段派发。
///   累积器只认字节，测试直接传字节模式，不依赖具体编码。
/// </summary>
public class SerialFrameAccumulatorTests
{
    private static List<byte[]> P(params string[] ascii)
    {
        var l = new List<byte[]>();
        foreach (var s in ascii) l.Add(Encoding.ASCII.GetBytes(s));
        return l;
    }
    private static string S(byte[] b) { return Encoding.ASCII.GetString(b); }

    [Fact]
    public void WholeFrameOneRead_Emitted()
    {
        var acc = new SerialFrameAccumulator();
        var buf = Encoding.ASCII.GetBytes("AB");
        var frames = acc.Append(2, buf, P("AB"));
        Assert.Equal(new[] { "AB" }, System.Array.ConvertAll(frames.ToArray(), S));
    }

    [Fact]
    public void SplitAtEveryPosition_Reassembles()
    {
        var pat = Encoding.ASCII.GetBytes("ABCD");
        for (int split = 1; split < pat.Length; split++)
        {
            var acc = new SerialFrameAccumulator();
            var first = new byte[split];
            System.Array.Copy(pat, first, split);
            var rest = new byte[pat.Length - split];
            System.Array.Copy(pat, split, rest, 0, rest.Length);
            Assert.Empty(acc.Append(first.Length, first, P("ABCD"))); // 分包前半：等待
            var frames = acc.Append(rest.Length, rest, P("ABCD"));   // 后半到齐：整帧
            Assert.Equal("ABCD", S(frames[0]));
        }
    }

    [Fact]
    public void MultiBytePattern_SplitMid_ReassemblesNoGarbage()
    {
        // 模拟 GBK 双字节：模式用两字节 {0xA1,0xA2}，在字节边界切开也应拼回、绝不产出半字节帧
        var pat = new List<byte[]> { new byte[] { 0xA1, 0xA2 } };
        var acc = new SerialFrameAccumulator();
        Assert.Empty(acc.Append(1, new byte[] { 0xA1 }, pat));   // 前半：真前缀，等待
        var f = acc.Append(1, new byte[] { 0xA2 }, pat);          // 后半到齐
        Assert.Single(f);
        Assert.Equal(new byte[] { 0xA1, 0xA2 }, f[0]);
    }

    [Fact]
    public void PrefixConfig_1and12_SplitDoesNotMisSwitch()
    {
        // P1 核心用例："1"/"12" 都配置，设备发 "12" 被分包成 "1"+"2"：不得先切 "1"
        var acc = new SerialFrameAccumulator();
        Assert.Empty(acc.Append(1, new byte[] { (byte)'1' }, P("1", "12"))); // "1" 恰吃满但为 "12" 前缀 → 等待
        var f = acc.Append(1, new byte[] { (byte)'2' }, P("1", "12"));        // "2" 到齐 → 整帧 "12"
        Assert.Equal(new[] { "12" }, System.Array.ConvertAll(f.ToArray(), S));
    }

    [Fact]
    public void GenuineShortString_SettlesAfterPrefixWait()
    {
        // 设备确实只发 "1"（"12" 也配置）：连续空读到 settle 上限后产出短串 "1"
        var acc = new SerialFrameAccumulator();
        Assert.Empty(acc.Append(1, new byte[] { (byte)'1' }, P("1", "12")));
        List<byte[]> last = null;
        for (int i = 0; i < SerialFrameAccumulator.PrefixWaitMaxPolls; i++)
        {
            last = acc.Append(0, new byte[0], P("1", "12"));
            if (last.Count > 0) break;
        }
        Assert.NotNull(last);
        Assert.Single(last);
        Assert.Equal("1", S(last[0]));
    }

    [Fact]
    public void UnknownData_FlushesWholeAfterIdle()
    {
        var acc = new SerialFrameAccumulator();
        Assert.Empty(acc.Append(2, Encoding.ASCII.GetBytes("XY"), P("AB"))); // 未知，非前缀
        Assert.Empty(acc.Append(0, new byte[0], P("AB")));                    // idle=1
        var f = acc.Append(0, new byte[0], P("AB"));                          // idle=2 → 整段产出
        Assert.Equal("XY", S(f[0]));
    }

    [Fact]
    public void MultipleFramesOneRead_AllEmitted()
    {
        var acc = new SerialFrameAccumulator();
        var f = acc.Append(4, Encoding.ASCII.GetBytes("ABCD"), P("AB", "CD"));
        Assert.Equal(new[] { "AB", "CD" }, System.Array.ConvertAll(f.ToArray(), S));
    }

    [Fact]
    public void MaxFrame_HardCap()
    {
        var acc = new SerialFrameAccumulator();
        var big = new byte[SerialFrameAccumulator.MaxFrame + 500];
        var f = acc.Append(big.Length, big, P("ZZ")); // 无匹配 → 溢出整段产出，pending 不超上限
        Assert.True(f.Count >= 1);
        Assert.True(acc.PendingCount <= SerialFrameAccumulator.MaxFrame);
    }

    [Fact]
    public void Reset_ClearsPending()
    {
        var acc = new SerialFrameAccumulator();
        acc.Append(1, new byte[] { (byte)'A' }, P("AB")); // 前缀等待，pending="A"
        Assert.Equal(1, acc.PendingCount);
        acc.Reset();
        Assert.Equal(0, acc.PendingCount);
    }

    [Fact]
    public void FindPrefixConflicts_DetectsPrefixAndIgnoresUnrelated()
    {
        var rows = new List<string[]> { new[] { "1", "p1" }, new[] { "12", "p2" }, new[] { "9", "p3" } };
        string c = SchemeStore.FindPrefixConflicts(rows, null);
        Assert.NotNull(c);
        Assert.Contains("[1]", c);
        Assert.Contains("[12]", c);
        // 无互前缀关系 → null
        var ok = new List<string[]> { new[] { "1", "p1" }, new[] { "2", "p2" }, new[] { "10", "p3" } };
        // "1" 是 "10" 的前缀 → 仍冲突
        Assert.NotNull(SchemeStore.FindPrefixConflicts(ok, null));
        var none = new List<string[]> { new[] { "A1", "p1" }, new[] { "B2", "p2" } };
        Assert.Null(SchemeStore.FindPrefixConflicts(none, null));
    }

    [Fact]
    public void FindPrefixConflicts_CrossChecksExtraCameraTriggers()
    {
        var rows = new List<string[]> { new[] { "1", "p1" } };
        string c = SchemeStore.FindPrefixConflicts(rows, new[] { "10" }); // 相机触发串 "10" 以方案串 "1" 开头
        Assert.NotNull(c);
        Assert.Contains("相机触发串", c);
    }
}
