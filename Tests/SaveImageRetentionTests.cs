using System;
using System.Collections.Generic;
using System.IO;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:存图保留天数清理回归（直链 Core/SaveImageRetention.cs 测生产实现）。
///   钉死四件事：
///   ① 只删“日期名”的文件夹（root\日期 与 root\相机目录\日期 两层），名字不是日期一律不碰；
///   ② 保留最近 N 天含今天：keepDays=3、今天 11-15 → 删 11-12 及更早，11-13/11-15 保留；
///   ③ keepDays=0 或根目录不存在 → 一个都不删；
///   ④ 改过日期格式后，旧的 yyyy-MM-dd / yyyyMMdd 目录仍能被识别清掉（否则现场会越积越多）。
/// </summary>
public class SaveImageRetentionTests : IDisposable
{
    private readonly string _root;

    public SaveImageRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vp8_retention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeDateDir(string dateName, params string[] parents)
    {
        string dir = _root;
        for (int i = 0; i < parents.Length; i++)
            dir = Path.Combine(dir, parents[i]);
        dir = Path.Combine(dir, dateName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), new byte[] { 1, 2, 3 });
        return dir;
    }

    private static readonly DateTime Now = new DateTime(2026, 11, 15, 9, 0, 0);

    [Fact]
    public void Cleanup_DeletesOnlyFoldersOlderThanKeepDays()
    {
        string oldFlat = MakeDateDir("2026-11-10");
        string oldCam = MakeDateDir("2026-10-01", "啊啊啊");
        string boundaryOld = MakeDateDir("2026-11-12");
        string boundaryKeep = MakeDateDir("2026-11-13");
        string today = MakeDateDir("2026-11-15");
        string todayCam = MakeDateDir("2026-11-15", "啊啊啊");
        string notDate = Path.Combine(_root, "啊啊啊", "SN0001");
        Directory.CreateDirectory(notDate);

        List<string> log = new List<string>();
        int removed = SaveImageRetention.Cleanup(_root, "yyyy-MM-dd", 3, Now, log.Add);

        Assert.Equal(3, removed);
        Assert.False(Directory.Exists(oldFlat));
        Assert.False(Directory.Exists(oldCam));
        Assert.False(Directory.Exists(boundaryOld));
        Assert.True(Directory.Exists(boundaryKeep));
        Assert.True(Directory.Exists(today));
        Assert.True(Directory.Exists(todayCam));
        Assert.True(Directory.Exists(notDate));
        Assert.Contains(log, l => l.Contains("已删除") && l.Contains("2026-10-01"));
    }

    [Fact]
    public void Cleanup_KeepDaysZero_DeletesNothing()
    {
        string old = MakeDateDir("2020-01-01");

        int removed = SaveImageRetention.Cleanup(_root, "yyyy-MM-dd", 0, Now, null);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void Cleanup_MissingRoot_ReturnsZero()
    {
        Assert.Equal(0, SaveImageRetention.Cleanup(Path.Combine(_root, "不存在"), "yyyy-MM-dd", 7, Now, null));
        Assert.Equal(0, SaveImageRetention.Cleanup("", "yyyy-MM-dd", 7, Now, null));
        Assert.Equal(0, SaveImageRetention.Cleanup(null, "yyyy-MM-dd", 7, Now, null));
    }

    [Fact]
    public void Cleanup_KeepOneDay_DeletesEverythingBeforeToday()
    {
        string yesterday = MakeDateDir("2026-11-14");
        string today = MakeDateDir("2026-11-15");

        int removed = SaveImageRetention.Cleanup(_root, "yyyy-MM-dd", 1, Now, null);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(yesterday));
        Assert.True(Directory.Exists(today));
    }

    [Fact]
    public void Cleanup_AfterDateFormatChanged_StillFindsLegacyFolders()
    {
        string legacyDash = MakeDateDir("2026-11-01");
        string legacyCompact = MakeDateDir("20261102");
        string currentOld = MakeDateDir("20261105", "啊啊啊");
        string currentNew = MakeDateDir("20261114", "啊啊啊");

        // 现场把日期格式改成 yyyyMMdd 后，老的 yyyy-MM-dd 目录也要能清
        int removed = SaveImageRetention.Cleanup(_root, "yyyyMMdd", 7, Now, null);

        Assert.Equal(3, removed);
        Assert.False(Directory.Exists(legacyDash));
        Assert.False(Directory.Exists(legacyCompact));
        Assert.False(Directory.Exists(currentOld));
        Assert.True(Directory.Exists(currentNew));
    }

    [Fact]
    public void Cleanup_OnlyScansTwoLevels_DoesNotReachDeeperDateFolders()
    {
        string deep = Path.Combine(_root, "aaa", "bbb", "2026-01-01");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "a.jpg"), new byte[] { 1 });

        int removed = SaveImageRetention.Cleanup(_root, "yyyy-MM-dd", 7, Now, null);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(deep));
    }

    [Theory]
    [InlineData("2026-11-15", true)]
    [InlineData("20261115", true)]
    [InlineData("2026_11_15", true)]
    [InlineData("2026年11月15日", true)]
    [InlineData("啊啊啊", false)]
    [InlineData("SN0001", false)]
    [InlineData("2026-13-01", false)]
    [InlineData("00000000", false)]
    [InlineData("", false)]
    public void TryParseDateFolder_RecognizesCommonNamesOnly(string name, bool expected)
    {
        DateTime date;
        Assert.Equal(expected, SaveImageRetention.TryParseDateFolder(name, "yyyy-MM-dd", out date));
    }

    [Fact]
    public void Cleanup_LogsFailureAndKeepsFolder_WhenDeleteBlocked()
    {
        string old = MakeDateDir("2020-01-01");
        List<string> log = new List<string>();

        // 只读文件在 Windows 上仍可删除（Directory.Delete 会清属性），这里用打开句柄占用目录里的文件
        using (FileStream fs = new FileStream(Path.Combine(old, "a.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            int removed = SaveImageRetention.Cleanup(_root, "yyyy-MM-dd", 1, Now, log.Add);
            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(old));
            Assert.Contains(log, l => l.Contains("删除失败"));
        }
    }
}
