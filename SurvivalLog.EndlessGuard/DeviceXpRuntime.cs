using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using GameCore.HotUpdate.Battle.Logic;
using UnityEngine;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备经验运行时（Task 3；spec docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md §3.4/§3.6）：
/// 四锚点 Harmony 补丁（TakeDamage/FireDevice/ApplyZombieBite/HitZombieByWreck，全部只读观测）+ 入账 Award + 升级生效 ApplyLevel。
/// 挂载走 EndlessGuardPlugin.Load 的逐类自动装配（每个 [HarmonyPatch] 嵌套类独立异常隔离），本类不自建 Harmony 实例。
///
/// ===== fixwave2 架构重写（2026-10-31 作者裁定"两个动作不应该来回覆写"，playtest 实证旧路线三病症：
/// 设备批量掉血[官方 Initialize 钳制 320→200 表现为伤害]/升级属性来回拍/222 条 recal+sticky 刷屏）=====
/// **升级=换配置行。官方代码是组件值的唯一写入者；本 mod 只改"官方读哪一行配置"。**
/// - 派生等级行（99112..99155，方案见 DeviceXpMath）由 ArrowTower.InjectDerivedRows 建好；
/// - ApplyLevel 唯一动作 = f.AgentConfigId 换到派生行 + 官方重初始化（TowerDeviceComponent.OnInit(行号) /
///   FurnitureDurabilityComponent.Initialize(满,满)）让数值当场生效——零组件属性直写、零 AddMaxDurability；
/// - 官方随后的任意重快照（读档/修理/重入）天然读的是同一行配置 → 无需粘性回贴、无需对账追赶
///   （旧 F4 双粘性 postfix 与 2s recal 追赶整链删除，RecalFuse 熔断器随之退役）；
/// - OnStep 只留轻量正确性巡检：AgentConfigId ≠ 账本等级对应的行号时（升级发生于设备不在场/读档残留
///   等带外情况）才补一次换行重初始化——只在不一致时写，幂等稳态零写零 trace；
/// - 受击减伤不再由 TakeDamage 前缀缩放（已删），改由派生行 Config_TowerDevice.SelfDamagePerBite 承载
///   （按等级缩放；官方四件套该值全 0 → 维持 0，箭塔 1.0 系数维持 0——缩放公式在行上，见 ArrowTower）；
///   TakeDamage 前缀退化为只读快照 hpBefore（供后缀算实际掉血入账），后缀零写组件。
///
/// ===== Step-1 反汇编/interop 实证（mdump 实测 interop HotUpdate.dll 2026-10-23_26157732 + gamekb dump.cs，fixwave2 复核）=====
/// ① 锚点签名（全部单重载，interop 方法全公开，Harmony 可直接挂）：
///   - FurnitureDurabilityComponent.TakeDamage : Void (Int32 damage, Boolean isRepair=False, String deathCauseKey, Boolean bypassShield=False)
///     —— 参数名逐字来自 dump.cs。
///   - TowerCombatManager.FireDevice : Void (Furniture furniture, TowerDeviceComponent device,
///     Il2CppSystem.Collections.Generic.IReadOnlyList`1&lt;Int64&gt; aliveIds)（原私有；目标 HP 快照口径=AgentManager.GetAgent(id)→Zombie.CurrentHP）。
///   - TowerCombatManager.ApplyZombieBite : Void (Zombie zombie)（公开；受咬设备经 zombie.AssignedTargetId → GetAgent → Furniture 解析）。
///   - TowerCombatManager.HitZombieByWreck : Void (Furniture furniture, Zombie zombie, Int32 damage, Boolean isFirstHit)（原私有）。
/// ② 组件→属主：BaseComponent.ownerInstanceId(long 公开 get) → BaseSingleton&lt;BattleLogicWorld&gt;.Instance._AgentManager
///   → AgentManager.GetAgent(long) → TryCast&lt;Furniture&gt;()。组件按 Type 索引直达：agentComponentDictionary[
///   Il2CppType.From(typeof(T))]（CabinetCatalog.ReadTagIds 已验证模式）。
/// ②b 在场枚举：BattleLogicWorld._AgentManager.GetAllAgent():Il2CppList&lt;BaseAgent&gt;（interop 公开无参，
///   mdump 实测）→ 过滤 IsDead → TryCast&lt;Furniture&gt; → IsTrackedDevice（含派生行）。
/// ③ 换行重初始化（fixwave2 复核 mdump）：BaseAgent.AgentConfigId get+set 公开（save 由游戏自己的存档
///   系统按 AgentConfigId 走，非本 mod 写存档文件）；TowerDeviceComponent.OnInit(Int32) 公开（攻击面板
///   按配置行重快照）；FurnitureDurabilityComponent.Initialize(Int32,Int32) 公开（耐久重快照）；
///   ConfigManager.Get_Config_Furniture(id).FurnitureHPMax 公开（派生行耐久上限读取）。
/// ④ 存档标识：GameSaveManager.GetLastPlayFileName():String（公开）→ SlotKey；不可达/空 → "default"。
///
/// 边界与让步（已知、可接受）：
/// - 升级满血是语义的一部分（Initialize(满,满)）：升级顺带修满=作者拍板的设计（旧直写路线同款"正增量回满"）。
/// - 巡检补写（带外升级残留）用 Initialize(min(cur,上限), 上限) 夹取不白送血；升级路径才满血。
/// - cellKey=TransformComponent.Position 四舍五入 "x,z"；不可达退化 ownerId 字符串。
/// - 材质 emissive 脉冲：Battle.Logic 层拿不到 Renderer 直连，只记录脉冲起点 + RenderPulse() 排水（T8）。
/// - XP 数值旋钮（Task 9 实装）：XpMultiplier/SandbagXpRate/KillBonusRatio/XpThresholdScale + 各类成长
///   步长/系数全部读 cfg Defense.*；改值后由 ArrowTower 的派生行新鲜度哨兵重建行内容，组件面板在下次
///   官方重初始化（读档/重放）时吸收新值。
/// </summary>
internal static class DeviceXpRuntime
{
    // ===== XP 数值旋钮（spec §3.5 全表接 cfg，Task 9）：读侧直取 ConfigEntry.Value——cfg 改值即刻生效，
    // 派生行内容由 ArrowTower 新鲜度哨兵按这些值重建（组件面板下次官方重初始化吸收）；cfg 未绑定（异常
    // 兜底）回退 spec 默认值。属性名与 spec §3.5 键名一一对应，语义注释同 cfg 描述（EndlessGuardPlugin.Load）=====
    public static float XpMultiplier => Cfg(EndlessGuardPlugin.XPMultiplier, 1f);       // 开火经验倍率
    public static float SandbagXpRate => Cfg(EndlessGuardPlugin.SandbagXPRate, 2f);     // 沙包(36001)承伤经验倍率（spec §3.1 作者决议）
    public static float KillBonusRatio => Cfg(EndlessGuardPlugin.KillBonusRatio, 0.5f); // 击杀追加比例（spec §3.1）
    public static float XpThresholdScale => Cfg(EndlessGuardPlugin.XPThresholdScale, 1f); // 阈值整体缩放（1=默认 1200/3600/8400/16800）
    public static float WeaponAtkStep => Cfg(EndlessGuardPlugin.WeaponAtkStep, 0.15f);        // 武器类每级攻击 +比例（复利）
    public static float WeaponIntervalStep => Cfg(EndlessGuardPlugin.WeaponIntervalStep, 0.08f); // 武器类每级间隔 −比例
    public static float WeaponHpStepRatio => Cfg(EndlessGuardPlugin.WeaponHpStepRatio, 0.30f);   // 武器类每级耐久 +比例
    public static float WeaponDefFactor => Cfg(EndlessGuardPlugin.WeaponDefFactor, 0.90f);       // 武器类每级受伤×系数
    public static float SandbagHpStepRatio => Cfg(EndlessGuardPlugin.SandbagHpStepRatio, 0.25f); // 沙包每级耐久 +比例
    public static float SandbagDefFactor => Cfg(EndlessGuardPlugin.SandbagDefFactor, 0.88f);     // 沙包每级受伤×系数
    public static float TowerRangeStep => Cfg(EndlessGuardPlugin.TowerRangeStep, 0.55f);         // 箭塔射程每级 +格数

    /// <summary>等级上限（cfg Defense.MaxLevel 夹在 [1, 阈值表长度+1=5]；1=关闭成长）。只封顶今后的升级
    /// ——已超上限设备配置行保留不回退（只升不降裁决，账本等级在，调回上限成长自动续上）。</summary>
    public static int MaxLevel => Math.Min(
        Math.Max(EndlessGuardPlugin.MaxLevel != null ? EndlessGuardPlugin.MaxLevel.Value : 5, 1),
        DeviceXpMath.ThresholdsDefault.Length + 1);

    private static float Cfg(ConfigEntry<float> e, float dflt) => e != null ? e.Value : dflt;

    /// <summary>账本脏标记：Award 写入即置位；巡检 ticker 消费（flush 后清零）。</summary>
    internal static bool _dirty;

    /// <summary>当前存档槽标识（设备键第一维）。RefreshSlotKey 刷新；默认 "default"。</summary>
    public static string SlotKey = "default";

    /// <summary>开火事件（家具实例id、muzzle 世界坐标=家具 TransformComponent 位置、目标实例id）。
    /// 托管侧纯 C# 静态事件（il2cpp event interop 有坑，mod 内部自产自消，Task 8 消费）。</summary>
    public static event Action<long, Vector3, long> DeviceFired;

    // ===== 类别判定/成长参数（fixwave2 改 internal：ArrowTower 建派生行时消费同一套口径，杜绝两套算式）=====
    // spec §3.3/§4.1：武器 36002/36003/36004/99005 攻防成长，沙包 36001 只长血；箭塔 HP 步长与减伤系数
    // 不入 cfg（§3.5 未列），保持 spec §4.1 裁定值。
    internal static bool IsWeaponClass(int cfgId) => cfgId == 36002 || cfgId == 36003 || cfgId == 36004 || cfgId == 99005;
    internal static float IncomingFactorFor(int cfgId) =>
        cfgId == 36001 ? SandbagDefFactor : cfgId == 99005 ? 1.0f : WeaponDefFactor;   // 箭塔 1.0=无减伤成长（裁决）
    internal static float HpStepRatioFor(int cfgId) =>
        cfgId == 36001 ? SandbagHpStepRatio : cfgId == 99005 ? TowerHpStepRatio : WeaponHpStepRatio;
    private const float TowerHpStepRatio = 0.15f;   // 箭塔耐久步长（spec §4.1 低成长脆皮定位；低血是平衡项，不放 cfg）
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

    /// <summary>阈值表（XpThresholdScale cfg 缩放后）。lv 封顶=MaxLevel（cfg，默认=长度+1）。</summary>
    private static int[] Thresholds() =>
        Array.ConvertAll(DeviceXpMath.ThresholdsDefault,
            t => (int)Math.Round(t * XpThresholdScale, MidpointRounding.AwayFromZero));

    /// <summary>上一步巡检时见到的存档槽标识（slot 切换检测基线；初值=冷启动 SlotKey，首步不误触发清场）。</summary>
    private static string _stepSlot = SlotKey;

    /// <summary>
    /// 巡检入口（GuardTicker 每 2s 调一次；fixwave2 轻量化：粘性回贴/对账追赶/熔断器全链删除）：
    /// ① RefreshSlotKey 后比对上步值——不同=发生读档/换档（官方读档已按保存的 AgentConfigId 重初始化，
    ///    派生行在档=数值原样回来，无需任何追补）。
    /// ② Defense.ResetLevels 还原模式（卸载前安全网，先于 XP 门——XPEnabled=false 也要能还原）：
    ///    全部在场受追踪设备换回基础行+官方重初始化+耐久夹回基础上限，清空本槽账本，然后可安全删 dll。
    /// ③ _dirty → Store.Save() 后清旗；Save 抛异常（磁盘病态）则旗不清，下个 2s 自动重试。
    /// ④ 轻量正确性巡检：账本等级对应的行号 ≠ 现场 AgentConfigId 才补写（升级发生于设备不在场的带外
    ///    情况/旧版本残留）；幂等稳态零写零 trace。
    /// ⑤ 箭塔 FX 注册表喂养（复用本轮 2s 枚举）。
    /// 全程 try/catch（单台异常不拖累其余台）。
    /// </summary>
    public static void OnStep()
    {
        try
        {
            if (!GateOn()) return;
            RefreshSlotKey();
            if (!string.Equals(SlotKey, _stepSlot, StringComparison.Ordinal))
                _stepSlot = SlotKey;   // 读档/换档：旧粘性/基线状态已随 fixwave2 删除，无需清场

            ResetAllLevelsIfArmed();   // 还原模式（cfg Defense.ResetLevels；未开启=一次布尔比较即出）

            if (!XpOn())
            {
                // Defense.XPEnabled=false：XP 侧（脏账落盘/巡检补写）全停（账本冻结不改写）；
                // 但箭塔 FX 注册表喂养照走——FX 属塔本体（门=Defense.ArrowTowerEnabled，T8 裁决），
                // DeviceFired 开火路径同样在喂养，此处只补「从未开过火的塔」的待机动画入口。
                foreach (var f in DevicesInPlay())
                {
                    try { if (DeviceXpMath.BaseCfgOf(f.AgentConfigId) == ArrowTowerCfgId) ArrowTowerFx.NoteTower(f.InstanceId); }
                    catch { /* 单台病态不拖垮整轮 */ }
                }
                return;
            }

            // ② 脏账本落盘冲刷（成功才清旗，失败下步重试）
            if (_dirty)
            {
                Store.Save();
                _dirty = false;
            }

            // ④ 轻量正确性巡检（只在行号不一致时写；稳态=每台一次字典查询+int 比较）
            foreach (var f in DevicesInPlay())
            {
                try
                {
                    var baseCfg = DeviceXpMath.BaseCfgOf(f.AgentConfigId);
                    if (baseCfg == ArrowTowerCfgId) ArrowTowerFx.NoteTower(f.InstanceId);   // T8：FX 注册表喂养
                    var cellKey = CellKeyOf(f);
                    var lv = 1;
                    if (Store.TryGet(SlotKey, baseCfg, cellKey, out var e) && e != null && e.Lv > 1) lv = Math.Min(e.Lv, MaxLevel);
                    var expected = DeviceXpMath.RowIdFor(baseCfg, lv);
                    if (expected < 0 || f.AgentConfigId == expected) continue;   // 幂等：一致零写零 trace
                    f.AgentConfigId = expected;                                  // 补写=换行（官方值权威，无属性直写）
                    ReinitFromRow(f, expected, fullHeal: false);                 // 夹取不白送血（升级路径才满血）
                    EndlessGuardPlugin.Trace("[XP] recal cfg=" + baseCfg + " lv=" + lv + " row=" + expected);
                }
                catch (Exception e) { EndlessGuardPlugin.Trace("[XP] recal probe: " + e.Message); }
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] step: " + e.Message); }
    }

    /// <summary>Defense.ResetLevels 还原模式（fixwave2 卸载安全网）：派生行随 dll 消失，留档设备的
    /// AgentConfigId 会指向不存在的行——此开关把存档一次性还原为纯官方状态：全部在场受追踪设备
    /// AgentConfigId=BaseCfgOf(现场值) + OnInit(基础行) 重快照攻击面板 + 耐久 Initialize(min(cur,基础上限),
    /// 基础上限)（夹取不回血），并清空本槽账本+落盘。开启状态下每轮巡检都跑（幂等：已在基础行且耐久
    /// 合规=零写）；全部干净时只打一次 scan-clean 行。cfg 不自动复位——作者反复进出档确认满意后手动
    /// 关回 false，然后可安全删除 dll（README 说明由控制方后续补，此处只留代码注释）。</summary>
    private static bool _resetCleanTraced;

    private static void ResetAllLevelsIfArmed()
    {
        var armed = EndlessGuardPlugin.ResetLevels != null && EndlessGuardPlugin.ResetLevels.Value;
        if (!armed) { _resetCleanTraced = false; return; }
        var changed = 0;
        foreach (var f in DevicesInPlay())
        {
            try
            {
                var cur = f.AgentConfigId;
                var baseCfg = DeviceXpMath.BaseCfgOf(cur);
                if (cur != baseCfg)
                {
                    f.AgentConfigId = baseCfg;
                    GetComponent<TowerDeviceComponent>(f)?.OnInit(baseCfg);   // 攻击面板回到基础行原厂值
                    changed++;
                }
                var dura = GetComponent<FurnitureDurabilityComponent>(f);
                var baseMax = MaxHpOfRow(baseCfg);
                if (dura != null && baseMax > 0 && (dura.MaxDurability != baseMax || dura.CurrentDurability > baseMax))
                {
                    dura.Initialize(Math.Min(dura.CurrentDurability, baseMax), baseMax);   // 夹取：不白送血
                    changed++;
                }
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] reset probe: " + e.Message); }
        }
        var wiped = 0;
        try { wiped = Store.ClearSlot(SlotKey); }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] reset wipe: " + e.Message); }
        if (wiped > 0)
        {
            try { Store.Save(); }   // 账本清空即落盘（作者关 dll 前的最后状态=空账本）
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] reset save: " + e.Message); }
        }
        if (changed > 0 || wiped > 0)
        {
            _resetCleanTraced = false;
            EndlessGuardPlugin.Trace("[XP] levels reset (safe uninstall) slot=" + SlotKey + " devices=" + changed + " ledger=" + wiped);
        }
        else if (!_resetCleanTraced)
        {
            _resetCleanTraced = true;   // 每会话一次：作者确认还原已完成（已干净，可删 dll）
            EndlessGuardPlugin.Trace("[XP] levels scan clean (safe uninstall ready) slot=" + SlotKey);
        }
    }

    /// <summary>在场受追踪家具枚举：BaseSingleton&lt;BattleLogicWorld&gt;._AgentManager.GetAllAgent()
    /// （interop 实证：AgentManager : BaseEntity 非单例，属主=BattleLogicWorld._AgentManager 公开 getter；
    /// GetAllAgent():Il2CppList&lt;BaseAgent&gt; 公开无参）。过滤 IsDead、TryCast&lt;Furniture&gt;、
    /// IsTrackedDevice（基础行或派生行）。世界未建/列表不可达=空枚举（主菜单静默零开销）。</summary>
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

    /// <summary>XP 门（Task 9，GateOn 之上再挂 Defense.XPEnabled）：false=经验系统整体停用——入账/换行
    /// 巡检/悬停进度行全部停；已换行的设备本局保留（行内容即数值），重启读档后仍按保存的行号原样回来。</summary>
    internal static bool XpOn() => GateOn() && (EndlessGuardPlugin.XPEnabled == null || EndlessGuardPlugin.XPEnabled.Value);

    /// <summary>只读等级查询（悬停进度 trace 消费；纯读侧，零写路径）：tracked 设备 → 账本条目
    /// lv/xp + 下一累计阈值（-1=满级）。未追踪/无账目/不可达=false。账本按基础 id 键控
    /// （BaseCfgOf 归一）；异常只 trace 不上抛（低频 UI 路径宁缺毋炸）。</summary>
    internal static bool TryGetLevelInfo(Furniture f, out int lv, out int xp, out int next)
    {
        lv = 1; xp = 0; next = -1;
        try
        {
            if (f == null) return false;
            var baseCfg = DeviceXpMath.BaseCfgOf(f.AgentConfigId);
            if (!DeviceXpMath.IsTrackedDevice(baseCfg)) return false;
            RefreshSlotKey();
            if (!Store.TryGet(SlotKey, baseCfg, CellKeyOf(f), out var e) || e == null) return false;
            lv = Math.Max(1, e.Lv);
            xp = e.Xp;
            next = lv >= MaxLevel ? -1 : DeviceXpMath.NextThreshold(lv, Thresholds());   // cfg 降上限时提前 MAX
            return true;
        }
        catch (Exception ex) { EndlessGuardPlugin.Trace("[XP] levelInfo: " + ex.Message); return false; }
    }

    // =====================================================================
    // 入账与升级生效
    // =====================================================================

    /// <summary>经验入账（唯一写入口）：账本（按基础 id 键控）+1 → 跨级则 ApplyLevel（换配置行）→ trace。
    /// 全程 try/catch，异常只 trace 不上抛（战斗热路径零炸点）。</summary>
    internal static void Award(Furniture furn, int amount)
    {
        try
        {
            if (furn == null || amount <= 0) return;
            if (!XpOn()) return;
            var baseCfg = DeviceXpMath.BaseCfgOf(furn.AgentConfigId);
            if (!DeviceXpMath.IsTrackedDevice(baseCfg)) return;
            var cellKey = CellKeyOf(furn);
            RefreshSlotKey();
            var entry = Store.GetOrNew(SlotKey, baseCfg, cellKey);
            if (entry.Lv < 1) entry.Lv = 1;   // GetOrNew 新建条目默认 0 → 初始 Lv1（不触发 lvup 行）
            var newXp = entry.Xp + amount;
            var th = Thresholds();
            var lv = DeviceXpMath.LevelFromXp(newXp, MaxLevel, th);   // cfg MaxLevel 封顶（默认 5=阈值表长度+1，同旧行为）
            if (lv > entry.Lv)   // 只升不降（阈值缩档不回退已换的行）
            {
                entry.Lv = lv;
                ApplyLevel(furn, lv);   // lvup trace（cfg=/lv=/row=）由 ApplyLevel 打，一行不重复
            }
            Store.Set(SlotKey, baseCfg, cellKey, newXp, entry.Lv);   // 裁决：入账走 Set 整体覆写 + 置脏，不逐事件 Save
            _dirty = true;
            var next = DeviceXpMath.NextThreshold(lv, th);
            EndlessGuardPlugin.Trace("[XP] cfg=" + baseCfg + " cell=" + cellKey + " xp=" + newXp + "/" + (next < 0 ? "MAX" : next.ToString()));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] award: " + e.Message); }
    }

    /// <summary>
    /// 升级生效（fixwave2 重写，唯一动作=换配置行）：f.AgentConfigId 换到派生行（lv1=基础行本身），
    /// 然后官方重初始化让行数值当场生效——TowerDeviceComponent.OnInit(行号)（攻击面板）+
    /// FurnitureDurabilityComponent.Initialize(满,满)（耐久满血，升级语义含修复）。零组件属性直写。
    /// 官方之后的任意重快照（读档/修理/重入）读的是同一行 → 天然稳定，无需回贴。
    /// 已在该行=零写零 trace（幂等）。行号不可得（非名单/越级）=trace 告警不动。
    /// </summary>
    internal static void ApplyLevel(Furniture f, int lv)
    {
        try
        {
            if (f == null || lv < 1) return;
            var baseCfg = DeviceXpMath.BaseCfgOf(f.AgentConfigId);
            var derived = DeviceXpMath.RowIdFor(baseCfg, lv);
            if (derived < 0)
            {
                EndlessGuardPlugin.Trace("[XP] lvup skip: no row for cfg=" + baseCfg + " lv=" + lv);
                return;
            }
            if (f.AgentConfigId == derived) return;   // 已在该行：零写（幂等）
            f.AgentConfigId = derived;                // 唯一写入：换配置行（游戏自己的存档系统会带上它）
            ReinitFromRow(f, derived, fullHeal: true);
            RecordPulse(f.InstanceId);
            EndlessGuardPlugin.Trace("[XP] lvup cfg=" + baseCfg + " lv=" + lv + " row=" + derived);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] applyLevel: " + e.Message); }
    }

    /// <summary>换行后官方重初始化（fixwave2）：攻击面板 OnInit(行号)（组件不在=跳过，沙包无塔组件）；
    /// 耐久 Initialize(cur/max, 上限)——上限=派生/基础行 FurnitureHPMax（配置读不到退化账面目标值，
    /// 再不行跳过耐久）。fullHeal=true（升级路径）回满；false（巡检补写/还原路径）min(cur,上限) 夹取不白送血。</summary>
    private static void ReinitFromRow(Furniture f, int rowId, bool fullHeal)
    {
        try { GetComponent<TowerDeviceComponent>(f)?.OnInit(rowId); }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] reinit OnInit: " + e.Message); }
        try
        {
            var dura = GetComponent<FurnitureDurabilityComponent>(f);
            if (dura == null) return;
            var max = MaxHpOfRow(rowId);
            if (max <= 0) return;   // 配置不可达：不动耐久（宁缺毋错血）
            dura.Initialize(fullHeal ? max : Math.Min(dura.CurrentDurability, max), max);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] reinit dura: " + e.Message); }
    }

    /// <summary>配置行耐久上限（FurnitureHPMax；行不可达=-1）。ConfigManager 单例直读
    ///（DeviceXpHover 同款模式）。</summary>
    private static int MaxHpOfRow(int rowId)
    {
        try
        {
            var cfg = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ConfigManager>.IsInstanceCreated
                ? GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ConfigManager>.Instance?.Get_Config_Furniture(rowId)
                : null;
            return cfg != null ? cfg.FurnitureHPMax : -1;
        }
        catch { return -1; }
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

    /// <summary>家具 cellKey：TransformComponent.Position 四舍五入 "x,z"；不可达退化 ownerId（裁决注明）。
    /// internal=悬停进度 trace 要打账本键字符串（DeviceXpHover 同装配）。</summary>
    internal static string CellKeyOf(Furniture f)
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

    // =====================================================================
    // 锚点 1：FurnitureDurabilityComponent.TakeDamage（承伤经验；fixwave2 起前缀只读，减伤在派生行配置里）
    // =====================================================================

    /// <summary>
    /// 前缀（只读）：属主家具在名单（基础 id 归一后）即拍 hpBefore（CurrentDurability）。零参数写、
    /// 零组件写——受击减伤已由派生行 Config_TowerDevice.SelfDamagePerBite 承载（官方按配置自取）。
    /// 后缀（只读组件+账本写）：hpLost&gt;0 才入账（修理/未破防=负数或零，跳过），
    /// AwardForTaken(hpLost, 沙包×2 其余×1)。等级提升触发的换行经 Award→ApplyLevel（换行是游戏自己的
    /// 存档属性+官方重初始化，非补丁写组件）。_ctx 前缀置后缀清（游戏主线程单线调用，同帧嵌套
    /// TakeDamage 不存在；异常路径由前缀置 null 兜底）。
    /// </summary>
    [HarmonyPatch]
    internal static class TakeDamagePatch
    {
        private sealed class Ctx { public Furniture Furn; public int BaseCfg; public int HpBefore; }
        private static Ctx _ctx;

        // 签名钉死（防重载/漂移静默 miss；mdump 实测 Void(Int32,Boolean,String,Boolean)）
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(FurnitureDurabilityComponent), "TakeDamage",
                new[] { typeof(int), typeof(bool), typeof(string), typeof(bool) });

        private static void Prefix(FurnitureDurabilityComponent __instance)
        {
            _ctx = null;
            try
            {
                if (__instance == null || !XpOn()) return;   // XP 关=不入账（减伤在配置行，不受此门管）
                var furn = ResolveFurniture(__instance.ownerInstanceId);
                if (furn == null) return;
                var baseCfg = DeviceXpMath.BaseCfgOf(furn.AgentConfigId);
                if (!DeviceXpMath.IsTrackedDevice(baseCfg)) return;   // 门/窗=不关注
                _ctx = new Ctx { Furn = furn, BaseCfg = baseCfg, HpBefore = __instance.CurrentDurability };
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
                Award(ctx.Furn, DeviceXpMath.AwardForTaken(lost, ctx.BaseCfg == SandbagCfgId ? SandbagXpRate : 1f));
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] TakeDamage postfix: " + e.Message); }
        }
    }

    // =====================================================================
    // 锚点 2：TowerCombatManager.FireDevice（开火经验 + 击杀追加 + DeviceFired 事件）
    // =====================================================================

    /// <summary>前缀快照目标 HP（口径=GetAgent→Zombie.CurrentHP，解析失败哨兵 -1）；后缀按 AwardForFire(atk,count,mult)
    /// 入账、快照时 HP&gt;0 且现在 HP≤0 者逐个 AwardForKill 追加，并 raise DeviceFired（muzzle=家具位置，目标取首个）。</summary>
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

                if (!XpOn()) return;   // XP 关=只发事件不记账（DeviceFired 已发，塔 FX 照常——FX 属塔本体，归 Defense.ArrowTowerEnabled 管）

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
    // 锚点 3：TowerCombatManager.ApplyZombieBite（咬击反伤经验：尖刺 36002 含派生行）
    // =====================================================================

    /// <summary>后缀：受咬设备=zombie.AssignedTargetId → GetAgent → Furniture；BaseCfgOf==36002 时按其
    /// TowerDeviceComponent.CounterDamage 入账（组件读不到退化 spec 默认 15）。非尖刺/解析失败一律静默
    /// 跳过（战斗热路径零噪音）。</summary>
    [HarmonyPatch]
    internal static class ApplyZombieBitePatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TowerCombatManager), "ApplyZombieBite", new[] { typeof(Zombie) });

        private static void Postfix(Zombie zombie)
        {
            try
            {
                if (zombie == null || !XpOn()) return;   // XP 关=反伤不入账
                var furn = ResolveFurniture(zombie.AssignedTargetId);
                if (furn == null || DeviceXpMath.BaseCfgOf(furn.AgentConfigId) != SpikeCfgId) return;
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

    /// <summary>后缀：残骸家具直接是首参；按其 TowerDeviceComponent.WreckDamage 入账（组件读不到退化 damage 入参）。
    /// 派生行设备的组件值由 OnInit(派生行) 官方重快照（WreckDamage 结构同源），读侧无需归一。</summary>
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
                if (furniture == null || !XpOn()) return;   // XP 关=残骸撞尸不入账
                var tower = GetComponent<TowerDeviceComponent>(furniture);
                var wreck = tower != null && tower.WreckDamage > 0 ? tower.WreckDamage : damage;
                if (wreck <= 0) return;
                Award(furniture, wreck);
            }
            catch (Exception e) { EndlessGuardPlugin.Trace("[XP] HitZombieByWreck postfix: " + e.Message); }
        }
    }
}
