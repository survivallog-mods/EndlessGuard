using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace SurvivalLog.EndlessGuard.Tests;

/// <summary>
/// DeviceXpStore 经验账本契约测试（Task 2）。
/// 覆盖：临时目录 round-trip（Set→Save→新 Store Load 值一致）/ JSON 形状逐字（slots→slot→"configId#cellKey"→xp/lv）
/// / 损坏+缺失文件→空账本不抛且注入 logger 恰一行 / 原子写（Save 后无 .tmp 残留、首次保存自动建目录）
/// / GetOrNew 活引用 vs TryGet 只读语义。测试沙箱 = 每用例实例独立临时目录，Dispose 兜底清理。
/// 测试工程按单文件链接 DeviceXpStore.cs（同程序集 internal 直读），不引用主工程。
/// </summary>
public class DeviceXpStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DeviceXpStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "EG_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "device_levels.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理尽力而为，不影响断言 */ }
    }

    // ---- Round-trip（Set→Save→新 Store Load→值一致） ----

    [Fact]
    public void RoundTrip_SetSaveNewLoad_PreservesValues()
    {
        var s1 = new DeviceXpStore(_path);
        s1.Set("s1", 36001, "12,8", 1500, 2);      // 沙包 2 级
        s1.Set("s1", 99005, "3,4", 20000, 5);      // 箭塔满级
        s1.Set("s2", 36003, "0,1", 10, 1);         // 第二存档槽
        s1.Save();

        var s2 = new DeviceXpStore(_path);
        s2.Load();

        Assert.True(s2.TryGet("s1", 36001, "12,8", out var sandbag));
        Assert.Equal(1500, sandbag.Xp);
        Assert.Equal(2, sandbag.Lv);
        Assert.True(s2.TryGet("s1", 99005, "3,4", out var tower));
        Assert.Equal(20000, tower.Xp);
        Assert.Equal(5, tower.Lv);
        Assert.True(s2.TryGet("s2", 36003, "0,1", out var grid));
        Assert.Equal(10, grid.Xp);
        Assert.Equal(1, grid.Lv);
    }

    [Fact]
    public void Save_JsonShape_IsVerbatimSlotsConfigIdHashCellKey()
    {
        var s = new DeviceXpStore(_path);
        s.Set("s1", 36001, "12,8", 1500, 2);
        s.Save();

        // 逐字形状：{"slots":{"s1":{"36001#12,8":{"xp":1500,"lv":2}}}}
        var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(doc.RootElement.TryGetProperty("slots", out var slots));
        Assert.True(slots.TryGetProperty("s1", out var devices));
        Assert.True(devices.TryGetProperty("36001#12,8", out var entry));
        Assert.Equal(1500, entry.GetProperty("xp").GetInt32());
        Assert.Equal(2, entry.GetProperty("lv").GetInt32());
    }

    // ---- 损坏 / 缺失容错（空账本、不抛、logger 恰一行） ----

    [Fact]
    public void Load_CorruptJson_EmptyLedgerNoThrow_OneLogLine()
    {
        File.WriteAllText(_path, "{{{不是 json 的乱码");
        var logs = new List<string>();
        var s = new DeviceXpStore(_path, logs.Add);

        s.Load();   // 不抛即通过；抛出则测试失败

        Assert.False(s.TryGet("s1", 36001, "12,8", out _));
        Assert.Single(logs);
        // 损坏重置后账本仍是可用空账本（非 null），可继续记账
        s.Set("s1", 36001, "1,1", 5, 1);
        Assert.True(s.TryGet("s1", 36001, "1,1", out var e));
        Assert.Equal(5, e.Xp);
    }

    [Fact]
    public void Load_MissingFile_EmptyLedgerNoThrow_OneLogLine()
    {
        var logs = new List<string>();
        var s = new DeviceXpStore(_path, logs.Add);

        s.Load();   // 首启无文件：空账本不抛

        Assert.False(s.TryGet("s1", 36001, "12,8", out _));
        Assert.Single(logs);
    }

    [Fact]
    public void Load_NullLogger_CorruptStillNoThrow()
    {
        File.WriteAllText(_path, "corrupt!!!");
        var s = new DeviceXpStore(_path);   // logger 默认 null：不注入也必须容错

        s.Load();

        Assert.False(s.TryGet("s1", 36001, "12,8", out _));
    }

    // ---- 原子写（tmp+Move） ----

    [Fact]
    public void Save_NoTmpResidue_AndTargetFileExists()
    {
        var s = new DeviceXpStore(_path);
        s.Set("s1", 36002, "5,6", 100, 1);
        s.Save();

        Assert.False(File.Exists(_path + ".tmp"));   // 临时文件已被 Move 走，无残留
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Save_CreatesMissingTargetDirectory()
    {
        var nested = Path.Combine(_dir, "EndlessGuard_data", "sub");
        var path = Path.Combine(nested, "device_levels.json");
        var s = new DeviceXpStore(path);

        s.Set("s1", 36001, "1,1", 5, 1);
        s.Save();   // 首次保存自动建目录（Directory.CreateDirectory 幂等）

        Assert.True(File.Exists(path));
    }

    // ---- ClearSlot（fixwave2 Defense.ResetLevels 卸载还原路径） ----

    [Fact]
    public void ClearSlot_RemovesOnlyTargetSlot_AndReturnsCount()
    {
        var s = new DeviceXpStore(_path);
        s.Set("s1", 36001, "12,8", 1500, 2);
        s.Set("s1", 99005, "3,4", 20000, 5);
        s.Set("s2", 36003, "0,1", 10, 1);

        var n = s.ClearSlot("s1");
        Assert.Equal(2, n);                                   // 条数=该槽条目数
        Assert.False(s.TryGet("s1", 36001, "12,8", out _));
        Assert.False(s.TryGet("s1", 99005, "3,4", out _));
        Assert.True(s.TryGet("s2", 36003, "0,1", out var other));   // 其他槽原样保留
        Assert.Equal(10, other.Xp);
    }

    [Fact]
    public void ClearSlot_UnknownSlot_ReturnsZero_AndSecondClearIsZero()
    {
        var s = new DeviceXpStore(_path);
        Assert.Equal(0, s.ClearSlot("nope"));
        s.Set("s1", 36001, "1,1", 5, 1);
        Assert.Equal(1, s.ClearSlot("s1"));
        Assert.Equal(0, s.ClearSlot("s1"));   // 幂等：已空再清=0（还原模式 2s 轮询下零重复写盘依据）
    }

    [Fact]
    public void ClearSlot_ThenSaveLoad_WipePersists()
    {
        var s1 = new DeviceXpStore(_path);
        s1.Set("s1", 36001, "12,8", 1500, 2);
        s1.Set("s2", 36003, "0,1", 10, 1);
        s1.ClearSlot("s1");
        s1.Save();

        var s2 = new DeviceXpStore(_path);
        s2.Load();
        Assert.False(s2.TryGet("s1", 36001, "12,8", out _));
        Assert.True(s2.TryGet("s2", 36003, "0,1", out var kept));
        Assert.Equal(10, kept.Xp);
    }

    // ---- GetOrNew 活引用 vs TryGet 只读语义 ----

    [Fact]
    public void GetOrNew_ReturnsSameLiveEntry_MutationVisibleViaTryGet()
    {
        var s = new DeviceXpStore(_path);

        var e1 = s.GetOrNew("s1", 36001, "12,8");
        var e2 = s.GetOrNew("s1", 36001, "12,8");
        Assert.Same(e1, e2);            // 同一活引用，不重复建条目

        e1.Xp = 1200;
        e1.Lv = 2;
        Assert.True(s.TryGet("s1", 36001, "12,8", out var got));
        Assert.Equal(1200, got.Xp);     // 直接改字段在账本内可见（活引用语义）
        Assert.Equal(2, got.Lv);
    }

    [Fact]
    public void Set_OverwritesPrevious_InMemoryImmediately()
    {
        var s = new DeviceXpStore(_path);
        s.Set("s1", 36004, "7,7", 100, 1);
        s.Set("s1", 36004, "7,7", 300, 2);

        Assert.True(s.TryGet("s1", 36004, "7,7", out var e));
        Assert.Equal(300, e.Xp);
        Assert.Equal(2, e.Lv);
    }

    [Fact]
    public void TryGet_UnknownSlotOrConfigOrCell_ReturnsFalseAndNull()
    {
        var s = new DeviceXpStore(_path);
        s.Set("s1", 36001, "12,8", 1, 1);

        Assert.False(s.TryGet("sX", 36001, "12,8", out var miss1));
        Assert.Null(miss1);
        Assert.False(s.TryGet("s1", 36002, "12,8", out var miss2));   // 换 configId 不命中
        Assert.Null(miss2);
        Assert.False(s.TryGet("s1", 36001, "0,0", out var miss3));    // 换 cellKey 不命中
        Assert.Null(miss3);
    }
}
