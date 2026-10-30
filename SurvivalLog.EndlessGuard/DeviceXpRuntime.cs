using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using GameCore.HotUpdate.Battle.Logic;
using UnityEngine;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备经验运行时（Task 3；spec docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md §3.4/§3.6）：
/// 四锚点 Harmony 补丁（TakeDamage/FireDevice/ApplyZombieBite/HitZombieByWreck）+ 入账 Award + 升级生效 ApplyLevel。
/// 挂载走 EndlessGuardPlugin.Load 的逐类自动装配（每个 [HarmonyPatch] 嵌套类独立异常隔离），本类不自建 Harmony 实例。
///
/// ===== Step-1 反汇编/interop 实证（mdump 实测 interop HotUpdate.dll 2026-10-23_26157732 + gamekb dump.cs，Task 3 复核）=====
/// ① 锚点签名（全部单重载，interop 方法全公开，Harmony 可直接挂）：
///   - FurnitureDurabilityComponent.TakeDamage : Void (Int32 damage, Boolean isRepair=False, String deathCauseKey, Boolean bypassShield=False)
///     —— 参数名逐字来自 dump.cs；damage 经 ref 入参写回实现减免（HarmonyX il2cpp 装配支持值类型 ref 写回）。
///   - TowerCombatManager.FireDevice : Void (Furniture furniture, TowerDeviceComponent device,
///     Il2CppSystem.Collections.Generic.IReadOnlyList`1&lt;Int64&gt; aliveIds)（原私有；目标 HP 快照口径=AgentManager.GetAgent(id)→Zombie.CurrentHP）。
///   - TowerCombatManager.ApplyZombieBite : Void (Zombie zombie)（公开；受咬设备经 zombie.AssignedTargetId → GetAgent → Furniture 解析）。
///   - TowerCombatManager.HitZombieByWreck : Void (Furniture furniture, Zombie zombie, Int32 damage, Boolean isFirstHit)（原私有）。
/// ② 组件→属主：BaseComponent.ownerInstanceId(long 公开 get) → BaseSingleton&lt;BattleLogicWorld&gt;.Instance._AgentManager
///   （interop 公开 getter，FallRestorer._TagManager 同款已验证模式）→ AgentManager.GetAgent(long) → TryCast&lt;Furniture&gt;()，
///   Furniture.AgentConfigId(int)=家具配置 id（BaseAgent 继承）。组件按 Type 索引直达：agentComponentDictionary[
///   Il2CppType.From(typeof(T))]（CabinetCatalog.ReadTagIds 已验证模式，无泛型/枚举坑）。
/// ②b 在场枚举（Task 4 实证）：AgentManager : BaseEntity 非 BaseSingleton（dump.cs 374312 行），属主仍是
///   BattleLogicWorld._AgentManager（同上公开 getter）→ GetAllAgent():Il2CppList&lt;BaseAgent&gt;（interop 公开无参，
///   mdump 实测）→ 过滤 BaseAgent.IsDead → TryCast&lt;Furniture&gt; → AgentConfigId ∈ IsTrackedDevice。
/// ③ 数值：Zombie.CurrentHP/MaxHP(int 公开)、BaseEntity.InstanceId(long 公开)、TowerDeviceComponent.Attack/
///   AttackIntervalSeconds/AttackRangeSqr get+set 公开、FurnitureDurabilityComponent.CurrentDurability/MaxDurability 公开、
///   AddMaxDurability/AddCurrentDurability(Void(Int32))。
/// ④ 存档标识：interop GameSaveManager 无字面 SaveFileName 成员；同型成员=GetLastPlayFileName():String（公开，
///   GameSaveManager : BaseSingleton&lt;GameSaveManager&gt;）→ SlotKey；不可达/空 → "default"（裁决兜底，Task 4 复核）。
///
/// 边界与让步（已知、可接受，Task 4/9 复核）：
/// - 基线快照按裁决"首见即拍"：首次 ApplyLevel 前拍 Attack/Interval/RangeSqr/MaxDurability 为基线。跨会话
///   （存档→读档）升级过的设备若游戏按配置重置了面板值，基线=重置后的原厂值，重放 ApplyLevel 幂等收敛；
///   MaxDurability 若已含上会话成长则基线偏高（账本只存 {xp,lv} 无基线，spec §6 认可此简化）。
/// - cellKey=TransformComponent.Position 四舍五入 "x,z"；不可达退化 ownerId 字符串（裁决：Task 4 复核）。
/// - 材质 emissive 脉冲：Battle.Logic 层拿不到 Renderer 直连，按裁决只记录脉冲起点列表 + RenderPulse()
///   排水（T8 实装：转投 ArrowTowerFx 逐帧消费，降级表现=Head 缩放脉冲）。
/// - XP 倍率常量字段（XpMultiplier/SandbagXpRate/KillBonusRatio/XpThresholdScale）Task 9 接 cfg。
/// </summary>
internal static class DeviceXpRuntime
{
    // ===== XP 倍率/系数（裁决定名；Task 9 改由 cfg 驱动，先常量占位）=====
    public static float XpMultiplier = 1f;      // 开火经验倍率
    public static float SandbagXpRate = 2f;     // 沙包(36001)承伤经验倍率（spec §3.1 作者决议）
    public static float KillBonusRatio = 0.5f;  // 击杀追加比例（spec §3.1）
    public static float XpThresholdScale = 1f;  // 阈值整体缩放（1=默认 1200/3600/8400/16800）

    /// <summary>账本脏标记：Award 写入即置位；Task 4 巡检 ticker 消费（flush 后清零）。本任务不落盘。</summary>
    internal static bool _dirty;

    /// <summary>当前存档槽标识（设备键第一维）。RefreshSlotKey 刷新；默认 "default"。</summary>
    public static string SlotKey = "default";

    /// <summary>开火事件（家具实例id、muzzle 世界坐标=家具 TransformComponent 位置、目标实例id）。
    /// 托管侧纯 C# 静态事件（il2cpp event interop 有坑，mod 内部自产自消，Task 8 消费）。</summary>
    public static event Action<long, Vector3, long> DeviceFired;

    // ===== 配置按家具 id 派发（spec §3.3/§4.1：武器 36002/36003/36004/99005 攻防成长，沙包 36001 只长血）=====
    private static bool IsWeaponClass(int cfgId) => cfgId == 36002 || cfgId == 36003 || cfgId == 36004 || cfgId == 99005;
    private static float IncomingFactorFor(int cfgId) =>
        cfgId == 36001 ? 0.88f : cfgId == 99005 ? 1.0f : 0.9f;   // 箭塔 1.0=无减伤成长（裁决）
    private static float HpStepRatioFor(int cfgId) =>
        cfgId == 36001 ? 0.25f : cfgId == 99005 ? 0.15f : 0.30f;
    private const int SandbagCfgId = 36001;
    private const int ArrowTowerCfgId = 99005;
    private const int SpikeCfgId = 36002;   // 尖刺障碍：咬击反伤记账目标（spec §3.4）

    // ===== 账本（懒初始化；路径 spec §6：BepInEx/plugins/EndlessGuard_data/device_levels.json）=====
    private static readonly object InitGate = new();
    private static DeviceXpStore _store;
    private static bool _storeReady;

    private static DeviceXpStore Store
    {
        get { EnsureStore(); return _store; }
    }

    private static void EnsureStore()
    {
        if (_storeReady) return;
        lock (InitGate)
        {
            if (_storeReady) return;
            try
            {
                var path = System.IO.Path.Combine(Paths.BepInExRootPath, "plugins", "EndlessGuard_data", "device_levels.json");
                _store = new DeviceXpStore(path, line => EndlessGuardPlugin.Trace(line));
                _store.Load();
            }
            catch (Exception e)
            {
                // 病态兜底（BepInExRoot 异常等）：换临时目录，保入账不炸（数据可弃，游戏不能炸）
                _store = new DeviceXpStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EndlessGuard_device_levels_fallback.json"));
                EndlessGuardPlugin.Trace("[XP] 账本初始化失败，临时目录兜底：" + e.Message);
            }
            _storeReady = true;
        }
    }

    /// <summary>阈值表（XpThresholdScale 缩放后；Task 9 把 cfg 接进字段即可生效）。lv 封顶=长度+1。</summary>
    private static int[] Thresholds() =>
        Array.ConvertAll(DeviceXpMath.ThresholdsDefault,
            t => (int)Math.Round(t * XpThresholdScale, MidpointRounding.AwayFromZero));

    /// <summary>上一步巡检时见到的存档槽标识（slot 切换检测基线；初值=冷启动 SlotKey，首步不误触发清场）。</summary>
    private static string _stepSlot = SlotKey;

    /// <summary>校准熔断器（评审 I1）：按家具实例 id 一台一份，与基线快照同生命周期（槽切换同清；
    /// 设备重建/换 id 天然新态）。连续 3 步 recal 未收敛 → 第 4 次起压制该校准写（封死官方钳制
    /// MaxDurability 时的每 2s 回血循环），halt 行每设备只打一次。</summary>
    private static readonly Dictionary<long, RecalFuse> Fuses = new();

    private static RecalFuse FuseOf(long ownerId)
    {
        if (!Fuses.TryGetValue(ownerId, out var fu) || fu == null) { fu = new RecalFuse(); Fuses[ownerId] = fu; }
        return fu;
    }

    /// <summary>
    /// Task 4 巡检入口（GuardTicker 每 2s 调一次，spec §3.6 读档重放+幂等校准）：
    /// ① RefreshSlotKey 后比对上步值——不同=发生读档/换档 → 清基线快照与校准熔断器（同生命周期）。
    ///    快照按家具实例 id 键控：换档后 id 集合不同，陈旧条目本是无害垃圾；但同档重进时 id 可能复用，
    ///    旧基线可能已是"含成长"的值（首次 ApplyLevel 前拍的快照在上一会话早已落袋），清空更安全且只是
    ///    托管 Dictionary.Clear，开销可忽略。
    /// ② _dirty → Store.Save() 后清旗（裁决的存档时机设计：入账只置脏，巡检统一落盘，无逐事件磁盘写）；
    ///    Save 抛异常（磁盘病态）则旗不清，下个 2s 自动重试。
    /// ③ 重放对账：读档后官方按配置重快照面板值（修理路径同理会回写原厂值），逐台把 live 组件值与
    ///    "账本等级+基线"算出的目标值比对，有漂移才 ApplyLevel 重放并 trace 一行（spec §3.4 逐字格式，
    ///    每修正台一行）。幂等：无漂移零写零 trace；重放复用 ApplyLevel（与升级同一写路径，不另起灶）。
    ///    连续 3 步未收敛的设备由 RecalFuse 熔断压制（评审 I1，防钳制回血循环）。
    /// 全程 try/catch（单台异常不拖累其余台，轮询容器不炸）。
    /// </summary>
    public static void OnStep()
    {
        try
        {
            if (!GateOn()) return;

            // ① slot 上下文失效检测（读档/换档）
            RefreshSlotKey();
            if (!string.Equals(SlotKey, _stepSlot, StringComparison.Ordinal))
            {
                _stepSlot = SlotKey;
                Bases.Clear();
                Fuses.Clear();   // 熔断器与基线同生命周期：换档=新会话，压制态一并复位（评审 I1）
            }

            // ② 脏账本落盘冲刷（成功才清旗，失败下步重试）
            if (_dirty)
            {
                Store.Save();
                _dirty = false;
            }

            // ③ 在场设备逐台对账重放（Lv1 无成长可放，账本无条目同）
            foreach (var f in DevicesInPlay())
            {
                try
                {
                    var cfgId = f.AgentConfigId;
                    if (cfgId == ArrowTower.Id) ArrowTowerFx.NoteTower(f.InstanceId);   // T8：箭塔 FX 注册表喂养（复用本轮 2s 枚举，免 FX 逐帧 GetAllAgent）
                    var cellKey = CellKeyOf(f);
                    if (!Store.TryGet(SlotKey, cfgId, cellKey, out var e) || e == null || e.Lv <= 1) continue;
                    var lv = e.Lv;
                    var ownerId = f.InstanceId;
                    var tower = GetComponent<TowerDeviceComponent>(f);
                    var dura = GetComponent<FurnitureDurabilityComponent>(f);
                    var b = GetOrCaptureBase(ownerId, tower, dura);   // 首见即拍（读档重置后的原厂值恰是合法基线，与 Task 3 让步一致）
                    if (b == null) continue;
                    var drifted =
                        (IsWeaponClass(cfgId) && tower != null && (
                            DeviceXpMath.AttackDrifts(b.Attack, lv, tower.Attack) ||
                            DeviceXpMath.IntervalDrifts(b.Interval, lv, tower.AttackIntervalSeconds) ||
                            (cfgId == ArrowTowerCfgId && DeviceXpMath.RangeDrifts(b.RangeSqr, lv, tower.AttackRangeSqr)))) ||
                        (dura != null && b.MaxHp > 0 &&
                            DeviceXpMath.MaxHpDrifts(b.MaxHp, DeviceXpMath.StepAbs(b.MaxHp, HpStepRatioFor(cfgId)), lv, dura.MaxDurability));
                    var fuse = FuseOf(ownerId);
                    if (!drifted) { fuse.OnConverged(); continue; }   // 幂等：无漂移零写零 trace（连续计数归零）
                    if (!fuse.OnTrigger())                            // 熔断中（评审 I1）：本会话压制该校准写——
                    {                                                 // 防官方钳制 MaxDurability 时每 2s ApplyLevel→AddCurrent 无限回血
                        if (!fuse.HaltTraced)
                        {
                            fuse.HaltTraced = true;
                            EndlessGuardPlugin.Trace("[XP] recal-halt cfg=" + cfgId + " owner=" + ownerId + " 连续3次未收敛，暂停校准（疑似官方钳制MaxDurability，待实机取证）");
                        }
                        continue;
                    }
                    ApplyLevel(f, lv);   // 唯一写路径：与升级生效共用
                    EndlessGuardPlugin.Trace("[XP] recal cfg=" + cfgId + " lv=" + lv);   // spec §3.4 逐字格式
                }
                catch (Exception e) { EndlessGuardPlugin.Trace("[XP] recal probe: " + e.Message); }
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] step: " + e.Message); }
    }

    /// <summary>在场受追踪家具枚举：BaseSingleton&lt;BattleLogicWorld&gt;._AgentManager.GetAllAgent()
    /// （interop 实证：AgentManager : BaseEntity 非单例，属主=BattleLogicWorld._AgentManager 公开 getter，
    /// 与 ResolveFurniture 同源；GetAllAgent():Il2CppList&lt;BaseAgent&gt; 公开无参）。过滤 IsDead、TryCast&lt;Furniture&gt;、
    /// IsTrackedDevice。世界未建/列表不可达=空枚举（主菜单静默零开销）。</summary>
    private static IEnumerable<Furniture> DevicesInPlay()
    {
        if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) yield break;
        Il2CppSystem.Collections.Generic.List<BaseAgent> all = null;
        try { all = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent(); }
        catch { yield break; }
        if (all == null) yield break;
        for (int i = 0; i < all.Count; i++)
        {
            Furniture f = null;
            try
            {
                var a = all[i];
                if (a == null || a.IsDead) continue;
                f = a.TryCast<Furniture>();
                if (f == null || !DeviceXpMath.IsTrackedDevice(f.AgentConfigId)) continue;
            }
            catch { continue; }   // 单个 il2cpp 代理病态不拖垮整轮巡检
            yield return f;
        }
    }

    /// <summary>存档槽标识刷新：GameSaveManager.GetLastPlayFileName（interop 无字面 SaveFileName，取同型公开成员；
    /// 单例不可达/异常/空值一律保留现值，冷启动自然落在 "default"）。</summary>
    public static void RefreshSlotKey()
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.IsInstanceCreated) return;
            var name = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.Instance?.GetLastPlayFileName();
            if (!string.IsNullOrWhiteSpace(name)) SlotKey = name;
        }
        catch { /* 不可达 → 保持现值（裁决兜底链：SaveFileName 型成员拿不到=default）*/ }
    }

    /// <summary>总开关门（FallRestorer 同款语义：Enabled 未初始化=放行，显式 false=全部还原官方）。</summary>
    private static bool GateOn() => EndlessGuardPlugin.Enabled == null || EndlessGuardPlugin.Enabled.Value;

    /// <summary>只读等级查询（Task 5 悬停展示消费；纯读侧，零写路径）：tracked 设备 → 账本条目
    /// lv/xp + 下一累计阈值（-1=满级）。未追踪/无账目/不可达=false（悬停保持官方原名）。复用
    /// Store/CellKeyOf/RefreshSlotKey/Thresholds 同一套口径（与 trace 行 xp=total/next 一致）；
    /// 异常只 trace 不上抛（低频 UI 路径宁缺毋炸）。</summary>
    internal static bool TryGetLevelInfo(Furniture f, out int lv, out int xp, out int next)
    {
        lv = 1; xp = 0; next = -1;
        try
        {
            if (f == null || !DeviceXpMath.IsTrackedDevice(f.AgentConfigId)) return false;
            RefreshSlotKey();
            if (!Store.TryGet(SlotKey, f.AgentConfigId, CellKeyOf(f), out var e) || e == null) return false;
            lv = Math.Max(1, e.Lv);
            xp = e.Xp;
            next = DeviceXpMath.NextThreshold(lv, Thresholds());
            return true;
        }
        catch (Exception ex) { EndlessGuardPlugin.Trace("[XP] levelInfo: " + ex.Message); return false; }
    }

    // =====================================================================
    // 入账与升级生效
    // =====================================================================

    /// <summary>经验入账（唯一写入口）：账本+1 → 跨级则 ApplyLevel → 按 spec §3.4 格式 trace。
    /// 全程 try/catch，异常只 trace 不上抛（战斗热路径零炸点）。</summary>
    internal static void Award(Furniture furn, int amount)
    {
        try
        {
            if (furn == null || amount <= 0) return;
            if (!GateOn()) return;
            var cfgId = furn.AgentConfigId;
            if (!DeviceXpMath.IsTrackedDevice(cfgId)) return;
            var cellKey = CellKeyOf(furn);
            RefreshSlotKey();
            var entry = Store.GetOrNew(SlotKey, cfgId, cellKey);
            if (entry.Lv < 1) entry.Lv = 1;   // GetOrNew 新建条目默认 0 → 初始 Lv1（不触发 lvup 行）
            var newXp = entry.Xp + amount;
            var th = Thresholds();
            var lv = DeviceXpMath.LevelFromXp(newXp, th.Length + 1, th);
            if (lv > entry.Lv)   // 只升不降（阈值缩档不回退已生效属性）
            {
                entry.Lv = lv;
                ApplyLevel(furn, lv);
                EndlessGuardPlugin.Trace("[XP] lvup cfg=" + cfgId + " lv=" + lv);   // spec §3.4 逐字格式
            }
            Store.Set(SlotKey, cfgId, cellKey, newXp, entry.Lv);   // 裁决：入账走 Set 整体覆写 + 置脏，不逐事件 Save
            _dirty = true;
            var next = DeviceXpMath.NextThreshold(lv, th);
            EndlessGuardPlugin.Trace("[XP] cfg=" + cfgId + " cell=" + cellKey + " xp=" + newXp + "/" + (next < 0 ? "MAX" : next.ToString()));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] award: " + e.Message); }
    }

    // 基线快照（裁决：首见即拍，先于任何改写；ApplyLevel 是唯一改写者，拍基线发生在其改写之前）
    private sealed class DeviceBase { public int Attack; public float Interval; public float RangeSqr; public int MaxHp; }
    private static readonly Dictionary<long, DeviceBase> Bases = new();   // key=家具实例id

    /// <summary>升级生效（brief Produces 契约：ApplyLevel(Furniture, int)）：武器类直写 Attack/AttackIntervalSeconds，
    /// AttackRangeSqr 仅箭塔(99005)（=TargetRange²）；耐久 AddMaxDurability(delta)+AddCurrentDurability(delta)（delta=target-现值，
    /// 首次升级 delta==stepAbs，与裁决公式重合；正增量同步回满=满血语义，负增量（读档已含成长）不动）。</summary>
    internal static void ApplyLevel(Furniture f, int lv)
    {
        try
        {
            if (f == null || lv < 1) return;
            var cfgId = f.AgentConfigId;
            var ownerId = f.InstanceId;
            var tower = GetComponent<TowerDeviceComponent>(f);
            var dura = GetComponent<FurnitureDurabilityComponent>(f);
            var b = GetOrCaptureBase(ownerId, tower, dura);
            if (b == null) return;

            if (IsWeaponClass(cfgId) && tower != null)
            {
                tower.Attack = DeviceXpMath.TargetAttack(b.Attack, lv);
                tower.AttackIntervalSeconds = DeviceXpMath.TargetInterval(b.Interval, lv);
                if (cfgId == ArrowTowerCfgId)
                {
                    tower.AttackRangeSqr = DeviceXpMath.TargetRangeSqr(b.RangeSqr, lv);   // brief：AttackRangeSqr(=TargetRange²)，与巡检判定同一换算
                }
            }
            if (dura != null && b.MaxHp > 0)
            {
                var stepAbs = DeviceXpMath.StepAbs(b.MaxHp, HpStepRatioFor(cfgId));
                var targetMax = DeviceXpMath.TargetMaxHp(b.MaxHp, lv, stepAbs);
                var delta = targetMax - dura.MaxDurability;
                if (delta > 0) { dura.AddMaxDurability(delta); dura.AddCurrentDurability(delta); }   // 同步加并回满
            }
            RecordPulse(ownerId);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] applyLevel: " + e.Message); }
    }

    private static DeviceBase GetOrCaptureBase(long ownerId, TowerDeviceComponent tower, FurnitureDurabilityComponent dura)
    {
        if (Bases.TryGetValue(ownerId, out var b) && b != null) return b;
        if (tower == null && dura == null) return null;   // 两件组件都够不着=没的可改没的可拍
        b = new DeviceBase
        {
            Attack = tower != null ? tower.Attack : 0,
            Interval = tower != null ? tower.AttackIntervalSeconds : 0f,
            RangeSqr = tower != null ? tower.AttackRangeSqr : 0f,
            MaxHp = dura != null ? dura.MaxDurability : 0,
        };
        Bases[ownerId] = b;
        return b;
    }

    // 升级发光脉冲：逻辑层无 Renderer 直连（裁决 simplification）——只记起点列表，RenderPulse() 留给 T8 的逐帧 ticker。
    private static readonly List<long> PulseOwners = new();
    private static readonly List<DateTime> PulseStarts = new();

    private static void RecordPulse(long ownerId)
    {
        try
        {
            var now = DateTime.UtcNow;
            for (int i = PulseStarts.Count - 1; i >= 0; i--)   // 只留 1s 内的（避免无 ticker 消费时无限堆）
                if ((now - PulseStarts[i]).TotalSeconds > 1.0) { PulseStarts.RemoveAt(i); PulseOwners.RemoveAt(i); }
            PulseOwners.Add(ownerId);
            PulseStarts.Add(now);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] RecordPulse: " + e.Message); }   // 全文约定：不静默吞（评审 F6）
    }

    /// <summary>逐帧脉冲排水（T8 实装；ArrowTowerFx.Update 每帧调用）：队列转投 FX 层
    /// （elapsed 补偿排水延迟，≥PulsePeriod 丢弃），逐帧衰减与降级表现（Head 缩放脉冲，材质 emissive
    /// 不可靠的裁决见 ArrowTowerFx 类头⑥）全在 FX 侧；本层仍无 Renderer 直连，只清账。</summary>
    public static void RenderPulse()
    {
        try
        {
            if (PulseOwners.Count == 0) return;
            var now = DateTime.UtcNow;
            for (int i = 0; i < PulseOwners.Count; i++)
            {
                var elapsed = (float)(now - PulseStarts[i]).TotalSeconds;
                if (elapsed < ArrowTowerFxMath.PulsePeriod) ArrowTowerFx.NotePulse(PulseOwners[i], elapsed);
            }
            PulseOwners.Clear();
            PulseStarts.Clear();
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] renderPulse: " + e.Message); }
    }

    // =====================================================================
    // il2cpp 解析助手（每一路都 null/异常兜底——interop 摩擦是预期）
    // =====================================================================

    /// <summary>组件按 Type 索引直达（CabinetCatalog.ReadTagIds 已验证模式；无枚举 KeyValuePair 坑）。</summary>
    private static T GetComponent<T>(Furniture f) where T : GameCore.HotUpdate.BaseEntity
    {
        try
        {
            var dict = f != null ? f.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppInterop.Runtime.Il2CppType.From(typeof(T)), out var raw)
                ? raw?.TryCast<T>() : null;
        }
        catch { return null; }
    }

    /// <summary>实例 id → 家具（BattleLogicWorld._AgentManager.GetAgent；interop 公开 getter，_TagManager 同款模式）。</summary>
    private static Furniture ResolveFurniture(long instanceId)
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return null;
            var am = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager;
            return am?.GetAgent(instanceId)?.TryCast<Furniture>();
        }
        catch { return null; }
    }

    private static Zombie ResolveZombie(long instanceId)
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return null;
            var am = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager;
            return am?.GetAgent(instanceId)?.TryCast<Zombie>();
        }
        catch { return null; }
    }

    /// <summary>家具 cellKey：TransformComponent.Position 四舍五入 "x,z"；不可达退化 ownerId（裁决注明，T4 复核）。</summary>
    private static string CellKeyOf(Furniture f)
    {
        try
        {
            var tc = GetComponent<TransformComponent>(f);
            if (tc != null)
            {
                var p = tc.Position;
                return Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z);
            }
        }
        catch { }
        return f != null ? f.InstanceId.ToString() : "?";
    }

    /// <summary>muzzle 世界坐标=家具 TransformComponent 位置（拿不到=Vector3.zero，T8 侧自行兜底）。</summary>
    private static Vector3 MuzzleOf(Furniture f)
    {
        try { return GetComponent<TransformComponent>(f)?.Position ?? Vector3.zero; }
        catch { return Vector3.zero; }
    }

    /// <summary>目标当前 HP（快照口径：GetAgent→Zombie.CurrentHP）；解析失败哨兵 -1。</summary>
    private static int TargetHp(long id)
    {
        try { return ResolveZombie(id)?.CurrentHP ?? -1; }
        catch { return -1; }
    }

    /// <summary>设备当前等级（无条目=1；异常=1——前缀减伤宁可少扣不可炸）。</summary>
    private static int LevelOf(int cfgId, string cellKey)
    {
        try { return Store.TryGet(SlotKey, cfgId, cellKey, out var e) && e != null && e.Lv > 1 ? e.Lv : 1; }
        catch { return 1; }
    }

    // =====================================================================
    // 锚点 1：FurnitureDurabilityComponent.TakeDamage（承伤减免 + 沙包/承伤经验）
    // =====================================================================

    /// <summary>
    /// 前缀：属主家具在名单且 ShouldScaleIncomingDamage(isRepair) 时按 IncomingDamageFactor 缩 damage（ref 写回，截断取整）。
    /// 非名单（门/窗等）一字节不动。同时拍 hpBefore（CurrentDurability）供后缀算实际掉血。
    /// 后缀：hpLost>0 才入账（修理/未破防=负数或零，跳过），AwardForTaken(hpLost, 沙包×2 其余×1)。
    /// _ctx 前缀置后缀清（游戏主线程单线调用，同帧嵌套 TakeDamage 不存在；异常路径由前缀置 null 兜底）。
    /// </summary>
    [HarmonyPatch]
    internal static class TakeDamagePatch
    {
        private sealed class Ctx { public Furniture Furn; public int CfgId; public int HpBefore; }
        private static Ctx _ctx;

        // 签名钉死（防重载/漂移静默 miss；mdump 实测 Void(Int32,Boolean,String,Boolean)）
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(FurnitureDurabilityComponent), "TakeDamage",
                new[] { typeof(int), typeof(bool), typeof(string), typeof(bool) });

        private static void Prefix(FurnitureDurabilityComponent __instance, ref int damage, bool isRepair)
        {
            _ctx = null;
            try
            {
                if (__instance == null || !GateOn()) return;
                var furn = ResolveFurniture(__instance.ownerInstanceId);
                if (furn == null) return;
                var cfgId = furn.AgentConfigId;
                if (!DeviceXpMath.IsTrackedDevice(cfgId)) return;   // 门/窗=字节透传
                _ctx = new Ctx { Furn = furn, CfgId = cfgId, HpBefore = __instance.CurrentDurability };
                if (!DeviceXpMath.ShouldScaleIncomingDamage(isRepair)) return;   // 修理不缩放（spec §3.4 前缀门）
                var lv = LevelOf(cfgId, CellKeyOf(furn));
                var factor = DeviceXpMath.IncomingDamageFactor(lv, IncomingFactorFor(cfgId));
                damage = (int)(damage * factor);   // 裁决逐字：截断取整
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] TakeDamage prefix: " + e.Message); }
        }

        private static void Postfix(FurnitureDurabilityComponent __instance)
        {
            var ctx = _ctx;
            _ctx = null;
            if (ctx == null || __instance == null) return;
            try
            {
                var lost = ctx.HpBefore - __instance.CurrentDurability;
                if (lost <= 0) return;   // 未破防/修理回血不倒扣经验
                Award(ctx.Furn, DeviceXpMath.AwardForTaken(lost, ctx.CfgId == SandbagCfgId ? SandbagXpRate : 1f));
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] TakeDamage postfix: " + e.Message); }
        }
    }

    // =====================================================================
    // 锚点 2：TowerCombatManager.FireDevice（开火经验 + 击杀追加 + DeviceFired 事件）
    // =====================================================================

    /// <summary>前缀快照目标 HP（口径=GetAgent→Zombie.CurrentHP，解析失败哨兵 -1）；后缀按 AwardForFire(atk,count,mult)
    /// 入账、快照时 HP>0 且现在 HP≤0 者逐个 AwardForKill 追加，并 raise DeviceFired（muzzle=家具位置，目标取首个）。</summary>
    [HarmonyPatch]
    internal static class FireDevicePatch
    {
        private static List<long> _ids;
        private static List<int> _hpBefore;
        private static int _count;

        // 一次性告警（ScavengePlans._lastRegErr 同款：同文只打首条防刷屏，换文重发）
        private static string _lastListWarn;
        private static void WarnOnce(string line)
        {
            if (line == _lastListWarn) return;
            _lastListWarn = line;
            EndlessGuardPlugin.Trace(line);
        }

        // 签名钉死（mdump 实测：第三参=Il2CppSystem.Collections.Generic.IReadOnlyList`1<Int64>）
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TowerCombatManager), "FireDevice",
                new[]
                {
                    typeof(Furniture),
                    typeof(TowerDeviceComponent),
                    typeof(Il2CppSystem.Collections.Generic.IReadOnlyList<long>)
                });

        private static void Prefix(Furniture furniture, TowerDeviceComponent device, Il2CppSystem.Collections.Generic.IReadOnlyList<long> aliveIds)
        {
            _ids = null; _hpBefore = null; _count = 0;
            try
            {
                if (aliveIds == null) return;
                // 首选 List<long> 强转（Count+索引齐备=全功能：HP 快照+击杀判定；TickDevices 实传 List）
                var list = aliveIds.TryCast<Il2CppSystem.Collections.Generic.List<long>>();
                if (list != null)
                {
                    _count = list.Count;
                    _ids = new List<long>(_count);
                    _hpBefore = new List<int>(_count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var id = list[i];
                        _ids.Add(id);
                        _hpBefore.Add(TargetHp(id));
                    }
                    return;
                }
                // 兜底 IReadOnlyCollection<long>（interop 代理的 Count 挂该接口）：保住 AwardForFire 按 count 计，
                // 放弃本帧 HP 快照/击杀判定（_hpBefore=null，后缀自然跳过击杀环）
                var coll = aliveIds.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<long>>();
                if (coll != null)
                {
                    _count = coll.Count;
                    WarnOnce("[XP] FireDevice 列表类型异常（降级只计数）: " + aliveIds.GetType().Name);
                    return;
                }
                // 两个强转都失败：本帧不记账，但必须留痕（消除武器经验静默归零路径，评审 F1）
                WarnOnce("[XP] FireDevice 列表类型不可解析（本帧不记账）: " + aliveIds.GetType().Name);
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] FireDevice prefix: " + e.Message); }
        }

        private static void Postfix(Furniture furniture, TowerDeviceComponent device)
        {
            try
            {
                if (furniture == null) return;
                var ids = _ids; var hpBefore = _hpBefore; var count = _count;
                _ids = null; _hpBefore = null; _count = 0;
                if (!GateOn()) return;

                long targetId = ids != null && ids.Count > 0 ? ids[0] : 0;
                try { DeviceFired?.Invoke(furniture.InstanceId, MuzzleOf(furniture), targetId); }
                catch (Exception e) { EndlessGuardPlugin.Trace("[XP] DeviceFired 消费者异常: " + e.Message); }

                var atk = device != null ? device.Attack : 0;
                int xp = DeviceXpMath.AwardForFire(atk, count, XpMultiplier);
                if (ids != null && hpBefore != null)
                {
                    int kills = 0;
                    for (int i = 0; i < ids.Count && i < hpBefore.Count; i++)
                    {
                        // -1=解析失败哨兵，不视作已死（防幻影击杀经验，评审 F2）：两次读数均有效且确死才计
                        var hpAfter = TargetHp(ids[i]);
                        if (hpBefore[i] > 0 && hpAfter >= 0 && hpAfter <= 0) kills++;
                    }
                    if (kills > 0) xp += DeviceXpMath.AwardForKill(atk, KillBonusRatio) * kills;
                }
                if (xp > 0) Award(furniture, xp);
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] FireDevice postfix: " + e.Message); }
        }
    }

    // =====================================================================
    // 锚点 3：TowerCombatManager.ApplyZombieBite（咬击反伤经验：尖刺 36002）
    // =====================================================================

    /// <summary>后缀：受咬设备=zombie.AssignedTargetId → GetAgent → Furniture；cfg==36002 时按其 TowerDeviceComponent.
    /// CounterDamage 入账（读不到退化 spec 默认 15）。非尖刺/解析失败一律静默跳过（战斗热路径零噪音）。</summary>
    [HarmonyPatch]
    internal static class ApplyZombieBitePatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TowerCombatManager), "ApplyZombieBite", new[] { typeof(Zombie) });

        private static void Postfix(Zombie zombie)
        {
            try
            {
                if (zombie == null || !GateOn()) return;
                var furn = ResolveFurniture(zombie.AssignedTargetId);
                if (furn == null || furn.AgentConfigId != SpikeCfgId) return;
                if (!DeviceXpMath.IsTrackedDevice(SpikeCfgId)) return;
                var tower = GetComponent<TowerDeviceComponent>(furn);
                var counter = tower != null && tower.CounterDamage > 0 ? tower.CounterDamage : 15;
                Award(furn, counter);
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] ApplyZombieBite postfix: " + e.Message); }
        }
    }

    // =====================================================================
    // 锚点 4：TowerCombatManager.HitZombieByWreck（残骸撞尸经验）
    // =====================================================================

    /// <summary>后缀：残骸家具直接是首参；按其 TowerDeviceComponent.WreckDamage 入账（组件读不到退化 damage 入参）。</summary>
    [HarmonyPatch]
    internal static class HitZombieByWreckPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TowerCombatManager), "HitZombieByWreck",
                new[] { typeof(Furniture), typeof(Zombie), typeof(int), typeof(bool) });

        private static void Postfix(Furniture furniture, Zombie zombie, int damage, bool isFirstHit)
        {
            try
            {
                if (furniture == null || !GateOn()) return;
                var tower = GetComponent<TowerDeviceComponent>(furniture);
                var wreck = tower != null && tower.WreckDamage > 0 ? tower.WreckDamage : damage;
                if (wreck <= 0) return;
                Award(furniture, wreck);
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] HitZombieByWreck postfix: " + e.Message); }
        }
    }
}
