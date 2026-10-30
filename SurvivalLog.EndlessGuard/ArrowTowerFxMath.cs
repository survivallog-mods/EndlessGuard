using System;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔动画/弹道纯数值内核（Task 8，spec §4.4）：零 Unity/游戏依赖（单测工程按单文件链接本文件，
/// 与 DeviceXpMath 同模式）。全部角度=度、时间=秒、位移=世界单位（游戏 1 格=1 单位）。
/// 规范值：待机扫描 ±15°/2s；红旗 ±16°/0.9s；开火转向角速度上限 120°/s；后坐 0.07/0.2s 三角波；
/// 箭矢飞行上限 0.25s、命中消散半径 0.2、并发 ≤8；升级脉冲 0.6s。
/// </summary>
internal static class ArrowTowerFxMath
{
    public const float IdleSweepDeg = 15f;        // ① 待机扫描振幅
    public const float IdlePeriod = 2f;           // ① 待机扫描周期
    public const float FlagSweepDeg = 16f;        // ② 红旗摆动振幅
    public const float FlagPeriod = 0.9f;         // ② 红旗摆动周期
    public const float TrackYawSpeedDeg = 120f;   // ③ 开火转向角速度上限
    public const float RecoilDist = 0.07f;        // ④ 后坐位移（沿自身 -x）
    public const float RecoilPeriod = 0.2f;       // ④ 后坐三角波周期
    public const float PulsePeriod = 0.6f;        // ⑥ 升级脉冲时长
    public const float ArrowLifetime = 0.25f;     // ⑤ 飞行上限（到点强制消散）
    public const float ArrowSpeed = 22f;          // ⑤ 追踪箭速（Lv5 射程 5.2 内 0.24s 必达）
    public const float ArrowHitRadius = 0.2f;     // ⑤ 命中消散半径
    public const int MaxArrows = 8;               // ⑤ 并发上限（超限杀最老）
    public const float TrackWindow = 2.5f;        // ③ 追踪窗（>开火间隔1.5s，持续射击自然续期）

    /// <summary>① 待机扫描偏航角：±IdleSweepDeg 正弦，IdlePeriod 秒一个来回。</summary>
    public static float IdleYawDeg(float time) =>
        IdleSweepDeg * MathF.Sin(2f * MathF.PI * time / IdlePeriod);

    /// <summary>② 红旗摆动角：±FlagSweepDeg 正弦，FlagPeriod 秒一个来回。</summary>
    public static float FlagWaveDeg(float time) =>
        FlagSweepDeg * MathF.Sin(2f * MathF.PI * time / FlagPeriod);

    /// <summary>③ 模 360 最短路径偏航步进（Unity MoveTowardsAngle 同义、依赖少可测）：
    /// |最短差| ≤ step → 直接到位；否则按差值符号进 step。current/target 任意角域，结果归一 (-180,180]。</summary>
    public static float StepYawDeg(float current, float target, float step)
    {
        if (step < 0f) step = 0f;
        var cur = Wrap180(current);
        var diff = Wrap180(target - cur);
        return Wrap180(MathF.Abs(diff) <= step ? cur + diff : cur + MathF.Sign(diff) * step);
    }

    /// <summary>④ 后坐三角波：前半程 0→1、后半程 1→0、周期外归 0（调用方乘 RecoilDist）。</summary>
    public static float RecoilTri(float age)
    {
        if (age <= 0f) return 0f;
        var p = age / RecoilPeriod;
        if (p >= 1f) return 0f;
        return p <= 0.5f ? p * 2f : (1f - p) * 2f;
    }

    /// <summary>⑥ 升级脉冲缩放因子：1 + 0.18·sin(π·elapsed/PulsePeriod)——首尾无缝回 1，
    /// 中段峰值 1.18（Head 局部缩放，降级表现裁决见 ArrowTowerFx 类头⑥）。</summary>
    public static float PulseScale(float elapsed)
    {
        if (elapsed <= 0f || elapsed >= PulsePeriod) return 1f;
        return 1f + 0.18f * MathF.Sin(MathF.PI * elapsed / PulsePeriod);
    }

    /// <summary>⑤ 追踪箭本帧步长：min(ArrowSpeed·dt, 距离)——永不过冲（保证命中半径内收敛，
    /// 弹道为表现层必命中语义：伤害已在 FireDevice 内即时结算）。</summary>
    public static float ArrowStep(float dist, float dt) =>
        MathF.Min(ArrowSpeed * MathF.Max(dt, 0f), MathF.Max(dist, 0f));

    /// <summary>③ 世界系 yaw → Head 局部系 yaw（减父链世界朝向，(-180,180] 归一）。
    /// 待机/追踪两分支共用同一换算（F1 参照系统一修复：修复前待机分支在已是局部的 yaw 上重复减父朝向，
    /// 旋转塔根时待机面向错误中心）。</summary>
    public static float ToLocalYawDeg(float worldYaw, float parentWorldYaw) =>
        Wrap180(worldYaw - parentWorldYaw);

    /// <summary>③ 待机扫描中心的世界系 yaw = 塔根（父链）世界朝向 + Head 基准局部朝向
    /// ——塔根带旋转时待机中心=塔根朝向本身（模板 Head 基准局部=0）。</summary>
    public static float IdleCenterWorldYawDeg(float parentWorldYaw, float headBaseLocalYaw) =>
        parentWorldYaw + headBaseLocalYaw;

    /// <summary>角度归一到 (-180,180]。</summary>
    private static float Wrap180(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        else if (a <= -180f) a += 360f;
        return a;
    }
}
