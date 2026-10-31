using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using GameCore.HotUpdate.Battle.Logic;    // Furniture（悬停事件锚点属主）

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备悬停等级显示——fixwave2 退役改版（2026-10-31 作者裁定）。
/// **墓碑**：旧版（样式A）在官方悬停名推送后经同一 NotifyInstance(1114) 通道重推「名称 Lv.N · 升级 x/y」
/// ——playtest 实证该通道对增写已死：trace `[XP] hover push: pushed`（AgentModel.Furniture_SetDisplayName
/// 确实被调）但 UI 忽略之，增强文本从未出现。等级显示改由**派生配置行的名字本身**承载（「机械电锯 Lv.3」，
/// ArrowTower 派生行 Name/Name_Local）——官方悬停/名牌/图鉴各处零补丁原生显示。
/// 本类保留 [HarmonyPatch] 挂 Furniture.PushTowerDeviceHoverName(bool) 后缀，仅作为**悬停事件的触发器**：
/// 打一行节流（同设备 5s）trace 进度行 `[XP] hoverinfo cfg=... lv=... xp=x/y`，供作者从
/// EndlessGuard_trace.txt 读进度。cfg：Defense.ShowLevel（默认 true）现 gating 这行 trace；
/// Defense.XPEnabled=false 全停。
/// 签名钉死沿用旧版实证（dump.cs 376394 + interop mdump 实测 Void(Boolean)，单重载公开）。
/// 全程 try/catch + Trace（全文约定：不静默吞）；零 UI 写、零组件写（旧 PushHoverName/BaseNameOf 已删）。
/// </summary>
[HarmonyPatch]
internal static class DeviceXpHover
{
    /// <summary>同设备 trace 节流间隔（brief 定值 5s；TickCount 环绕安全比较）。</summary>
    private const int TraceIntervalMs = 5000;

    /// <summary>每设备上次 trace 时刻（owner 实例 id → Environment.TickCount）。</summary>
    private static readonly Dictionary<long, int> LastTrace = new();

    // 签名钉死（防重载/漂移静默 miss）
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Furniture), "PushTowerDeviceHoverName", new[] { typeof(bool) });

    private static void Postfix(Furniture __instance)
    {
        try
        {
            if (__instance == null) return;
            if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;   // 总开关关=静默
            if (!DeviceXpRuntime.XpOn()) return;   // Defense.XPEnabled=false=进度行也停
            if (EndlessGuardPlugin.ShowLevel != null && !EndlessGuardPlugin.ShowLevel.Value) return;   // Defense.ShowLevel=false=关进度行
            var owner = __instance.InstanceId;
            if (LastTrace.Count > 512) LastTrace.Clear();   // M3：节流字典按实例累积——超阈值整体清零（代价=清后每设备多一条节流行；设备销毁/跨读档残留不堆积）
            var now = Environment.TickCount;
            if (LastTrace.TryGetValue(owner, out var last) && unchecked((uint)(now - last)) < TraceIntervalMs) return;
            if (!DeviceXpRuntime.TryGetLevelInfo(__instance, out var lv, out var xp, out var next)) return;   // 无账目=不打扰
            LastTrace[owner] = now;
            EndlessGuardPlugin.Trace("[XP] hoverinfo cfg=" + DeviceXpMath.BaseCfgOf(__instance.AgentConfigId)
                                     + " row=" + __instance.AgentConfigId
                                     + " lv=" + lv + " xp=" + xp + "/" + (next < 0 ? "MAX" : next.ToString()));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] hoverinfo: " + e.Message); }
    }
}
