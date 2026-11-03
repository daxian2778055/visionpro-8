using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R12 方案切换载图串匹配器回归。
///   Tests/UnitTests.csproj 直链 WindowsFormsApplication1/Core/SchemeMatcher.cs ——
///   测的是四条通讯通道 qiehuan() 正在用的那份实现。
///   语义钉三条：空载图串不参与；仅唯一命中生效；多命中=冲突拒绝并回报次数（原 if 链先到先得会静默切错方案）。
/// </summary>
public class SchemeMatcherTests
{
    [Fact]
    public void UniqueHit_ReturnsIndexOfMatchingKey()
    {
        string[] keys = { "A1", "B2", "", "C3" };
        int hits;
        Assert.Equal(3, SchemeMatcher.Match(keys, "C3", out hits));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void DuplicateKeys_ReturnConflictWithHitCount()
    {
        string[] keys = { "X1", "X1", "X1" };
        int hits;
        Assert.Equal(SchemeMatcher.Conflict, SchemeMatcher.Match(keys, "X1", out hits));
        Assert.Equal(3, hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyInput_NeverMatches(string input)
    {
        // 空键不参与 + 空输入不匹配：原 if 链在"文本框空且读回空串"时会误命中第一路
        string[] keys = { "", "A1" };
        int hits;
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(keys, input, out hits));
        Assert.Equal(0, hits);
    }

    [Fact]
    public void NoMatchingKey_ReturnsNoHit()
    {
        string[] keys = { "A1", "B2" };
        int hits;
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(keys, "ZZ", out hits));
        Assert.Equal(0, hits);
    }

    [Fact]
    public void NullKeys_ReturnsNoHit()
    {
        int hits;
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(null, "A1", out hits));
    }

    [Fact]
    public void MaxScales_HundredRowTable_LastRowHits()
    {
        // 100 路量级：末行唯一命中要能取到（为表格化 UI 放开数量前钉住行为）
        string[] keys = new string[SchemeMatcher.MaxSchemes];
        for (int i = 0; i < keys.Length; i++) keys[i] = "S" + i;
        int hits;
        Assert.Equal(SchemeMatcher.MaxSchemes - 1, SchemeMatcher.Match(keys, "S99", out hits));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void ExactMatchOnly_NoSubstringOrTrimGames()
    {
        // 现场协议为定长精确指令码：子串/前后空格都不得命中
        string[] keys = { "A1B1" };
        int hits;
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(keys, "A1", out hits));
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(keys, "A1B1 ", out hits));
    }
}
