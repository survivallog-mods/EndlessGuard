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

    // ---- 跨槽账目融合（fixwave6D：自动保存换档名分裂账本的对账回收）----

    [Fact]
    public void MergeMaxInto_MissingKeyCopiedFromOtherSlots()
    {
        // 目标槽缺键：其他槽同键条目拷入（分裂出去的真账回收）
        var s = new DeviceXpStore(_path);
        s.Set("old", 99005, "6,-27", 161307, 5);
        s.Set("old", 36001, "8,-27", 1448, 2);

        var merged = s.MergeMaxInto("new");

        Assert.Equal(2, merged);
        Assert.True(s.TryGet("new", 99005, "6,-27", out var t));
        Assert.Equal(161307, t.Xp); Assert.Equal(5, t.Lv);
        Assert.True(s.TryGet("new", 36001, "8,-27", out var b));
        Assert.Equal(1448, b.Xp); Assert.Equal(2, b.Lv);
    }

    [Fact]
    public void MergeMaxInto_SmallerTargetOverwrittenByLargerSource()
    {
        // 目标槽已有半截新账（lv1 xp1001），旧槽真账更大（lv5）→ 整条覆盖（xp/lv 成对，不拼配）
        var s = new DeviceXpStore(_path);
        s.Set("new", 99005, "6,-27", 1001, 1);
        s.Set("old", 99005, "6,-27", 161307, 5);

        var merged = s.MergeMaxInto("new");

        Assert.Equal(1, merged);
        Assert.True(s.TryGet("new", 99005, "6,-27", out var e));
        Assert.Equal(161307, e.Xp); Assert.Equal(5, e.Lv);
    }

    [Fact]
    public void MergeMaxInto_LargerTargetKept_SourceUntouched()
    {
        // 目标槽账更大：不回退；源槽原样保留（不删——误融合可逆）
        var s = new DeviceXpStore(_path);
        s.Set("new", 36004, "4,-27", 21036, 5);
        s.Set("old", 36004, "4,-27", 500, 1);

        var merged = s.MergeMaxInto("new");

        Assert.Equal(0, merged);
        Assert.True(s.TryGet("new", 36004, "4,-27", out var e));
        Assert.Equal(21036, e.Xp); Assert.Equal(5, e.Lv);
        Assert.True(s.TryGet("old", 36004, "4,-27", out var src));
        Assert.Equal(500, src.Xp);   // 源槽不动
    }

    [Fact]
    public void MergeMaxInto_MultipleSourcesTakesLargest()
    {
        // 多个分裂槽同键：取 xp 最大者
        var s = new DeviceXpStore(_path);
        s.Set("a", 99005, "5,-26", 300, 1);
        s.Set("b", 99005, "5,-26", 92957, 5);
        s.Set("c", 99005, "5,-26", 800, 1);

        s.MergeMaxInto("target");

        Assert.True(s.TryGet("target", 99005, "5,-26", out var e));
        Assert.Equal(92957, e.Xp); Assert.Equal(5, e.Lv);
    }

    [Fact]
    public void MergeMaxInto_DifferentPositionKeysDoNotCollide()
    {
        // 真·不同档的同类型设备（不同位置）：本档键不被他档键覆盖；他档键拷入本档后
        // 是"无人位置"（本档无设备在 -9,5）——读取永不命中，无害；源槽原样保留
        var s = new DeviceXpStore(_path);
        s.Set("otherSave", 36004, "-9,5", 29215, 5);   // 另一基地坐标
        s.Set("target", 36004, "4,-27", 10, 1);

        var merged = s.MergeMaxInto("target");

        Assert.Equal(1, merged);   // 仅 -9,5 作为缺失键拷入（无害冗余）
        Assert.True(s.TryGet("target", 36004, "4,-27", out var e));
        Assert.Equal(10, e.Xp); Assert.Equal(1, e.Lv);   // 本档原账不动
        Assert.True(s.TryGet("target", 36004, "-9,5", out var copy));
        Assert.Equal(29215, copy.Xp);                     // 拷贝件（无人键，读不到）
        Assert.True(s.TryGet("otherSave", 36004, "-9,5", out var o));
        Assert.Equal(29215, o.Xp);   // 源槽不动
    }
}
