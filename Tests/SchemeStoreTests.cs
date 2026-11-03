using System;
using System.Collections.Generic;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R12 方案表数据层回归。
///   Tests/UnitTests.csproj 直链 WindowsFormsApplication1/Core/SchemeStore.cs。
///   现场语义钉住：无 count 键的老 ini 必须仍按 8 行装载（零迁移兼容）；
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
        int h; string p;
        Assert.True(store.TryResolve("K8", out p, out h));
        Assert.Equal("P8", p);
    }

    [Fact]
    public void LoadFrom_CountTwelve_ReadsTwelveRows()
    {
        var store = new SchemeStore();
        store.LoadFrom(i => "K" + i, i => "P" + i, "12");
        Assert.Equal(12, store.KeysArray().Length);
        int h; string p;
        Assert.True(store.TryResolve("K12", out p, out h));
        Assert.Equal("P12", p);
    }

    [Fact]
    public void SetRows_ClampsToMaxAndSnapshotMatches()
    {
        var store = new SchemeStore();
        var rows = new List<string[]>();
        for (int i = 0; i < 150; i++) rows.Add(new[] { "S" + i, "p" + i });
        int dropped = store.SetRows(rows);
        Assert.Equal(50, dropped); // ch:R13-4 返回值契约：超出 100 的行数
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

    [Fact]
    public void TryResolve_PairedSnapshot_UnderConcurrentResizes()
    {
        // ch:R13-1 回归钉：100 行↔8 行整表改写与解析并发时，keys/paths 必须始终配对——
        //   旧实现分两个 volatile 数组发布，读侧可"旧 keys+新 paths"→ paths[hit] 越界或错路径
        var store = new SchemeStore();
        var big = new List<string[]>();
        for (int i = 0; i < SchemeStore.MaxSchemes; i++) big.Add(new[] { "K" + i, "P" + i });
        var small = new List<string[]>();
        for (int i = 0; i < SchemeStore.LegacyRows; i++) small.Add(new[] { "K" + i, "P" + i });
        store.SetRows(big);

        var stop = new int[1];
        var writer = new System.Threading.Thread(() =>
        {
            var rng = new Random(20261103);
            while (System.Threading.Interlocked.CompareExchange(ref stop[0], 0, 0) == 0)
                store.SetRows(rng.Next(2) == 0 ? big : small);
        }) { IsBackground = true };
        writer.Start();

        int hit99 = 0;
        var ex = Record.Exception(() =>
        {
            for (int n = 0; n < 200000; n++)
            {
                int h; string p;
                if (store.TryResolve("K99", out p, out h))
                {
                    hit99++;
                    Assert.Equal("P99", p); // 命中了就必须是配对的第 99 行路径
                }
                // K7 在大表/小表中都存在：必须每次都唯一命中，防止 TryResolve 退化为恒 false 时测试空过
                Assert.True(store.TryResolve("K7", out p, out h));
                Assert.Equal(1, h);
                Assert.Equal("P7", p);
            }
        });
        System.Threading.Interlocked.Exchange(ref stop[0], 1);
        writer.Join(2000);
        Assert.Null(ex);
        Assert.True(hit99 > 0); // 确保 100 行快照确实被解析过，而不是只跑了小表
    }

    [Fact]
    public void TrimTrailingEmpty_NullRows_TreatedAsEmpty()
    {
        // ch:R13-3 null 行契约对齐 SetRows/FindDuplicates：不得 NRE
        var rows = new List<string[]> { new[] { "A", "1" }, null, new[] { "", "" } };
        var trimmed = SchemeStore.TrimTrailingEmpty(rows);
        Assert.Single(trimmed);
        Assert.Null(SchemeStore.FindDuplicates(new List<string[]> { null, new[] { "B", "" }, null }));
    }
}
