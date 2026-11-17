using System;
using System.Collections.Generic;
using WindowsFormsApplication1;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// ch:铭泰定/转子定制存图规则回归（Tests/UnitTests.csproj 直链 Core/SaveImageSettings.cs）。
///   钉死四件事：
///   ① 目录 = 根目录\相机目录\日期\触发字符串，相机目录可留空；两台相机填同名目录即合并；
///   ② 日期格式非法要回退而不是抛异常；
///   ③ 存图名称 + 槽位：同一相机拍 3 张必须得到 3 个不同文件名（否则只剩 1 张），
///      槽位循环到 Slots 后回到 1（现场要求"拍多了自动覆盖"）；
///   ④ ini 配置读写往返不丢项，非法值夹到合法区间。
/// </summary>
public class SaveImageSettingsTests
{
    private static Dictionary<string, string> ReadMap(params string[] kv)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < kv.Length; i += 2)
            map[kv[i]] = kv[i + 1];
        return map;
    }

    [Fact]
    public void Load_WithEmptyStore_KeepsSafeDefaults()
    {
        SaveImageSettings s = SaveImageSettingsStore.Load((k, d) => d);

        Assert.False(s.Enabled);
        Assert.Equal(30, s.RetentionDays);
        Assert.Equal(SaveImageSettings.CameraCount, s.Cameras.Length);
        for (int i = 0; i < s.Cameras.Length; i++)
        {
            Assert.False(s.Cameras[i].Enabled);
            Assert.Equal(1, s.Cameras[i].Slots);
            Assert.Equal("", s.Cameras[i].Folder);
            Assert.Equal("", s.Cameras[i].Name);
        }
    }

    [Fact]
    public void Load_RoundTripsEverythingSaved()
    {
        SaveImageSettings src = new SaveImageSettings();
        src.Enabled = true;
        src.Root = @"D:\图片";
        src.DateFormat = "yyyyMMdd";
        src.Jpeg = true;
        src.JpegQuality = 77;
        src.RetentionDays = 7;
        src.Cameras[0].Enabled = true;
        src.Cameras[0].Folder = "啊啊啊";
        src.Cameras[0].Name = "顶部";
        src.Cameras[0].Slots = 3;
        src.Cameras[7].Enabled = true;
        src.Cameras[7].Folder = "啊啊啊";
        src.Cameras[7].Name = "底部";
        src.Cameras[7].Slots = 1;

        Dictionary<string, string> store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaveImageSettingsStore.Save(src, (k, v) => store[k] = v);
        SaveImageSettings dst = SaveImageSettingsStore.Load((k, d) => store.TryGetValue(k, out string v) ? v : d);

        Assert.True(dst.Enabled);
        Assert.Equal(@"D:\图片", dst.Root);
        Assert.Equal("yyyyMMdd", dst.DateFormat);
        Assert.True(dst.Jpeg);
        Assert.Equal(77, dst.JpegQuality);
        Assert.Equal(7, dst.RetentionDays);
        Assert.True(dst.Cameras[0].Enabled);
        Assert.Equal("啊啊啊", dst.Cameras[0].Folder);
        Assert.Equal("顶部", dst.Cameras[0].Name);
        Assert.Equal(3, dst.Cameras[0].Slots);
        Assert.True(dst.Cameras[7].Enabled);
        Assert.Equal("啊啊啊", dst.Cameras[7].Folder);
        Assert.Equal("底部", dst.Cameras[7].Name);
    }

    [Fact]
    public void Load_ClampsIllegalQualityAndSlots()
    {
        Dictionary<string, string> store = ReadMap(
            "quality", "999",
            "cam1_enabled", "true",
            "cam1_slots", "0",
            "cam2_slots", "999",
            "cam3_slots", "abc");
        SaveImageSettings s = SaveImageSettingsStore.Load(
            (k, d) => store.TryGetValue(k, out string v) ? v : d);

        Assert.Equal(SaveImageSettings.MaxJpegQuality, s.JpegQuality);
        Assert.Equal(1, s.Cameras[0].Slots);
        Assert.Equal(SaveImageSettings.MaxSlots, s.Cameras[1].Slots);
        Assert.Equal(1, s.Cameras[2].Slots);
        Assert.True(s.Cameras[0].Enabled);
    }

    [Fact]
    public void Load_ClampsIllegalRetentionDays()
    {
        Dictionary<string, string> store = ReadMap(
            "keepdays", "-3");
        SaveImageSettings neg = SaveImageSettingsStore.Load(
            (k, d) => store.TryGetValue(k, out string v) ? v : d);
        Assert.Equal(0, neg.RetentionDays);

        store["keepdays"] = "99999";
        Assert.Equal(SaveImageSettings.MaxRetentionDays, SaveImageSettingsStore.Load(
            (k, d) => store.TryGetValue(k, out string v) ? v : d).RetentionDays);

        store["keepdays"] = "abc";
        Assert.Equal(30, SaveImageSettingsStore.Load(
            (k, d) => store.TryGetValue(k, out string v) ? v : d).RetentionDays);
    }

    [Fact]
    public void BuildFolderPath_UsesRootCameraDirDateTriggerOrder()
    {
        DateTime t = new DateTime(2026, 11, 15, 9, 30, 0);
        string p = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "yyyy-MM-dd", "SN0001", t, "啊啊啊");

        Assert.Equal(System.IO.Path.Combine(@"E:\fuNok", "啊啊啊", "2026-11-15", "SN0001"), p);
    }

    [Fact]
    public void BuildFolderPath_EmptyCameraDir_SkipsThatLevel()
    {
        DateTime t = new DateTime(2026, 11, 15, 9, 30, 0);
        string p = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "yyyy-MM-dd", "SN0001", t, "  ");

        Assert.Equal(System.IO.Path.Combine(@"E:\fuNok", "2026-11-15", "SN0001"), p);
    }

    [Fact]
    public void BuildFolderPath_SharedCameraDir_MergesTwoCameras()
    {
        // 相机1(顶) 与 相机4(底) 配同一个目录名，同型号产品最终落进同一个文件夹
        DateTime t = new DateTime(2026, 11, 15, 9, 30, 0);
        string cam1 = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "yyyy-MM-dd", "SN0001", t, "啊啊啊");
        string cam4 = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "yyyy-MM-dd", "SN0001", t, "啊啊啊");

        Assert.Equal(cam1, cam4);
    }

    [Fact]
    public void BuildFolderPath_EmptyRoot_ReturnsEmpty()
    {
        Assert.Equal("", SaveImageSettingsStore.BuildFolderPath("", "yyyy-MM-dd", "SN", DateTime.Now));
        Assert.Equal("", SaveImageSettingsStore.BuildFolderPath(null, "yyyy-MM-dd", "SN", DateTime.Now));
    }

    [Fact]
    public void BuildFolderPath_InvalidDateAndIllegalChars_FallBackInsteadOfThrowing()
    {
        DateTime t = new DateTime(2026, 11, 15);
        string badFormat = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "yyyy/MM/dd", "A:1", t, "B/C");
        // "Q" 不是合法的标准/自定义格式说明符 → DateTime.ToString 抛 FormatException，必须回退 yyyy-MM-dd
        string broken = SaveImageSettingsStore.BuildFolderPath(@"E:\fuNok", "Q", "SN", t);

        Assert.Equal(System.IO.Path.Combine(@"E:\fuNok", "B_C", "2026_11_15", "A_1"), badFormat);
        Assert.Equal(System.IO.Path.Combine(@"E:\fuNok", "2026-11-15", "SN"), broken);
    }

    [Fact]
    public void RenderFileName_SingleSlot_NoSequenceSuffix()
    {
        CameraSaveSetting cam = new CameraSaveSetting { Name = "顶部", Slots = 1 };
        string name = SaveImageSettingsStore.RenderFileName(cam, 3, "SN1", new DateTime(2026, 11, 15), 1);

        Assert.Equal("顶部", name);
    }

    [Fact]
    public void RenderFileName_MultiSlot_WithoutSlotToken_AppendsSequence()
    {
        CameraSaveSetting cam = new CameraSaveSetting { Name = "内壁", Slots = 3 };

        Assert.Equal("内壁_1", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", DateTime.Now, 1));
        Assert.Equal("内壁_2", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", DateTime.Now, 2));
        Assert.Equal("内壁_3", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", DateTime.Now, 3));
    }

    [Fact]
    public void RenderFileName_CustomName_UsesTokens()
    {
        CameraSaveSetting cam = new CameraSaveSetting
        {
            Name = "{文件夹}-{相机}-{序号}",
            Slots = 3
        };
        string name = SaveImageSettingsStore.RenderFileName(cam, 2, "SN9", DateTime.Now, 3);

        Assert.Equal("SN9-2-3", name);
    }

    [Fact]
    public void RenderFileName_EmptyName_UsesCameraFallback()
    {
        CameraSaveSetting cam = new CameraSaveSetting { Name = "", Slots = 1 };
        string name = SaveImageSettingsStore.RenderFileName(cam, 5, "SN", DateTime.Now, 1);

        Assert.Equal("相机5", name);
    }

    [Fact]
    public void RenderFileName_IllegalCharsAreReplaced()
    {
        CameraSaveSetting cam = new CameraSaveSetting { Name = "A/B:*?", Slots = 1 };
        string name = SaveImageSettingsStore.RenderFileName(cam, 1, "SN", DateTime.Now, 1);

        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("*", name);
        Assert.DoesNotContain("?", name);
    }

    [Theory]
    [InlineData("IMG_{SLOT}")]
    [InlineData("IMG_{slot}")]
    [InlineData("IMG_{Num}")]
    public void RenderFileName_SlotToken_IsCaseInsensitive(string template)
    {
        // ch:原实现用区分大小写的 Replace，{SLOT}/{NUM} 不替换也不补 _序号，
        //   3 个槽位会写同一个文件名，每个产品最后只剩 1 张图
        CameraSaveSetting cam = new CameraSaveSetting { Name = template, Slots = 3 };
        DateTime now = new DateTime(2026, 11, 15, 10, 0, 0);

        Assert.Equal("IMG_1", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 1));
        Assert.Equal("IMG_2", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 2));
        Assert.Equal("IMG_3", SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 3));
    }

    [Fact]
    public void RenderFileName_LongNameWithSlots_KeepsSlotSuffix()
    {
        // ch:名称超长时先截断再补 _序号，否则后缀被截掉，3 个槽位又同名互相覆盖
        CameraSaveSetting cam = new CameraSaveSetting { Name = new string('A', 200), Slots = 3 };
        DateTime now = new DateTime(2026, 11, 15, 10, 0, 0);

        string n1 = SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 1);
        string n2 = SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 2);
        string n3 = SaveImageSettingsStore.RenderFileName(cam, 1, "SN", now, 3);

        Assert.NotEqual(n1, n2);
        Assert.NotEqual(n2, n3);
        Assert.EndsWith("_1", n1);
        Assert.EndsWith("_2", n2);
        Assert.EndsWith("_3", n3);
        Assert.True(n1.Length <= SaveImageSettings.MaxSegmentLength);
    }

    [Fact]
    public void FindOutputConflict_SanitizedFolderCollision_IsDetected()
    {
        // "A/B" 与 "A_B" 存图时都会被清洗成 A_B，是同一个目录
        SaveImageSettings s = new SaveImageSettings();
        s.Cameras[0].Enabled = true;
        s.Cameras[0].Folder = "A/B";
        s.Cameras[0].Name = "X";
        s.Cameras[1].Enabled = true;
        s.Cameras[1].Folder = "A_B";
        s.Cameras[1].Name = "X";

        Assert.NotNull(SaveImageSettingsStore.FindOutputConflict(s, new DateTime(2026, 11, 15, 10, 0, 0)));
    }

    [Fact]
    public void FindOutputConflict_SlotSuffixCollision_IsDetected()
    {
        // 相机1 X 三槽位 → X_1/X_2/X_3；相机2 名字直接叫 X_2，会覆盖相机1的第2张
        SaveImageSettings s = new SaveImageSettings();
        s.Cameras[0].Enabled = true;
        s.Cameras[0].Folder = "F";
        s.Cameras[0].Name = "X";
        s.Cameras[0].Slots = 3;
        s.Cameras[1].Enabled = true;
        s.Cameras[1].Folder = "F";
        s.Cameras[1].Name = "X_2";
        s.Cameras[1].Slots = 1;

        Assert.NotNull(SaveImageSettingsStore.FindOutputConflict(s, new DateTime(2026, 11, 15, 10, 0, 0)));
    }

    [Fact]
    public void FindOutputConflict_CameraToken_NoConflict()
    {
        // {相机} 渲染成各自相机号，两台相机同模板也不会撞名
        SaveImageSettings s = new SaveImageSettings();
        s.Cameras[0].Enabled = true;
        s.Cameras[0].Folder = "F";
        s.Cameras[0].Name = "{相机}";
        s.Cameras[1].Enabled = true;
        s.Cameras[1].Folder = "F";
        s.Cameras[1].Name = "{相机}";

        Assert.Null(SaveImageSettingsStore.FindOutputConflict(s, new DateTime(2026, 11, 15, 10, 0, 0)));
    }

    [Fact]
    public void FindOutputConflict_DifferentFolders_NoConflict()
    {
        SaveImageSettings s = new SaveImageSettings();
        s.Cameras[0].Enabled = true;
        s.Cameras[0].Folder = "F1";
        s.Cameras[0].Name = "X";
        s.Cameras[1].Enabled = true;
        s.Cameras[1].Folder = "F2";
        s.Cameras[1].Name = "X";

        Assert.Null(SaveImageSettingsStore.FindOutputConflict(s, new DateTime(2026, 11, 15, 10, 0, 0)));
    }

    [Fact]
    public void FindOutputConflict_DisabledCamera_IsIgnored()
    {
        SaveImageSettings s = new SaveImageSettings();
        s.Cameras[0].Enabled = true;
        s.Cameras[0].Folder = "F";
        s.Cameras[0].Name = "X";
        s.Cameras[1].Enabled = false;
        s.Cameras[1].Folder = "F";
        s.Cameras[1].Name = "X";

        Assert.Null(SaveImageSettingsStore.FindOutputConflict(s, new DateTime(2026, 11, 15, 10, 0, 0)));
    }

    [Fact]
    public void SlotCounter_CyclesPerFolderPerCamera()
    {
        SaveSlotCounter c = new SaveSlotCounter();
        DateTime now = new DateTime(2026, 11, 15, 10, 0, 0);

        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
        Assert.Equal(2, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
        Assert.Equal(3, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
        // 第 4 次回到 1：现场"拍多了自动覆盖第 1 张"
        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
    }

    [Fact]
    public void SlotCounter_SeparatesCamerasAndProducts()
    {
        SaveSlotCounter c = new SaveSlotCounter();
        DateTime now = new DateTime(2026, 11, 15, 10, 0, 0);

        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 2, 3, now));
        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN2", 1, 3, now));

        Assert.Equal(2, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
        Assert.Equal(2, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 2, 3, now));
    }

    [Fact]
    public void SlotCounter_ResetStartsFromOne()
    {
        SaveSlotCounter c = new SaveSlotCounter();
        DateTime now = DateTime.Now;
        c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now);
        c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now);

        c.Reset();

        Assert.Equal(1, c.GetNextSlot(@"E:\a\2026-11-15\SN1", 1, 3, now));
    }
}
