using Xunit;

namespace SurvivalLog.EndlessGuard.Tests;

/// <summary>
/// DeviceXpMath 纯数值内核契约测试（Task 1）。
/// 断言值 = 设计 spec 定稿数值（docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md §3.2/§3.3/§4.1，task-1-brief Step 1）。
/// 测试工程按单文件链接 DeviceXpMath.cs（同程序集 internal 直读），不引用主工程。
/// </summary>
public class DeviceXpMathTests
{
    // ---- 等级曲线（spec §3.2：Lv1 起、上限 Lv5、阈值=累计经验） ----

    [Fact]
    public void ThresholdsDefault_IsSpecValues()
    {
        Assert.Equal(new[] { 1200, 3600, 8400, 16800 }, DeviceXpMath.ThresholdsDefault);
    }

    [Theory]
    [InlineData(0, 1)]       // 零经验=1级
    [InlineData(1199, 1)]    // 阈值边界下沿
    [InlineData(1200, 2)]    // 达 2 级阈值即升（"阈值即到达"）
    [InlineData(3600, 3)]
    [InlineData(8400, 4)]
    [InlineData(16800, 5)]   // 满级
    [InlineData(999999, 5)]  // 超出封顶
    public void LevelFromXp_MapsXpToLevel(int xp, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.LevelFromXp(xp, 5, DeviceXpMath.ThresholdsDefault));
    }

    [Theory]
    [InlineData(1, 1200)]
    [InlineData(2, 3600)]
    [InlineData(3, 8400)]
    [InlineData(4, 16800)]
    [InlineData(5, -1)]      // 满级=MAX
    public void NextThreshold_ReturnsNextLevelCost(int level, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.NextThreshold(level, DeviceXpMath.ThresholdsDefault));
    }

    // ---- 目标属性（spec §3.3 逐级成长） ----

    [Theory]
    [InlineData(12, 1, 12)]   // Lv1=面板原值（×1.15^0）
    [InlineData(12, 2, 14)]   // 12×1.15=13.8→14（复利逐级验证）
    [InlineData(12, 5, 21)]   // 电锯 Lv5：12×1.15^4(=1.749)≈20.99→21（brief Step 1）
    public void TargetAttack_Compounds15PctPerLevel(int baseAtk, int level, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.TargetAttack(baseAtk, level));
    }

    [Fact]
    public void TargetInterval_ChainsawLv5_IsSpec143()
    {
        // 电锯 2s：2×0.92^4≈1.4328→1.43（间隔 −8%/级，brief Step 1 ±0.01）
        Assert.InRange(DeviceXpMath.TargetInterval(2f, 5), 1.42f, 1.44f);
    }

    [Fact]
    public void TargetInterval_NeverBelow60PctOfBase()
    {
        // 下限 0.6×base：0.92^19≈0.205 远低于 0.6，必须钳在 0.6×base
        Assert.InRange(DeviceXpMath.TargetInterval(1f, 20), 0.599f, 0.601f);
        Assert.InRange(DeviceXpMath.TargetInterval(10f, 20), 5.99f, 6.01f);
    }

    [Fact]
    public void TargetInterval_Level1_IsBase()
    {
        Assert.Equal(2f, DeviceXpMath.TargetInterval(2f, 1));
    }

    [Theory]
    [InlineData(600, 1, 150, 600)]    // Lv1=基础耐久
    [InlineData(600, 5, 150, 1200)]   // 沙包 Lv5：600+4×150=1200（brief Step 1）
    public void TargetMaxHp_AddsStepPerLevel(int baseHp, int level, int stepAbs, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.TargetMaxHp(baseHp, level, stepAbs));
    }

    [Fact]
    public void TargetRange_TowerLv5_IsSpec52()
    {
        // 箭塔 3.0：+0.55/级 ×4=5.2（spec §4.1 作者拍板端点，brief Step 1 ±0.01）
        Assert.InRange(DeviceXpMath.TargetRange(3.0f, 5), 5.19f, 5.21f);
    }

    [Fact]
    public void TargetRange_Level1_IsBase()
    {
        Assert.Equal(3.0f, DeviceXpMath.TargetRange(3.0f, 1));
    }

    [Fact]
    public void IncomingDamageFactor_WeaponLv5_IsSpec0656()
    {
        // 武器受击 ×0.90/级：0.9^4≈0.6561（brief Step 1 ±0.001）
        Assert.InRange(DeviceXpMath.IncomingDamageFactor(5, 0.9f), 0.655f, 0.657f);
    }

    [Fact]
    public void IncomingDamageFactor_Factor1OrLevel1_IsOne()
    {
        // 箭塔无减伤成长（传 1f）恒 1；Lv1 任何系数均无成长
        Assert.Equal(1f, DeviceXpMath.IncomingDamageFactor(5, 1f));
        Assert.Equal(1f, DeviceXpMath.IncomingDamageFactor(1, 0.9f));
    }

    // ---- 耐久成长步长（武器 0.30 / 沙包 0.25 / 箭塔 0.15） ----

    [Theory]
    [InlineData(200, 0.30f, 60)]
    [InlineData(600, 0.25f, 150)]
    [InlineData(100, 0.15f, 15)]  // 0.15f 二进制=0.150000006（略大于 0.15），100×0.15f=15.0000→截断 15，与 spec §4.1 箭塔 Lv5 HP160 端点吻合
    public void StepAbs_TruncatesBaseTimesRatio(int baseHp, float ratio, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.StepAbs(baseHp, ratio));
    }

    // ---- 经验结算（spec §3.1） ----

    [Fact]
    public void AwardForFire_AttackTimesHitsTimesMult()
    {
        Assert.Equal(36, DeviceXpMath.AwardForFire(12, 3, 1f));
        // 2.5 中点 → AwayFromZero=3（银行家舍入会是 2），钉死取整约定
        Assert.Equal(3, DeviceXpMath.AwardForFire(5, 1, 0.5f));
    }

    [Fact]
    public void AwardForTaken_SandbagDoubleRate()
    {
        Assert.Equal(6, DeviceXpMath.AwardForTaken(3, 2f));   // 沙包承伤经验系数 2.0（brief Step 1）
    }

    [Fact]
    public void AwardForKill_HalfOfKillingDamage()
    {
        Assert.Equal(50, DeviceXpMath.AwardForKill(100, 0.5f));   // 击杀追加 50%（spec §3.1）
    }

    // ---- 设备名单（含派生行）/ 派生行身份（fixwave2：升级=换配置行） ----

    [Theory]
    [InlineData(36001, true)]   // 沙包
    [InlineData(36002, true)]   // 尖刺障碍
    [InlineData(36003, true)]   // 电网
    [InlineData(36004, true)]   // 机械电锯
    [InlineData(99005, true)]   // 箭塔（mod 新增，99xxx 预留段，spec §5）
    [InlineData(36000, false)]
    [InlineData(36005, false)]  // 官方邻位空号（官方续号大概率撞号，spec §5 明确不用）
    [InlineData(99006, false)]
    [InlineData(0, false)]
    public void IsTrackedDevice_MatchesSpecWhitelist(int configId, bool expected)
    {
        Assert.Equal(expected, DeviceXpMath.IsTrackedDevice(configId));
    }

    [Theory]
    [InlineData(99112, 36001, 2)]    // 沙包 Lv2（typeIdx=1）
    [InlineData(99115, 36001, 5)]    // 沙包 Lv5
    [InlineData(99122, 36002, 2)]    // 尖刺 Lv2（typeIdx=2）
    [InlineData(99132, 36003, 2)]    // 电网 Lv2（typeIdx=3）
    [InlineData(99143, 36004, 3)]    // 电锯 Lv3（typeIdx=4）
    [InlineData(99145, 36004, 5)]
    [InlineData(99152, 99005, 2)]    // 箭塔 Lv2（typeIdx=5）
    [InlineData(99155, 99005, 5)]    // 箭塔 Lv5
    public void TrySplitDerivedRow_DecodesScheme(int rowId, int expectedBase, int expectedLv)
    {
        Assert.True(DeviceXpMath.TrySplitDerivedRow(rowId, out var baseCfg, out var lv));
        Assert.Equal(expectedBase, baseCfg);
        Assert.Equal(expectedLv, lv);
        Assert.Equal(expectedLv, DeviceXpMath.LevelOfDerivedRow(rowId));
        Assert.Equal(expectedBase, DeviceXpMath.BaseCfgOf(rowId));
        Assert.True(DeviceXpMath.IsDerivedRow(rowId));
        Assert.True(DeviceXpMath.IsTrackedDevice(rowId));
    }

    [Theory]
    [InlineData(99111)]   // lv=1 不是合法派生行（lv1=基础行本身）
    [InlineData(99116)]   // lv=6 越界（上限 5）
    [InlineData(99101)]   // typeIdx=0 段
    [InlineData(99160)]   // typeIdx=6 越界
    [InlineData(99120)]   // lv=0
    [InlineData(99121)]   // lv=1 段（22 前的 21 一样非法）
    [InlineData(99005)]   // 基础行不是派生行
    [InlineData(36001)]
    [InlineData(0)]
    [InlineData(99006)]
    public void TrySplitDerivedRow_RejectsNonScheme(int id)
    {
        Assert.False(DeviceXpMath.TrySplitDerivedRow(id, out _, out _));
        Assert.Equal(0, DeviceXpMath.LevelOfDerivedRow(id));
        Assert.False(DeviceXpMath.IsDerivedRow(id));
        Assert.Equal(id, DeviceXpMath.BaseCfgOf(id));   // 非派生行透传（账本/悬停归一口径）
    }

    [Theory]
    [InlineData(36001, 1, 36001)]    // lv1=基础行本身（不换行）
    [InlineData(36001, 2, 99112)]
    [InlineData(36001, 5, 99115)]
    [InlineData(36002, 3, 99123)]
    [InlineData(36003, 4, 99134)]
    [InlineData(36004, 2, 99142)]
    [InlineData(36004, 5, 99145)]
    [InlineData(99005, 1, 99005)]    // 箭塔 lv1=99005 本身
    [InlineData(99005, 2, 99152)]
    [InlineData(99005, 5, 99155)]
    public void RowIdFor_MapsBaseAndLevelToRow(int baseCfg, int level, int expected)
    {
        Assert.Equal(expected, DeviceXpMath.RowIdFor(baseCfg, level));
    }

    [Theory]
    [InlineData(36001, 6)]    // 越级（上限 5）
    [InlineData(36005, 2)]    // 非名单设备没有派生行
    [InlineData(99006, 3)]
    [InlineData(0, 2)]
    public void RowIdFor_InvalidBaseOrLevel_ReturnsMinusOne(int baseCfg, int level)
    {
        Assert.Equal(-1, DeviceXpMath.RowIdFor(baseCfg, level));
    }

    [Fact]
    public void RowIdFor_NonPositiveLevel_NormalizesToBaseRow()
    {
        // 病态账本（lv=0）防御：≤1 一律归一到基础行（巡检按"无成长"处理，不会因 -1 卡死补写）
        Assert.Equal(36001, DeviceXpMath.RowIdFor(36001, 0));
        Assert.Equal(36001, DeviceXpMath.RowIdFor(36001, -3));
    }

    [Fact]
    public void RowIdFor_RoundTripsThroughTrySplit()
    {
        foreach (var baseCfg in DeviceXpMath.TrackedBaseIds)
            for (int lv = 2; lv <= 5; lv++)
            {
                var row = DeviceXpMath.RowIdFor(baseCfg, lv);
                Assert.True(DeviceXpMath.TrySplitDerivedRow(row, out var back, out var backLv));
                Assert.Equal(baseCfg, back);
                Assert.Equal(lv, backLv);
            }
    }

    [Theory]
    [InlineData("机械电锯", 3, "机械电锯 Lv.3")]
    [InlineData("箭塔", 2, "箭塔 Lv.2")]
    [InlineData("沙包", 5, "沙包 Lv.5")]
    public void WithLevelSuffix_AppendsLevel(string baseName, int lv, string expected)
    {
        Assert.Equal(expected, DeviceXpMath.WithLevelSuffix(baseName, lv));
    }

    // ---- Task 9 cfg 接线：成长步长/等级上限参数化（可选参数默认值=spec §3.5 定值）----

    [Fact]
    public void TargetAttack_CustomStep_Compounds()
    {
        // cfg WeaponAtkStep=0.20：10×1.2²=14.4→14（复利式与默认步长同款，只是比例换值）
        Assert.Equal(14, DeviceXpMath.TargetAttack(10, 3, 0.20f));
        Assert.Equal(10, DeviceXpMath.TargetAttack(10, 1, 0.20f));   // Lv1 恒=面板原值
    }

    [Fact]
    public void TargetInterval_CustomStep_Clamped()
    {
        // cfg WeaponIntervalStep=0.10：2×0.9²=1.62
        Assert.InRange(DeviceXpMath.TargetInterval(2f, 3, 0.10f), 1.61f, 1.63f);
        // 0.6×base 钳制不随步长变化：步长加大也一样钳住
        Assert.InRange(DeviceXpMath.TargetInterval(1f, 20, 0.20f), 0.599f, 0.601f);
    }

    [Fact]
    public void TargetRange_CustomStep_Linear()
    {
        // cfg TowerRangeStep=0.70：3+4×0.7=5.8（箭塔派生行 AttackRange 用同一换算）
        Assert.InRange(DeviceXpMath.TargetRange(3.0f, 5, 0.70f), 5.79f, 5.81f);
        Assert.Equal(3.0f, DeviceXpMath.TargetRange(3.0f, 1, 0.70f));   // Lv1 恒=基础射程
    }

    [Fact]
    public void NextThreshold_MaxLevelCap_ShowsMaxEarly()
    {
        // cfg MaxLevel=3：lv3 即 MAX（-1），lv2 仍给下一阈值；默认 maxLevel=0 行为不变（老测试锁定）
        Assert.Equal(-1, DeviceXpMath.NextThreshold(3, DeviceXpMath.ThresholdsDefault, 3));
        Assert.Equal(3600, DeviceXpMath.NextThreshold(2, DeviceXpMath.ThresholdsDefault, 3));
        // 已超上限的账本等级（只升不降裁决：属性保留）悬停同样显示 MAX
        Assert.Equal(-1, DeviceXpMath.NextThreshold(4, DeviceXpMath.ThresholdsDefault, 3));
    }

    [Fact]
    public void LevelFromXp_LowerMaxLevel_Caps()
    {
        // cfg MaxLevel=3：16800 经验（原本 Lv5）只到 Lv3
        Assert.Equal(3, DeviceXpMath.LevelFromXp(16800, 3, DeviceXpMath.ThresholdsDefault));
    }
}
