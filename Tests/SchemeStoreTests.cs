using System;
using System.Collections.Generic;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R12 方案表数据层回归。
///   Tests/UnitTests.csproj 直链 WindowsFormsApplication1/Core/SchemeStore.cs。
///   现场语义钉住：无 scheme_count 的老 ini 必须仍按 8 行装载（零迁移兼容）；
///   超上限夹 100；重复检测与尾部空行裁剪决定保存内容。
/// </summary>
public class SchemeStoreTests
{
    [Theory]
    [InlineData(null, 8)]
    [InlineData("", 8)]
    [InlineData("abc", 8)]
    [InlineData("0", 0)]
    [InlineData("-5", 0)]
    [InlineData(" 33 ", 33)]
    [InlineData("150", 100)]
    public void ResolveCount_LegacyDefaultAndClamp(string raw, int expected)
    {
        Assert.Equal(expected, SchemeStore.ResolveCount(raw));
    }

    [Fact]
    public void LoadFrom_MissingCount_ReadsLegacyEightRows()
    {
        var store = new SchemeStore();
        store.LoadFrom(i => "K" + i, i => "P" + i, "");
        Assert.Equal(8, store.KeysArray().Length);
        Assert.Equal("K1", store.KeysArray()[0]);
        Assert.Equal("P8", store.PathsArray()[7]);
    }

    [Fact]
    public void LoadFrom_CountTwelve_ReadsTwelveRows()
    {
        var store = new SchemeStore();
        store.LoadFrom(i => "K" + i, i => "P" + i, "12");
        Assert.Equal(12, store.PathsArray().Length);
        Assert.Equal("K12", store.KeysArray()[11]);
    }

    [Fact]
    public void SetRows_ClampsToMaxAndSnapshotMatches()
    {
        var store = new SchemeStore();
        var rows = new List<string[]>();
        for (int i = 0; i < 150; i++) rows.Add(new[] { "S" + i, "p" + i });
        store.SetRows(rows);
        Assert.Equal(SchemeStore.MaxSchemes, store.KeysArray().Length);
        Assert.Equal("S99", store.KeysArray()[99]);

        int hits;
        Assert.Equal(99, SchemeMatcher.Match(store.KeysArray(), "S99", out hits));
    }

    [Fact]
    public void GetRows_ReturnsDetachedCopy()
    {
        var store = new SchemeStore();
        store.SetRows(new List<string[]> { new[] { "A", "1" }, new[] { "B", "2" } });
        var copy = store.GetRows();
        copy[0][0] = "MUTATED";
        Assert.Equal("A", store.KeysArray()[0]);
    }

    [Fact]
    public void FindDuplicates_IgnoresEmptyAndReportsRows()
    {
        var rows = new List<string[]> { new[] { "", "" }, new[] { "X", "p1" }, new[] { "Y", "p2" }, new[] { "X", "p3" } };
        string dup = SchemeStore.FindDuplicates(rows);
        Assert.NotNull(dup);
        Assert.Contains("第2行", dup);
        Assert.Contains("第4行", dup);
        Assert.Null(SchemeStore.FindDuplicates(new List<string[]> { new[] { "A", "" }, new[] { "", "" }, new[] { "B", "" } }));
    }

    [Fact]
    public void TrimTrailingEmpty_KeepsMiddleGapsCutsTail()
    {
        var rows = new List<string[]> { new[] { "A", "1" }, new[] { "", "" }, new[] { "B", "2" }, new[] { "", "" }, new[] { "", "" } };
        var trimmed = SchemeStore.TrimTrailingEmpty(rows);
        Assert.Equal(3, trimmed.Count);
        Assert.Equal("", trimmed[1][0]);
        Assert.Empty(SchemeStore.TrimTrailingEmpty(new List<string[]> { new[] { "", "" } }));
    }

    [Fact]
    public void SetRows_ReplacesWholesale_OldKeysDisappear()
    {
        var store = new SchemeStore();
        store.SetRows(new List<string[]> { new[] { "OLD", "1" } });
        store.SetRows(new List<string[]> { new[] { "NEW", "2" } });
        int hits;
        Assert.Equal(SchemeMatcher.NoHit, SchemeMatcher.Match(store.KeysArray(), "OLD", out hits));
        Assert.Equal(0, SchemeMatcher.Match(store.KeysArray(), "NEW", out hits));
    }
}
