using System;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备经验升级——纯数值内核（Task 1；spec §3.1/§3.2/§3.3/§4.1：
/// docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md）。后续 Harmony 补丁/持久化/注入全部消费本类，不自带逻辑。
/// 纯托管 BCL 代码：零 BepInEx/Il2Cpp/UnityEngine 引用——Tests 工程按单文件链接编译（device-xp 契约）。
/// 约定：复利一律 Math.Pow；取整一律 Math.Round(..., MidpointRounding.AwayFromZero)；阈值语义="xp≥阈值即已到达该级"。
/// </summary>
internal static class DeviceXpMath
{
    /// <summary>升级累计经验阈值（升到 2/3/4/5 级所需，spec §3.2 定稿加大版：一波普通尸潮升不满）。</summary>
    internal static int[] ThresholdsDefault { get; } = { 1200, 3600, 8400, 16800 };

    // 可获经验设备名单（家具配置 ID，spec §1/§5：36001 沙包 / 36002 尖刺障碍 / 36003 电网 / 36004 机械电锯 / 99005 箭塔=99xxx 预留段）
    private static readonly int[] TrackedIds = { 36001, 36002, 36003, 36004, 99005 };

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

    /// <summary>是否可获经验设备（名单 36001/36002/36003/36004/99005）。</summary>
    internal static bool IsTrackedDevice(int furnitureConfigId) =>
        Array.IndexOf(TrackedIds, furnitureConfigId) >= 0;

    /// <summary>受击是否缩放伤害入参：真实受击缩放；修理（isRepair=true）不缩放（spec §3.4 前缀门）。</summary>
    internal static bool ShouldScaleIncomingDamage(bool isRepair) => !isRepair;

    // ===== Task 4 巡检漂移判定（纯函数：live 值 vs 该级目标值；测试工程单文件链接可直测）=====

    /// <summary>浮点判定容差：写回后本应位相等，容差只吸收游戏侧重算/序列化的浮点抖动；
    /// 1e-4 远小于任何一格成长步长（最小射程步 0.55 格），不会把真实成长误判为无漂移。</summary>
    private const float FloatEps = 1e-4f;

    /// <summary>攻击面板漂移：live ≠ TargetAttack(base, lv)（int 直比；步长参数须与 ApplyLevel 同源）。</summary>
    internal static bool AttackDrifts(int baseAtk, int lv, int liveAtk, float stepRatio = 0.15f) =>
        liveAtk != TargetAttack(baseAtk, lv, stepRatio);

    /// <summary>攻击间隔漂移：|live − TargetInterval(base, lv)| > 容差。</summary>
    internal static bool IntervalDrifts(float baseInt, int lv, float liveInt, float stepRatio = 0.08f) =>
        Math.Abs(liveInt - TargetInterval(baseInt, lv, stepRatio)) > FloatEps;

    /// <summary>目标射程平方（RangeSqr 口径，spec §4.1）：对 base 开方取 TargetRange 再平方。
    /// ApplyLevel 写回与巡检漂移判定共用此一处的开方/平方换算（杜绝两套算式各自漂移）。</summary>
    internal static float TargetRangeSqr(float baseRangeSqr, int lv, float step = 0.55f)
    {
        var baseRange = (float)Math.Sqrt(Math.Max(0f, baseRangeSqr));
        var r = TargetRange(baseRange, lv, step);
        return r * r;
    }

    /// <summary>箭塔射程漂移（RangeSqr 口径）：|live − TargetRangeSqr(base, lv)| > 容差。</summary>
    internal static bool RangeDrifts(float baseRangeSqr, int lv, float liveRangeSqr, float step = 0.55f) =>
        Math.Abs(liveRangeSqr - TargetRangeSqr(baseRangeSqr, lv, step)) > FloatEps;

    /// <summary>最大耐久漂移：只判 live &lt; TargetMaxHp 方向——ApplyLevel 只加不减，live&gt;target 时
    /// 无法收敛，判了只会每 2s 永久 trace 刷屏（读档已含上会话成长且基线偏高属 spec §6 认可的已知让步）。</summary>
    internal static bool MaxHpDrifts(int baseHp, int stepAbs, int lv, int liveMaxHp) =>
        liveMaxHp < TargetMaxHp(baseHp, lv, stepAbs);
}

/// <summary>
/// 校准熔断器状态机（评审 I1；纯托管，Tests 单文件链接可直测）：按设备（owner 实例 id）跟踪
/// 连续未收敛次数。是否真被官方钳制只有实机能证，但代码侧先封死爆炸路径：若 AddMaxDurability
/// 内部钳制（如不超过配置上限），校准会永不收敛 → 每 2s 反复 ApplyLevel → 每次又 AddCurrentDurability
/// = 无限回血漏洞。规则：连续 3 次触发 recal 后第 4 次起压制写（本会话内）；某步无漂移即收敛，
/// 连续计数归零但已熔断态不解除；仅槽切换/设备重建（新 id）由调用方 Reset 全复位。
/// 正常路径（读档一次性重快照→一次校准即收敛）计数最多到 1，完全不受影响。
/// </summary>
internal sealed class RecalFuse
{
    /// <summary>连续触发多少次后熔断（评审 I1 定值 3）。</summary>
    internal const int TripAfter = 3;

    private int _streak;                       // 连续触发 recal 的步数（无漂移步归零）
    private bool _tripped;                     // 熔断态：本会话内不因收敛解除，仅 Reset 解除

    /// <summary>调用方标记 halt 行已打过（每设备只打一次；追踪用纯调用方字段，不参与判定）。</summary>
    public bool HaltTraced;

    /// <summary>本步漂移触发：返回是否还允许 ApplyLevel 写；未熔断时计数 +1，计满即熔断。</summary>
    public bool OnTrigger()
    {
        if (_tripped) return false;
        if (++_streak >= TripAfter) _tripped = true;
        return true;
    }

    /// <summary>本步无漂移（收敛）：连续计数归零；熔断态保持（"本会话内暂停"语义）。</summary>
    public void OnConverged() => _streak = 0;

    /// <summary>全复位（槽切换/设备重建=换 id 天然新态）：计数与熔断态一并清零，校准恢复。</summary>
    public void Reset()
    {
        _streak = 0;
        _tripped = false;
    }
}
