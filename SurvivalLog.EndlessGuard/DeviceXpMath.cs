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

    /// <summary>升到 level+1 级所需累计经验；已满级（≥thresholds.Length+1）返回 -1（MAX）。</summary>
    internal static int NextThreshold(int level, int[] thresholds) =>
        level >= thresholds.Length + 1 ? -1 : thresholds[level - 1];

    /// <summary>目标攻击：面板 ×1.15^(lv-1) 复利，四舍五入（spec §3.3 武器成长）。</summary>
    internal static int TargetAttack(int baseAtk, int level) =>
        (int)Math.Round(baseAtk * Math.Pow(1.15, level - 1), MidpointRounding.AwayFromZero);

    /// <summary>目标攻击间隔：面板 ×0.92^(lv-1)（间隔 −8%/级），下限 0.6×base（spec §3.3 钳制，攻速不封顶缩）。</summary>
    internal static float TargetInterval(float baseInt, int level) =>
        Math.Max(baseInt * (float)Math.Pow(0.92, level - 1), baseInt * 0.6f);

    /// <summary>目标耐久：base + (lv-1)×stepAbs（stepAbs 由调用方按 StepAbs 预先算好传入）。</summary>
    internal static int TargetMaxHp(int baseHp, int level, int stepAbs) =>
        baseHp + (level - 1) * stepAbs;

    /// <summary>目标射程：base + (lv-1)×0.55 格（箭塔专属成长，spec §4.1 端点 3.0→5.2）。</summary>
    internal static float TargetRange(float baseRange, int level) =>
        baseRange + (level - 1) * 0.55f;

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
}
