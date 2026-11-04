using System;
using System.Collections.Generic;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:R45 方案表段解析回归：
///   Form3（无协议/串口）迁到 [change_serial]/[path_serial] 后，旧段 [change]/[path] 归 Omron；
///   新段没有 count 键时必须回退读旧段（现场升级零迁移），有 count=0 时则保持空表、不能回退。
/// </summary>
public class SerialSchemeSectionTests
{
    private static Func<string, string, string, string> Reader(Dictionary<string, string> values)
    {
        return (section, key, def) =>
        {
            string v;
            return values.TryGetValue(section + "/" + key, out v) ? v : def;
        };
    }

    [Fact]
    public void NewSectionPresent_UsesSerialSections()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "change_serial/count", "3" },
            { "change/count", "8" }
        });

        string change, path, count;
        Assert.True(SerialSchemeSection.Resolve(read, out change, out path, out count));
        Assert.Equal("change_serial", change);
        Assert.Equal("path_serial", path);
        Assert.Equal("3", count);
    }

    [Fact]
    public void NewSectionMissing_FallsBackToOldSections()
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "change/count", "8" }
        });

        string change, path, count;
        Assert.False(SerialSchemeSection.Resolve(read, out change, out path, out count));
        Assert.Equal("change", change);
        Assert.Equal("path", path);
        Assert.Equal("8", count);
    }

    [Fact]
    public void EmptyTable_CountZero_StaysOnNewSection()
    {
        // 现场把 Form3 方案表清空后保存 count=0，不能又回退去读 Omron 的 8 行
        var read = Reader(new Dictionary<string, string>
        {
            { "change_serial/count", "0" },
            { "change/count", "8" }
        });

        string change, path, count;
        Assert.True(SerialSchemeSection.Resolve(read, out change, out path, out count));
        Assert.Equal("0", count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNewCount_FallsBackToOldSections(string ownCount)
    {
        var read = Reader(new Dictionary<string, string>
        {
            { "change_serial/count", ownCount },
            { "change/count", "7" }
        });

        string change, path, count;
        Assert.False(SerialSchemeSection.Resolve(read, out change, out path, out count));
        Assert.Equal("change", change);
        Assert.Equal("7", count);
    }

    [Fact]
    public void LegacyCountMissing_ReturnsEmptyForSchemeStoreDefault()
    {
        // 老 ini 没有 count 键：SchemeStore.ResolveCount("") 会回退 8 行，这里原样透传空串即可
        var read = Reader(new Dictionary<string, string>());

        string change, path, count;
        Assert.False(SerialSchemeSection.Resolve(read, out change, out path, out count));
        Assert.Equal(string.Empty, count);
    }

    [Fact]
    public void NullReader_Throws()
    {
        string change, path, count;
        Assert.Throws<ArgumentNullException>(
            () => SerialSchemeSection.Resolve(null, out change, out path, out count));
    }
}
