using System;
using System.Reflection;
using HarmonyLib;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.Battle.Show;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 防御设备悬停等级显示（Task 5；spec docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md
/// §3.7 样式A，作者拍板）：后缀挂 Furniture.PushTowerDeviceHoverName(bool)，在官方悬停名推送之后把
/// 同一通道再推一次「名称 Lv.N · 升级 x/y」（满级「Lv.N MAX」）。后缀晚于原方法执行=最后写入者，
/// 官方名先落、增强名后盖，悬停关闭由官方 OnCloseName 还原，零残留。
/// cfg：Defense.ShowLevel（默认 true；EndlessGuardPlugin.Load 绑定）+ Defense.XPEnabled（Task 9：
/// false=悬停不加等级，官方原名）。
///
/// ===== Step-1 反汇编实证（Iced 反汇编 GameAssembly.dll @ dump.cs RVA 0x306AAB0，2026-10-30；非仅接口推断）=====
/// PushTowerDeviceHoverName(bool broken) 原体逐指令可读，全部调用点带符号名：
///   ① ConfigManager.Get_Config_Furniture(this.AgentConfigId)            （BaseAgent.AgentConfigId @0x30）
///   ② name = ConstantTextTools.GetLocalText(cfg.Name)                   （Config_Furniture.Name @0x18；本地化查名）
///   ③ if (broken) name = String.Format(GameKey.Text_TowerDefense_DeviceBrokenName, name)   （损坏名模板 {0}）
///   ④ EventDispatcherTools.NotifyInstance(this.ShowInstanceId, 0x45A, name)
///     —— 0x45A=1114=EnumNotify_Furniture.FurnitureSetDisplayName，show 侧处理器=
///     AgentModel.Furniture_SetDisplayName(String)（dump.cs 枚举名↔处理器名一一对应，interop 双双在册）。
/// 路由键=BaseAgent.ShowInstanceId(long @0x38，非逻辑 InstanceId)——AgentModelManager.GetAgentModel(long)
/// 即按 show id 取模型（dump.cs 353486；GetAgentModelByLogicInstanceId 是另一口径，勿混用）。
///
/// ===== 通道可写性裁决（决策门）=====
/// **主路径（样式A）成立**：通道=对 show 模型的显示名写字，postfix 用同一口径重推即可增写——
/// 不碰泛型 NotifyInstance&lt;T&gt;（interop 泛型共享有坑），直呼 AgentModel.Furniture_SetDisplayName
/// （等价的非泛型处理器入口，与原方法落点完全一致）。故
/// **HoverFallback=false 恒定，样式B（TextMesh 浮标）无需实现**（spec §3.7 方案2 的启用条件不满足）。
/// 运行期世界/模型暂不可达（BattleShowWorld 未建、GetAgentModel 返 null）只是本帧静默跳过=悬停显示
/// 官方原名，不降级常驻浮标（低频 UI 路径宁缺毋炸）。
///
/// 文本口径：x=账本累计经验，y=下一级累计阈值（与 Task 3 trace 行 xp=total/next 同约定；y 可达故
/// "x/y" 成其为进度）；满级 y=-1 → 「Lv.N MAX」。通道落点是单行 UGUI Text（UI_FurnitureHeadBar_Vm.
/// Text_Name），prefab 富文本/折行属性离线不可证 → 按 brief 降级单行纯文本（经验条/耐久条由官方
/// 头条自身的 Hp 条与事件条承担，不在此挤占）。耐久官方已有，不重复。
///
/// 边界与让步：悬停期间新入账不实时刷新（推送按悬停事件触发，再悬停即新值）；Lv1 且无账本条目
/// 的设备一字节不动（官方原名零污染）；broken 名用游戏同款模板先行拼接再附等级，损坏态显示不丢。
/// 全程 try/catch + Trace（全文约定：不静默吞）；无新 Harmony 实例（[HarmonyPatch] 走 Load 逐类
/// 自动装配）、无属性 getter 补丁、无 async 状态机。
/// </summary>
[HarmonyPatch]
internal static class DeviceXpHover
{
    /// <summary>通道裁决旗（brief 决策门）：false=样式A 主路径（后缀重推显示名）；true=样式B TextMesh
    /// 兜底。Step-1 反汇编实证通道可写（见类头），恒 false；样式B未实现（其启用条件不成立）。</summary>
    internal const bool HoverFallback = false;

    // 签名钉死（防重载/漂移静默 miss；dump.cs 376394 + interop mdump 实测 Void(Boolean)，单重载公开）
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Furniture), "PushTowerDeviceHoverName", new[] { typeof(bool) });

    private static void Postfix(Furniture __instance, bool broken)
    {
        try
        {
            if (__instance == null) return;
            if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;   // 总开关关=字节透传
            if (!DeviceXpRuntime.XpOn()) return;   // Defense.XPEnabled=false=悬停不加等级（Task 9 门）
            if (EndlessGuardPlugin.ShowLevel != null && !EndlessGuardPlugin.ShowLevel.Value) return;   // Defense.ShowLevel=false=官方原名
            if (!DeviceXpRuntime.TryGetLevelInfo(__instance, out var lv, out var xp, out var next)) return;
            if (lv <= 1 && xp <= 0) return;   // Lv1 零经验=无可展示，官方原名零污染

            var name = BaseNameOf(__instance.AgentConfigId, broken);
            if (string.IsNullOrEmpty(name)) return;   // 配置/本地化不可达=不动（与官方显示一致，宁缺毋炸）
            var text = next < 0
                ? $"{name} Lv.{lv} MAX"
                : $"{name} Lv.{lv} · 升级 {xp}/{next}";   // brief 逐字格式「名称 Lv.N · 升级 x/y」
            PushHoverName(__instance, text);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] hover postfix: " + e.Message); }
    }

    /// <summary>官方基名重建（与原方法①②③逐句同款）：配置名→本地化→损坏模板。拿不到=null（跳过增写）。</summary>
    private static string BaseNameOf(int cfgId, bool broken)
    {
        var cfg = GameCore.HotUpdate.BaseSingleton<ConfigManager>.IsInstanceCreated
            ? GameCore.HotUpdate.BaseSingleton<ConfigManager>.Instance?.Get_Config_Furniture(cfgId)
            : null;
        var name = cfg != null ? ConstantTextTools.GetLocalText(cfg.Name) : null;
        if (string.IsNullOrEmpty(name)) return null;
        if (broken)
        {
            var tpl = GameKey.Text_TowerDefense_DeviceBrokenName;
            if (!string.IsNullOrEmpty(tpl)) name = string.Format(tpl, name);   // 游戏同款 {0} 模板（RVA ④ 前 ③ 步同参）
        }
        return name;
    }

    /// <summary>同一通道重推（原方法④的处理器直达）：show id → AgentModel → Furniture_SetDisplayName。
    /// 世界未建/模型未就绪=静默跳过（本帧官方原名，下次悬停再试）。</summary>
    private static void PushHoverName(Furniture f, string text)
    {
        if (!GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.IsInstanceCreated) return;
        var mgr = GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.Instance?._AgentModelManager;
        mgr?.GetAgentModel(f.ShowInstanceId)?.Furniture_SetDisplayName(text);
    }
}
