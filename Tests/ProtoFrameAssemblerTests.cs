using System.Collections.Generic;
using System.Text;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R42 TCP 触发串组帧器回归。
///   Tests/UnitTests.csproj 直链 WindowsFormsApplication1/Core/ProtoFrameAssembler.cs ——
///   测的是生产收包路径正在用的那份实现。
///   现场协议：socket 上只有若干条「精确匹配的触发串」、无分隔符/无长度前缀；
///   TCP 分段/合并时旧的「整段等值比较」会静默丢触发，本组件负责按已知串组帧。
/// </summary>
public class ProtoFrameAssemblerTests
{
    private static List<byte[]> P(params string[] ss)
    {
        var l = new List<byte[]>();
        foreach (var s in ss) l.Add(Encoding.ASCII.GetBytes(s));
        return l;
    }

    private static List<string> S(List<byte[]> frames)
    {
        var l = new List<string>();
        foreach (var f in frames) l.Add(Encoding.ASCII.GetString(f));
        return l;
    }

    [Fact]
    public void 整段完整触发串_直接切出()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "A1" }, S(a.Append(Encoding.ASCII.GetBytes("A1"), 2, P("A1", "B1"))));
    }

    [Fact]
    public void 合并两触发串_依次切出()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "A1", "B1" }, S(a.Append(Encoding.ASCII.GetBytes("A1B1"), 4, P("A1", "B1"))));
    }

    [Fact]
    public void 分段触发串_拼全后才切出()
    {
        var a = new ProtoFrameAssembler();
        var pats = P("A1", "B1");
        Assert.Empty(S(a.Append(Encoding.ASCII.GetBytes("A"), 1, pats)));       // 前缀暂存，不误发
        Assert.Equal(new[] { "A1" }, S(a.Append(Encoding.ASCII.GetBytes("1"), 1, pats)));
    }

    [Fact]
    public void 前置未知字节_先残留后触发()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "xx", "A1" }, S(a.Append(Encoding.ASCII.GetBytes("xxA1"), 4, P("A1", "B1"))));
    }

    [Fact]
    public void 尾随未知字节_先触发后残留()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "A1", "xx" }, S(a.Append(Encoding.ASCII.GetBytes("A1xx"), 4, P("A1", "B1"))));
    }

    [Fact]
    public void 纯未知段_原样交回()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "HELLO" }, S(a.Append(Encoding.ASCII.GetBytes("HELLO"), 5, P("A1", "B1"))));
    }

    [Fact]
    public void 未知段尾巴是触发前缀_保留等待不误发()
    {
        var a = new ProtoFrameAssembler();
        var pats = P("A1", "B1");
        Assert.Equal(new[] { "xx" }, S(a.Append(Encoding.ASCII.GetBytes("xxA"), 3, pats)));
        Assert.Equal(new[] { "A1" }, S(a.Append(Encoding.ASCII.GetBytes("1"), 1, pats)));
    }

    [Fact]
    public void 跨段_未知加触发加半截前缀()
    {
        var a = new ProtoFrameAssembler();
        var pats = P("A1", "B1");
        Assert.Equal(new[] { "junk", "A1" }, S(a.Append(Encoding.ASCII.GetBytes("junkA1B"), 7, pats)));
        Assert.Equal(new[] { "B1" }, S(a.Append(Encoding.ASCII.GetBytes("1"), 1, pats)));
    }

    [Fact]
    public void 连续重复合并_不丢不重()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "A1", "A1", "B1" }, S(a.Append(Encoding.ASCII.GetBytes("A1A1B1"), 6, P("A1", "B1"))));
    }

    [Fact]
    public void 未配置触发串_整段原样退化为旧行为()
    {
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "XYZ" }, S(a.Append(Encoding.ASCII.GetBytes("XYZ"), 3, P())));
    }

    [Fact]
    public void Reset_丢弃半截_不串到新连接()
    {
        var a = new ProtoFrameAssembler();
        var pats = P("A1", "B1");
        Assert.Empty(S(a.Append(Encoding.ASCII.GetBytes("A"), 1, pats)));
        a.Reset();
        Assert.Equal(new[] { "1" }, S(a.Append(Encoding.ASCII.GetBytes("1"), 1, pats))); // 半截被清，"1" 是残留
    }

    [Fact]
    public void 多字节串_按字节组帧不拆码()
    {
        // 触发串 <- 0xD6 0xD0（GBK「中」）；两个 TCP 段各带一半，拼全后才输出整串两字节
        var a = new ProtoFrameAssembler();
        var pats = new List<byte[]> { new byte[] { 0xD6, 0xD0 } };
        Assert.Empty(a.Append(new byte[] { 0xD6 }, 1, pats));
        var outp = a.Append(new byte[] { 0xD0 }, 1, pats);
        Assert.Single(outp);
        Assert.Equal(new byte[] { 0xD6, 0xD0 }, outp[0]);
    }

    [Fact]
    public void 前缀包含关系_按左端最长立即切分_文档化行为()
    {
        // 约束：触发串宜互不为前缀。若配置存在包含（T 与 T1），按左端最长优先：
        // "T1" 命中长串；单独的 "T" 立即切出（不等后续字节，避免真实短串触发被拖延）
        var a = new ProtoFrameAssembler();
        Assert.Equal(new[] { "T1" }, S(a.Append(Encoding.ASCII.GetBytes("T1"), 2, P("T", "T1"))));
        var b = new ProtoFrameAssembler();
        Assert.Equal(new[] { "T" }, S(b.Append(Encoding.ASCII.GetBytes("T"), 1, P("T", "T1"))));
    }
}
