using System;
using Xunit;

namespace SurvivalLog.EndlessGuard.Tests;

/// <summary>
/// ArrowTowerFxMath 纯数值内核契约测试（Task 8）。断言值 = spec §4.4 定稿数值
///（±15°/2s 待机、±16°/0.9s 红旗、120°/s 转向上限、0.07/0.2s 后坐三角波、0.6s 脉冲、
/// 0.25s 飞行上限/22 速/0.2 命中半径/≤8 并发）。测试工程按单文件链接 ArrowTowerFxMath.cs（同 internal 直读）。
/// 浮点断言统一显式 ε（=1e-3 或 1e-4），不依赖 xunit 的 tolerance 重载歧义。
/// </summary>
public class ArrowTowerFxMathTests
{
    private const float Eps = 1e-3f;
    private static void Equal(float expected, float actual, float eps = Eps) => Assert.True(MathF.Abs(expected - actual) <= eps, $"expected={expected} actual={actual}");

    // ---- ①② 正弦扫描/摆动（幅值+相位+周期） ----

    [Theory]
    [InlineData(0f, 0f)]          // t=0 过零点（基准角）
    [InlineData(0.5f, 15f)]       // 2s 周期的 1/4 → +15°（峰值）
    [InlineData(1.5f, -15f)]      // 3/4 → -15°
    [InlineData(2.0f, 0f)]        // 整周期回零
    public void IdleYawDeg_Sweeps15DegOver2s(float t, float expected)
    {
        Equal(expected, ArrowTowerFxMath.IdleYawDeg(t));
    }

    [Fact]
    public void IdleYawDeg_AmplitudeNeverExceeds15()
    {
        for (var t = 0f; t < 4f; t += 0.025f)
            Assert.InRange(ArrowTowerFxMath.IdleYawDeg(t), -15.0001f, 15.0001f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.225f, 16f)]     // 0.9s 周期的 1/4 → +16°
    [InlineData(0.675f, -16f)]
    public void FlagWaveDeg_Sweeps16DegOver09s(float t, float expected)
    {
        Equal(expected, ArrowTowerFxMath.FlagWaveDeg(t));
    }

    // ---- ③ 模 360 最短路径偏航步进 ----

    [Fact]
    public void StepYawDeg_NoStep_IsNoOp()
    {
        Equal(30f, ArrowTowerFxMath.StepYawDeg(30f, 30f, 0f), 1e-4f);
    }

    [Fact]
    public void StepYawDeg_WithinStep_LandsExactly()
    {
        // |diff|≤step → 直接到位
        Equal(38f, ArrowTowerFxMath.StepYawDeg(30f, 38f, 10f), 1e-4f);
    }

    [Fact]
    public void StepYawDeg_ClampsToStep()
    {
        Equal(40f, ArrowTowerFxMath.StepYawDeg(30f, 55f, 10f), 1e-4f);   // 55-30=15>10 → 只进 10
        Equal(20f, ArrowTowerFxMath.StepYawDeg(30f, 5f, 10f), 1e-4f);    // 负向同样钳制
    }

    [Fact]
    public void StepYawDeg_WrapsThroughShortestPath()
    {
        // 350→10 最短差=+20（经 360），|20|≤120 → 一步到位，结果归一 (-180,180] = 10
        Equal(10f, ArrowTowerFxMath.StepYawDeg(350f, 10f, 120f), 1e-4f);
    }

    [Fact]
    public void StepYawDeg_BigGap_TakesShortestDirection()
    {
        // 170→-170 最短差=+20（经 180）：step=120 一步到位 190→归一 -170
        Equal(-170f, ArrowTowerFxMath.StepYawDeg(170f, -170f, 120f), 1e-4f);
        // step=5：从 170 出发正向进 5 → 175
        Equal(175f, ArrowTowerFxMath.StepYawDeg(170f, -170f, 5f), 1e-4f);
    }

    [Fact]
    public void StepYawDeg_NormalizesOutputDomain()
    {
        var r = ArrowTowerFxMath.StepYawDeg(400f, 400f, 0f);   // 400≡40
        Equal(40f, r, 1e-4f);
        Assert.InRange(r, -180f, 180f);
    }

    [Fact]
    public void StepYawDeg_NegativeStepTreatedAsZero()
    {
        Equal(30f, ArrowTowerFxMath.StepYawDeg(30f, 55f, -3f), 1e-4f);   // 防御：负 step=原地
    }

    // ---- ③ 世界→局部参照系统一（F1 修复契约：待机/追踪共用同一换算） ----

    [Fact]
    public void YawFrames_RotatedRoot90_IdleCenterIsRootFacing()
    {
        // 塔根旋转 90°（Head 基准局部 0）：待机中心世界系=90（=塔根朝向），
        // 经共用换算入局部=0（弩机正对塔根前方摆 ±15°，不再偏 −90）
        var centerWorld = ArrowTowerFxMath.IdleCenterWorldYawDeg(90f, 0f);
        Equal(90f, centerWorld, 1e-4f);
        Equal(0f, ArrowTowerFxMath.ToLocalYawDeg(centerWorld, 90f), 1e-4f);
    }

    [Theory]
    [InlineData(0f, 90f, -90f)]     // 塔根 90°、目标在世界 0° → 局部 -90（评审指定用例）
    [InlineData(90f, 90f, 0f)]      // 目标正对塔根朝向 → 局部 0
    [InlineData(45f, 0f, 45f)]      // 塔根无旋转 → 世界=局部
    [InlineData(-90f, 90f, 180f)]   // 差 -180 归一到 (-180,180] 上沿
    public void ToLocalYawDeg_SubtractsParentWorldYaw(float worldYaw, float parentYaw, float expected)
    {
        Equal(expected, ArrowTowerFxMath.ToLocalYawDeg(worldYaw, parentYaw), 1e-4f);
    }

    // ---- ④ 后坐三角波 ----

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.05f, 0.5f)]     // 前半程中点
    [InlineData(0.1f, 1f)]        // 峰值（半周期）
    [InlineData(0.15f, 0.5f)]     // 后半程中点
    [InlineData(0.2f, 0f)]        // 周期末回零
    [InlineData(0.3f, 0f)]        // 周期外归零
    [InlineData(-1f, 0f)]         // 未开始（空闲哨兵态）
    public void RecoilTri_TriangleWave(float age, float expected)
    {
        Equal(expected, ArrowTowerFxMath.RecoilTri(age));
    }

    // ---- ⑥ 脉冲缩放因子 ----

    [Fact]
    public void PulseScale_SeamsAtZeroAndOne()
    {
        Equal(1f, ArrowTowerFxMath.PulseScale(0f), 1e-4f);
        Equal(1f, ArrowTowerFxMath.PulseScale(0.6f), 1e-3f);      // 周期末无缝回 1
        Equal(1f, ArrowTowerFxMath.PulseScale(0.7f), 1e-4f);      // 周期外=1
        Equal(1f, ArrowTowerFxMath.PulseScale(-1f), 1e-4f);
    }

    [Fact]
    public void PulseScale_Peak118AtMidpoint()
    {
        Equal(1.18f, ArrowTowerFxMath.PulseScale(0.3f));          // 0.6s 中段峰值
        Equal(1f + 0.18f * MathF.Sin(MathF.PI * 0.25f), ArrowTowerFxMath.PulseScale(0.15f));
    }

    // ---- ⑤ 追踪箭步长（永不过冲 → 必命中收敛） ----

    [Fact]
    public void ArrowStep_ClampedByDistance()
    {
        Equal(3f, ArrowTowerFxMath.ArrowStep(3f, 1f), 1e-4f);     // 距离 3 < 22 → 步长=3
        Equal(0f, ArrowTowerFxMath.ArrowStep(0f, 1f), 1e-4f);     // 已在目标点 → 0
    }

    [Fact]
    public void ArrowStep_ClampedBySpeed()
    {
        Equal(0.352f, ArrowTowerFxMath.ArrowStep(100f, 0.016f), 1e-3f);   // 22×0.016=0.352
    }

    [Fact]
    public void ArrowStep_NeverOvershoots()
    {
        // 任意 (dist, dt)：步长 ≤ 距离（保证一帧内不跨过目标点 → 命中半径内必收敛）
        Assert.True(ArrowTowerFxMath.ArrowStep(0.13f, 0.033f) <= 0.13f + 1e-5f);
        Assert.True(ArrowTowerFxMath.ArrowStep(0.05f, 0.033f) <= 0.05f + 1e-5f);
    }

    [Fact]
    public void ArrowStep_NegativeInputsAreSafe()
    {
        Equal(0f, ArrowTowerFxMath.ArrowStep(-2f, 0.016f), 1e-4f);
        Equal(0f, ArrowTowerFxMath.ArrowStep(2f, -0.016f), 1e-4f);
    }

    // ---- 常量契约（spec §4.4 锚定，防实现漂移） ----

    [Fact]
    public void Constants_MatchSpec44()
    {
        Assert.Equal(15f, ArrowTowerFxMath.IdleSweepDeg);
        Assert.Equal(2f, ArrowTowerFxMath.IdlePeriod);
        Assert.Equal(16f, ArrowTowerFxMath.FlagSweepDeg);
        Assert.Equal(0.9f, ArrowTowerFxMath.FlagPeriod);
        Assert.Equal(120f, ArrowTowerFxMath.TrackYawSpeedDeg);
        Assert.Equal(0.07f, ArrowTowerFxMath.RecoilDist);
        Assert.Equal(0.2f, ArrowTowerFxMath.RecoilPeriod);
        Assert.Equal(0.6f, ArrowTowerFxMath.PulsePeriod);
        Assert.Equal(0.25f, ArrowTowerFxMath.ArrowLifetime);
        Assert.Equal(0.2f, ArrowTowerFxMath.ArrowHitRadius);
        Assert.Equal(8, ArrowTowerFxMath.MaxArrows);
    }
}
