using System;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备经验升级——纯数值内核（Task 1；spec §3.1/§3.2/§3.3/§4.1：
/// docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md）。后续 Harmony 补丁/持久化/注入全部消费本类，不自带逻辑。
/// 纯托管 BCL 代码：零 BepInEx/Il2Cpp/UnityEngine 引用——Tests 工程按单文件链接编译（device-xp 契约）。
/// 约定：复利一律 Math.Pow；取整一律 Math.Round(..., MidpointRounding.AwayFromZero)；阈值语义="xp≥阈值即已到达该级"。
/// fixwave2（2026-10-31 作者裁定"两个动作不应该来回覆写"重构）：升级=换配置行——本类新增派生等级行
/// 身份纯函数（RowIdFor/BaseCfgOf/IsTrackedDevice 派生段）；旧的组件直写/漂移判定/校准熔断（RecalFuse）
/// 随官方重快照对抗路线一并移除。
/// </summary>
internal static class DeviceXpMath
{
    /// <summary>升级累计经验阈值（升到 2/3/4/5 级所需，spec §3.2 定稿加大版：一波普通尸潮升不满）。</summary>
    internal static int[] ThresholdsDefault { get; } = { 1200, 3600, 8400, 16800 };

    // ===== 派生等级行身份（fixwave2 架构核心：升级=换配置行，官方代码是组件值的唯一写入者）=====
    // 行号方案：99100 + typeIdx*10 + lv（typeIdx：36001→1…99005→5；lv∈2..5）→
    // 99112-99115 / 99122-99125 / 99132-99135 / 99142-99145 / 99152-99155。
    // gamekb 全表（Config_Furniture/TowerDevice/Item/ProductionList/DefenseLv）实测 99xxx 段零占用（2026-10-31 复核）。
    // 卸载安全：派生行随本 dll 移除而消失（进程内存态），删 dll 前先开 Defense.ResetLevels 进档一次，
    // DeviceXpRuntime 会把全部设备换回基础行并清账本，存档回到纯官方状态（还原机理见其 ResetAllLevelsIfArmed）。
    internal const int DerivedRowBase = 99100;

    /// <summary>可获经验基础设备名单（家具配置 ID，spec §1/§5：36001 沙包 / 36002 尖刺障碍 / 36003 电网 /
    /// 36004 机械电锯 / 99005 箭塔）。下标+1=TypeIdxOf 的 typeIdx（派生行号方案依赖此顺序）。</summary>
    internal static readonly int[] TrackedBaseIds = { 36001, 36002, 36003, 36004, 99005 };

    /// <summary>基础设备 id → 派生行号方案的 typeIdx（1..5）；非名单=0。</summary>
    internal static int TypeIdxOf(int baseCfgId)
    {
        for (int i = 0; i < TrackedBaseIds.Length; i++)
            if (TrackedBaseIds[i] == baseCfgId) return i + 1;
        return 0;
    }

    /// <summary>等级 → 该设备应处的配置行号：lv≤1=基础行本身；lv 2..5=派生行；越界/非名单=-1（调用方视为"无行可换，跳过写"）。</summary>
    internal static int RowIdFor(int baseCfgId, int level)
    {
        var t = TypeIdxOf(baseCfgId);
        if (t <= 0) return -1;
        if (level <= 1) return baseCfgId;
        if (level > 5) return -1;
        return DerivedRowBase + t * 10 + level;
    }

    /// <summary>派生行号拆解：命中返回 true + 基础 id + 等级（lv 2..5）；否则 false。</summary>
    internal static bool TrySplitDerivedRow(int id, out int baseCfgId, out int level)
    {
        baseCfgId = 0; level = 0;
        var d = id - DerivedRowBase;
        if (d < 0) return false;
        var t = d / 10;
        var lv = d % 10;
        if (t < 1 || t > TrackedBaseIds.Length || lv < 2 || lv > 5) return false;
        baseCfgId = TrackedBaseIds[t - 1];
        level = lv;
        return true;
    }

    /// <summary>任意配置 id → 基础设备 id（派生行拆回基础；其余原样透传——账本/悬停/巡检统一经此归一）。</summary>
    internal static int BaseCfgOf(int cfgId) => TrySplitDerivedRow(cfgId, out var baseCfg, out _) ? baseCfg : cfgId;

    /// <summary>是否派生等级行（99112..99155 的合法段）。</summary>
    internal static bool IsDerivedRow(int cfgId) => TrySplitDerivedRow(cfgId, out _, out _);

    /// <summary>派生等级行的等级（2..5）；非派生行=0。</summary>
    internal static int LevelOfDerivedRow(int cfgId) => TrySplitDerivedRow(cfgId, out _, out var lv) ? lv : 0;

    /// <summary>显示名等级后缀（派生行 Name/Name_Local 建行用）：baseName + " Lv.N"。</summary>
    internal static string WithLevelSuffix(string baseName, int level) => baseName + " Lv." + level;

    /// <summary>巡检核对的期望行号（fixwave2 F1 回归修复）：账本等级本身就是真值——不经 cfg MaxLevel
    /// 夹取（调低上限只封顶今后的升级，走 Award→LevelFromXp(maxLevel) 路径；已升级设备绝不因 cfg
    /// 事后调整被换回低级行削血）。ledgerLv≤0 病态归一基础行；非名单/越级（&gt;5，脏账本防）=-1
    ///（调用方跳过写，不猜低级行）。</summary>
    internal static int ExpectedRowFor(int baseCfgId, int ledgerLv) =>
        RowIdFor(baseCfgId, Math.Max(1, ledgerLv));

    /// <summary>累计经验→当前等级（Lv1 起；xp 达阈值即升；封顶 maxLevel）。</summary>
    internal static int LevelFromXp(int xp, int maxLevel, int[] thresholds)
    {
        var lv = 1;
        foreach (var t in thresholds)
        {
            if (xp < t) break;
            lv++;
        }
        return Math.Min(lv, maxLevel);
    }

    /// <summary>升到 level+1 级所需累计经验；已满级（≥有效上限，maxLevel&gt;0 时= maxLevel，否则 thresholds.Length+1）
    /// 返回 -1（MAX）。Task 9：maxLevel 供 cfg Defense.MaxLevel 传参（降上限时提前显示 MAX）。</summary>
    internal static int NextThreshold(int level, int[] thresholds, int maxLevel = 0)
    {
        var cap = maxLevel > 0 ? maxLevel : thresholds.Length + 1;
        return level >= cap ? -1 : thresholds[level - 1];
    }

    /// <summary>目标攻击：面板 ×(1+stepRatio)^(lv-1) 复利，四舍五入（spec §3.3 默认 0.15；Task 9 由 cfg
    /// Defense.WeaponAtkStep 传参，可选参数默认值=spec §3.5 定值，下同）。</summary>
    internal static int TargetAttack(int baseAtk, int level, float stepRatio = 0.15f) =>
        (int)Math.Round(baseAtk * Math.Pow(1.0 + stepRatio, level - 1), MidpointRounding.AwayFromZero);

    /// <summary>目标攻击间隔：面板 ×(1-stepRatio)^(lv-1)（默认 −8%/级），下限 0.6×base（spec §3.3 钳制，
    /// 攻速不封顶缩；钳制值不入 cfg）。</summary>
    internal static float TargetInterval(float baseInt, int level, float stepRatio = 0.08f) =>
        Math.Max(baseInt * (float)Math.Pow(1.0 - stepRatio, level - 1), baseInt * 0.6f);

    /// <summary>目标耐久：base + (lv-1)×stepAbs（stepAbs 由调用方按 StepAbs 预先算好传入）。</summary>
    internal static int TargetMaxHp(int baseHp, int level, int stepAbs) =>
        baseHp + (level - 1) * stepAbs;

    /// <summary>目标射程：base + (lv-1)×step 格（箭塔专属成长；默认 0.55，spec §4.1 端点 3.0→5.2；
    /// Task 9 由 cfg Defense.TowerRangeStep 传参）。</summary>
    internal static float TargetRange(float baseRange, int level, float step = 0.55f) =>
        baseRange + (level - 1) * step;

    /// <summary>受击伤害系数：perLevelFactor^(lv-1)（武器 0.9/级；箭塔传 1f=无减伤成长；Lv1 恒 1）。</summary>
    internal static float IncomingDamageFactor(int level, float perLevelFactor) =>
        (float)Math.Pow(perLevelFactor, level - 1);

    /// <summary>耐久成长步长：base×ratio 截断取整（brief 逐字 (int)(baseHp*ratio)；武器 0.30 / 沙包 0.25 / 箭塔 0.15）。
    /// 单测实证 0.15f 二进制=0.150000006 略大于 0.15：(int)(100*0.15f)=15，恰与 spec §4.1 箭塔 Lv5 HP160 端点吻合。</summary>
    internal static int StepAbs(int baseHp, float ratio) => (int)(baseHp * ratio);

    /// <summary>开火经验：attack×hitCount×mult（天赋加成前面板值，spec §3.1）。</summary>
    internal static int AwardForFire(int attack, int hitCount, float mult) =>
        (int)Math.Round(attack * hitCount * mult, MidpointRounding.AwayFromZero);

    /// <summary>承伤经验：damage×mult（沙包传 2.0 承伤经验系数，spec §3.1 作者决议）。</summary>
    internal static int AwardForTaken(int damage, float mult) =>
        (int)Math.Round(damage * mult, MidpointRounding.AwayFromZero);

    /// <summary>击杀追加经验：该次伤害×killRatio（默认 0.5，spec §3.1，可 cfg 关）。</summary>
    internal static int AwardForKill(int killingDamage, float killRatio) =>
        (int)Math.Round(killingDamage * killRatio, MidpointRounding.AwayFromZero);

    /// <summary>是否可获经验设备（基础名单 36001/36002/36003/36004/99005，或其派生等级行 99112..99155）。</summary>
    internal static bool IsTrackedDevice(int furnitureConfigId) =>
        Array.IndexOf(TrackedBaseIds, furnitureConfigId) >= 0 || IsDerivedRow(furnitureConfigId);
}
