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
/// F5（2026-10-31 playtest 取证补强）：增强从不出现且零错误——①首次触发打全量解析现场（槽/格键/
/// 账本键字符串命中与否/showId）；②推送通道逐态节流 trace（pushed/缺失态/异常文本）。两处一次性
/// 或同文一次，稳态零噪音。显示规则收口为「有账目且（Lv&gt;1 或 XP&gt;0）」（原条件等价，改写对齐 spec）。
/// 全程 try/catch + Trace（全文约定：不静默吞）；无新 Harmony 实例（[HarmonyPatch] 走 Load 逐类
/// 自动装配）、无属性 getter 补丁、无 async 状态机。
/// </summary>
[HarmonyPatch]
internal static class DeviceXpHover
{
    /// <summary>通道裁决旗（brief 决策门）：false=样式A 主路径（后缀重推显示名）；true=样式B TextMesh
    /// 兜底。Step-1 反汇编实证通道可写（见类头），恒 false；样式B未实现（其启用条件不成立）。</summary>
    internal const bool HoverFallback = false;

    // ── F5 取证（2026-10-31 playtest：官方 HP 全设备显示、我方 Lv/XP 增强从不出现且零错误 trace）──
    // 一次性/节流探针：①首次触发打全量解析现场（cfg/槽/格键/账本键字符串命中与否/showId）；
    // ②推送通道逐态节流 trace（同文一次，换态重发）——下轮实机据此定位真实失败点。
    private static int _fires;                 // postfix 触发计数（gates 之后；首次触发打全量现场）
    private static bool _firstTraced;
    private static string _lastPushTrace;      // 推送态节流（FireDevicePatch.WarnOnce 同款）

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
            _fires++;
            var hit = DeviceXpRuntime.TryGetLevelInfo(__instance, out var lv, out var xp, out var next);
            if (!_firstTraced)
            {
                _firstTraced = true;
                // F5 取证①（一次性）：槽/格键/账本键字符串/showId 全量现场（键格式应如 "14,-22"，
                // 与 Award 侧同源 CellKeyOf——若实机此行显示 MISS 即键口径漂移的直证）
                var cell = DeviceXpRuntime.CellKeyOf(__instance);
                var key = DeviceXpRuntime.SlotKey + "|" + __instance.AgentConfigId + "|" + cell;
                EndlessGuardPlugin.Trace("[XP] hover#" + _fires + " fired cfg=" + __instance.AgentConfigId
                                         + " slot=" + DeviceXpRuntime.SlotKey + " cell=" + cell
                                         + " ledger=" + (hit ? "hit" : "MISS") + "(key=\"" + key + "\")"
                                         + " lv=" + lv + " xp=" + xp + " showId=" + __instance.ShowInstanceId);
            }
            if (!hit) return;
            if (!(lv > 1 || xp > 0)) return;   // F5(i)：有账目且（已升级 或 有经验）才显示；Lv1 零经验=官方原名零污染

            var name = BaseNameOf(__instance.AgentConfigId, broken);
            if (string.IsNullOrEmpty(name)) return;   // 配置/本地化不可达=不动（与官方显示一致，宁缺毋炸）
            var text = next < 0
                ? $"{name} Lv.{lv} MAX"
                : $"{name} Lv.{lv} · 升级 {xp}/{next}";   // F5(ii)：brief 逐字格式「名称 Lv.N · 升级 x/y」（核对无误）
            TracePushState(PushHoverName(__instance, text));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[XP] hover postfix: " + e.Message); }
    }

    /// <summary>F5 取证②：推送通道逐态节流 trace（同文一次、换文重发）——"pushed" 只见一次，
    /// world/model 缺失或推送异常各自露脸一次并带现场值。</summary>
    private static void TracePushState(string state)
    {
        if (string.IsNullOrEmpty(state)) return;
        var line = "[XP] hover push: " + state;
        if (string.Equals(line, _lastPushTrace, StringComparison.Ordinal)) return;
        _lastPushTrace = line;
        EndlessGuardPlugin.Trace(line);
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
    /// 世界未建/模型未就绪=静默跳过（本帧官方原名，下次悬停再试）。F5：返状态串供取证
    ///（"pushed"/各级缺失现场/异常文本——异常在此捕获转文，postfix 大 catch 只兜意外）。</summary>
    private static string PushHoverName(Furniture f, string text)
    {
        if (!GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.IsInstanceCreated)
            return "world 未建（本帧跳过）";
        var mgr = GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.Instance?._AgentModelManager;
        if (mgr == null) return "mgr null";
        var model = mgr.GetAgentModel(f.ShowInstanceId);
        if (model == null) return "model null showId=" + f.ShowInstanceId;
        try
        {
            model.Furniture_SetDisplayName(text);
            return "pushed";
        }
        catch (Exception e) { return "push 异常: " + e.Message; }
    }
}
