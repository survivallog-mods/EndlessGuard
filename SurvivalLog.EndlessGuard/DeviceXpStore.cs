using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurvivalLog.EndlessGuard;

/// <summary>单个设备的经验账目条目：Xp=累计经验，Lv=当前等级（public 字段；JsonPropertyName 钉死 JSON 小写形状 xp/lv，Task 2 契约逐字）。</summary>
internal sealed class DeviceXpEntry
{
    [JsonPropertyName("xp")] public int Xp;
    [JsonPropertyName("lv")] public int Lv;
}

/// <summary>
/// 防御设备经验账本（Task 2；spec docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md §6 持久化）：
/// 按存档槽 slot → 设备键 "configId#cellKey"（cellKey 形如 "12,8" 的格坐标）记账。
/// JSON 形状逐字：{"slots":{"<slot>":{"<configId>#<cellKey>":{"xp":n,"lv":n}}}}。
/// 纯托管 BCL 代码：零 BepInEx/Il2Cpp/UnityEngine 引用——类自己不调 EndlessGuardPlugin.Trace（il2cpp 世界类型，测试摸不到），
/// 构造函数注入 Action&lt;string&gt; logger（默认 null），宿主侧传入自己的 Trace 包装。
/// 容错：文件缺失/损坏 → Load 得空账本不抛，经注入 logger 记一行（首启缺文件属正常，Trace 级无噪音负担）。
/// 原子写：Save 先写 &lt;path&gt;.tmp 再 File.Move(overwrite:true) 原子替换，崩溃不留半个账本；首次保存自动建目标目录。
/// 后续 Harmony 补丁/注入消费本类（GetOrNew 返回活引用，调用方可直改字段；Set 整体覆写）。
/// </summary>
internal sealed class DeviceXpStore
{
    private readonly string _jsonPath;
    private readonly Action<string> _log;
    private Dictionary<string, Dictionary<string, DeviceXpEntry>> _slots = new();

    // IncludeFields=true：DeviceXpEntry 契约是 public 字段（非属性），STJ 默认只认属性，需显式放开；缩进便于人工排查存档
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true, WriteIndented = true };

    internal DeviceXpStore(string jsonPath, Action<string> log = null)
    {
        _jsonPath = jsonPath;
        _log = log;
    }

    /// <summary>从磁盘加载账本；文件缺失/损坏 → 空账本（不抛），缺失/损坏各经注入 logger 记一行。</summary>
    internal void Load()
    {
        _slots = new Dictionary<string, Dictionary<string, DeviceXpEntry>>();
        try
        {
            if (!File.Exists(_jsonPath))
            {
                _log?.Invoke($"[EndlessGuard] 设备经验账本不存在，从空账本开始：{_jsonPath}");
                return;
            }
            var data = JsonSerializer.Deserialize<PersistData>(File.ReadAllText(_jsonPath), JsonOpts);
            if (data?.Slots != null)
                _slots = data.Slots;
        }
        catch (Exception ex)
        {
            _slots = new Dictionary<string, Dictionary<string, DeviceXpEntry>>();
            _log?.Invoke($"[EndlessGuard] 设备经验账本损坏已重置为空：{_jsonPath}（{ex.Message}）");
        }
    }

    /// <summary>原子保存：先写 &lt;path&gt;.tmp 再 File.Move 原子替换；首次保存自动建目标目录（幂等）。</summary>
    internal void Save()
    {
        var dir = Path.GetDirectoryName(_jsonPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _jsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new PersistData { Slots = _slots }, JsonOpts));
        File.Move(tmp, _jsonPath, true);
    }

    /// <summary>取条目，没有则新建并挂账（返回活引用：调用方直改 Xp/Lv 字段即生效，Save 时落盘）。</summary>
    internal DeviceXpEntry GetOrNew(string slot, int configId, string cellKey)
    {
        var key = DeviceKey(configId, cellKey);
        if (!_slots.TryGetValue(slot, out var devices))
        {
            devices = new Dictionary<string, DeviceXpEntry>();
            _slots[slot] = devices;
        }
        if (!devices.TryGetValue(key, out var entry))
        {
            entry = new DeviceXpEntry();
            devices[key] = entry;
        }
        return entry;
    }

    /// <summary>只读查询：命中返回 true+条目，未命中返回 false 且条目为 null（不新建）。</summary>
    internal bool TryGet(string slot, int configId, string cellKey, out DeviceXpEntry entry)
    {
        entry = null;
        return _slots.TryGetValue(slot, out var devices) && devices.TryGetValue(DeviceKey(configId, cellKey), out entry);
    }

    /// <summary>整体覆写条目（xp=累计经验，lv=当前等级），内存即时生效，Save 时落盘。</summary>
    internal void Set(string slot, int configId, string cellKey, int xp, int lv)
    {
        var entry = GetOrNew(slot, configId, cellKey);
        entry.Xp = xp;
        entry.Lv = lv;
    }

    /// <summary>清空一个存档槽的全部条目（Defense.ResetLevels 卸载还原路径用），返回清除条数；槽不存在=0。内存即时生效，Save 时落盘。</summary>
    internal int ClearSlot(string slot)
    {
        if (!_slots.TryGetValue(slot, out var devices)) return 0;
        var n = devices.Count;
        _slots.Remove(slot);
        return n;
    }

    /// <summary>设备键："configId#cellKey"，如 "36001#12,8"（JSON 形状契约逐字）。</summary>
    private static string DeviceKey(int configId, string cellKey) => $"{configId}#{cellKey}";

    /// <summary>落盘 DTO：顶层只含 slots 一键（JsonPropertyName 小写钉死 JSON 形状契约，多余键一律不出现）。</summary>
    private sealed class PersistData
    {
        [JsonPropertyName("slots")] public Dictionary<string, Dictionary<string, DeviceXpEntry>> Slots;
    }
}
