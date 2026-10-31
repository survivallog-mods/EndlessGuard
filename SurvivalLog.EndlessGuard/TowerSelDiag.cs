using System;
using System.Collections.Generic;
using System.Reflection;                           // MethodBase（TargetMethod 返回类型）
using GameCore.HotUpdate;                          // BaseSingleton
using GameCore.HotUpdate.Battle.Logic;             // BattleLogicWorld / Furniture
using GameCore.HotUpdate.Battle.Show;              // AgentModelManager / AgentModel / MouseRaycastService
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray（GetMouseHits 的 out 数组实型）
using UnityEngine;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔选取链路只读探针（diag-only，2026-10-31）：布局-防御模式里官方设备（射线→模型→家具→槽位面板）
/// 可选中而我方供给的箭塔不可选中——同一管线上分段埋点，控制器实测官方设备 vs 箭塔各点一遍，
/// 从 trace diff 出分叉段。零行为改动：全部 postfix/prefix 只读，不写任何游戏对象/状态。
///
/// 四组探针（行前缀统一 [SEL]，输出 BepInEx/EndlessGuard_trace.txt）：
/// 1. OnSelectFurnitureShow postfix（AgentModelManager，私有实例方法，dump.cs:353447）——每次家具
///    选中事件到达管理器即打一行，并顺带只读回查 GetAgentModel(showId)（纯字典查）验证
///    showId→模型解析是否成立。点电锯有行、点箭塔无行=分叉在【上游】（射线/注册/层过滤）；
///    点箭塔也有行=分叉在【下游】（模型→家具→槽位面板解析）。
/// 2. GetMouseHits postfix（MouseRaycastService，静态，dump.cs:363731）——物理射线命中数与前三命中
///    （collider 名@层/距离/映射 modelCfg）。节流：命中数与上次相同即跳 + 4/s 上限。看箭塔的
///    BoxCollider 是否真的被射线打到、打到后映射出的 modelCfg 是多少。
/// 3. 场景载入对比 dump（每场景一次，两者都在场后触发）：官方受追踪设备（AgentConfigId 基础行
///    ∈{36001..36004}）与我方箭塔（DeviceXpMath.BaseCfgOf(cfg)==99005）各取一台，经
///    AgentModelManager.GetAgentModel（showId / instanceId / logicId 三键只读试解）取 AgentModel，
///    dump 可读公开字段（configId/logicInstanceId/Type/FloorId/_colliderId/_collider/Obj/各隐藏位）
///    + 模型根名/层/子数/各 Collider 注册态，两相对照。其中一方 GetAgentModel=null 即答案本身。
///    泵=AgentModelManager.OnUpdate postfix（每帧常规方法，非 async 状态机，兵律安全）；完成态
///    一次引用比较即出，换场景（管理器实例变化）自动重开一轮。
/// 4. RefreshAgentColliderByFloor prefix/postfix（dump.cs:2F17D20）——管理器的碰撞体分层刷新对
///    「有趣模型」（Obj 名含 Tripo 或 cfg∈受追踪设备集）记录 collider enabled 前→后；若我方塔的
///    碰撞体被楼层过滤禁用（enabled true→false），射线层掩码内也打不到=选中链断点实锤。
///    同一行内容 2s 去重 + 4/s 上限。
///
/// 签名口径（interop 反射实证，非 dump 推测）：GetMouseHits(out Il2CppStructArray&lt;RaycastHit&gt; hits)→int
///（postfix 侧以 ref 声明同名参数绑定——只读注入值，绝不回写：回写会篡改官方调用方拿到的 out 实参）；
/// OnSelectFurnitureShow(Int64,Int64) 私有实例；RefreshAgentColliderByFloor(AgentModel) 公开实例；
/// OnUpdate(Single,Single) 公开实例。AgentModel 可读公开成员：logicInstanceId/configId/IsBagModel/
/// _colliderId/_collider/Obj/Type/FloorId/IsFloorExempt/HiddenByCamera/ColliderHiddenByDock/
/// ModelState/IsModelContainerHidden。成员不可达（interop 漂移）→ 打
/// `[SEL] diag member unreachable: &lt;name&gt;`（每名一次）后继续，绝不抛出。
/// </summary>
internal static class TowerSelDiag
{
    // ── 1. OnSelectFurnitureShow ─────────────────────────────────────────────────

    [HarmonyPatch]
    private sealed class OnSelectFurnitureShowHook
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AgentModelManager), "OnSelectFurnitureShow");

        private static void Postfix(AgentModelManager __instance, long showId, long triggerLogicId) =>
            TowerSelDiag.OnSelectFurnitureShow(__instance, showId, triggerLogicId);
    }

    // ── 2. GetMouseHits ──────────────────────────────────────────────────────────

    [HarmonyPatch]
    private sealed class GetMouseHitsHook
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(MouseRaycastService), "GetMouseHits");

        private static void Postfix(int __result, ref Il2CppStructArray<RaycastHit> hits) =>
            TowerSelDiag.OnMouseHits(__result, hits);
    }

    // ── 3. 场景对比 dump 泵（AgentModelManager.OnUpdate postfix）─────────────────

    [HarmonyPatch]
    private sealed class ModelComparePumpHook
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AgentModelManager), "OnUpdate");

        private static void Postfix(AgentModelManager __instance) =>
            TowerSelDiag.Pump(__instance);
    }

    // ── 4. RefreshAgentColliderByFloor ───────────────────────────────────────────

    [HarmonyPatch]
    private sealed class RefreshAgentColliderByFloorHook
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AgentModelManager), "RefreshAgentColliderByFloor");

        private static void Prefix(AgentModel model) => TowerSelDiag.RefreshPre(model);
        private static void Postfix(AgentModelManager __instance, AgentModel model) =>
            TowerSelDiag.RefreshPost(__instance, model);
    }

    // ══════════════════════════════ 实现与节流状态 ══════════════════════════════

    /// <summary>每名一次的「成员不可达」记录（防刷屏）。</summary>
    private static readonly HashSet<string> Unreachable = new();

    private static bool Gate() =>
        EndlessGuardPlugin.Enabled == null || EndlessGuardPlugin.Enabled.Value;

    /// <summary>成员读取统一兜底：读失败=记一次 unreachable 后返 default，绝不抛。</summary>
    private static T Get<T>(Func<T> read, string memberName)
    {
        try { return read(); }
        catch (Exception e)
        {
            if (Unreachable.Add(memberName))
                EndlessGuardPlugin.Trace("[SEL] diag member unreachable: " + memberName + " (" + e.GetType().Name + ")");
            return default;
        }
    }

    // ── 1. 选中事件（用户点击频度，仅连续重复去重）────────────────────────────────

    private static long _lastSelShow, _lastSelTrig;

    private static void OnSelectFurnitureShow(AgentModelManager amm, long showId, long trig)
    {
        try
        {
            if (!Gate()) return;
            if (showId == _lastSelShow && trig == _lastSelTrig) return;   // 连续同键重复=同一事件重放
            _lastSelShow = showId; _lastSelTrig = trig;
            var m = Get(() => amm != null ? amm.GetAgentModel(showId) : null, "AgentModelManager.GetAgentModel");
            var cfg = m != null ? Get(() => m.configId, "AgentModel.configId") : -1;
            var objName = m != null ? SafeObjName(m) : "-";
            EndlessGuardPlugin.Trace("[SEL] OnSelectFurnitureShow showId=" + showId + " trig=" + trig
                                     + " modelFound=" + (m != null)
                                     + (m != null ? " cfg=" + cfg + " obj=" + objName : ""));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL] selectShow: " + e.Message); }
    }

    // ── 2. 物理射线（命中数不变跳 + 4/s）─────────────────────────────────────────

    private static int _lastHits = int.MinValue;
    private static float _lastHitsTrace;

    private static void OnMouseHits(int n, Il2CppStructArray<RaycastHit> hits)
    {
        try
        {
            if (!Gate()) return;
            if (n == _lastHits) return;                            // 口径：命中数不变=画面未动，跳
            _lastHits = n;
            var now = Time.unscaledTime;
            if (now - _lastHitsTrace < 0.25f) return;              // 4/s 上限（突发防护）
            _lastHitsTrace = now;

            var amm = LiveAmm();
            var sb = new System.Text.StringBuilder("[SEL] hits=" + n);
            for (int i = 0; i < n && i < 3; i++)
            {
                var h = Get(() => hits[i], "RaycastHit[" + i + "]");
                var col = Get(() => h.collider, "RaycastHit.collider");
                if (col == null) { sb.Append(" i" + i + "=<null col>"); continue; }
                var mcfg = -1;
                if (amm != null)
                {
                    var m = Get(() => amm.GetModelByColliderId(Get(() => col.GetInstanceID(), "Collider.GetInstanceID")),
                                "AgentModelManager.GetModelByColliderId");
                    if (m != null) mcfg = Get(() => m.configId, "AgentModel.configId");
                }
                sb.Append(" i" + i + "=" + Get(() => col.name, "Collider.name")
                          + "@" + Get(() => col.gameObject.layer, "GameObject.layer")
                          + " d=" + Get(() => h.distance, "RaycastHit.distance").ToString("0.##")
                          + " mcfg=" + (mcfg >= 0 ? mcfg.ToString() : "-"));
            }
            EndlessGuardPlugin.Trace(sb.ToString());
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL] hits: " + e.Message); }
    }

    // ── 3. 场景载入对比 dump ─────────────────────────────────────────────────────

    private static AgentModelManager _seenAmm;   // 换实例（换场景）=重开一轮
    private static bool _cmpDone, _waitTraced, _pumpAttached;
    private static float _acc;

    private static AgentModelManager LiveAmm()
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.IsInstanceCreated) return null;
            return GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.Instance?._AgentModelManager;
        }
        catch { return null; }
    }

    private static void Pump(AgentModelManager amm)
    {
        try
        {
            if (!Gate()) return;
            if (!_pumpAttached)
            {
                _pumpAttached = true;
                EndlessGuardPlugin.Trace("[SEL] pump attached (TowerSelDiag live: selectShow/hits/cmp/refresh)");
            }
            if (_cmpDone && ReferenceEquals(amm, _seenAmm)) return;    // 稳态一次引用比较
            if (!ReferenceEquals(amm, _seenAmm)) { _seenAmm = amm; _cmpDone = false; _waitTraced = false; }
            if (amm == null) return;
            _acc += Time.unscaledDeltaTime;
            if (_acc < 1f) return;                                     // 每场景一次的对比按 1s 步进试
            _acc = 0f;
            TryCompare(amm);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL] pump: " + e.Message); }
    }

    private static void TryCompare(AgentModelManager amm)
    {
        Furniture official = null, tower = null;
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return;
            var all = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent();
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                Furniture f = null;
                try
                {
                    var a = all[i];
                    if (a == null || a.IsDead) continue;
                    f = a.TryCast<Furniture>();
                }
                catch { continue; }
                if (f == null) continue;
                var cfg = Get(() => f.AgentConfigId, "Furniture.AgentConfigId");
                var baseCfg = DeviceXpMath.BaseCfgOf(cfg);
                if (baseCfg >= 36001 && baseCfg <= 36004) official = official ?? f;
                else if (baseCfg == 99005) tower = tower ?? f;
                if (official != null && tower != null) break;
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL][cmp] scan: " + e.Message); return; }

        if (official == null || tower == null)
        {
            if (!_waitTraced)
            {
                _waitTraced = true;
                EndlessGuardPlugin.Trace("[SEL][cmp] waiting: official=" + (official != null)
                                         + " tower=" + (tower != null) + "（两者在场后出对比 dump）");
            }
            return;
        }
        _cmpDone = true;
        EndlessGuardPlugin.Trace("[SEL][cmp] === 模型对比（官方设备 vs 我方箭塔）viewFloor="
                                 + Get(() => amm.CurrentViewFloor, "AgentModelManager.CurrentViewFloor") + " ===");
        DumpModelOf(amm, official, "official");
        DumpModelOf(amm, tower, "tower");
    }

    /// <summary>单台家具→AgentModel 解析与可读字段 dump。model=NULL 一行即答案本身。</summary>
    private static void DumpModelOf(AgentModelManager amm, Furniture f, string tag)
    {
        try
        {
            var cfg = Get(() => f.AgentConfigId, "Furniture.AgentConfigId");
            var showId = Get(() => f.ShowInstanceId, "Furniture.ShowInstanceId");
            var instId = Get(() => f.InstanceId, "Furniture.InstanceId");

            var m = Get(() => amm.GetAgentModel(showId), "AgentModelManager.GetAgentModel");
            var key = "showId";
            if (m == null) { m = Get(() => amm.GetAgentModel(instId), "AgentModelManager.GetAgentModel"); key = "instId"; }
            if (m == null)
            {
                m = Get(() => amm.GetAgentModelByLogicInstanceId(instId), "AgentModelManager.GetAgentModelByLogicInstanceId");
                key = "logicId";
            }

            EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " cfg=" + cfg + " showId=" + showId
                                     + " instId=" + instId + " model=" + (m != null ? "found" : "NULL")
                                     + (m != null ? " key=" + key : "（三键全 null=模型不在管理器缓存）"));
            if (m == null) return;

            var cfgM = Get(() => m.configId, "AgentModel.configId");
            var logic = Get(() => m.logicInstanceId, "AgentModel.logicInstanceId");
            var type = Get(() => m.Type, "AgentModel.Type");
            var floor = Get(() => m.FloorId, "AgentModel.FloorId");
            var colId = Get(() => m._colliderId, "AgentModel._colliderId");
            var col = Get(() => m._collider, "AgentModel._collider");
            var bag = Get(() => m.IsBagModel, "AgentModel.IsBagModel");
            var exempt = Get(() => m.IsFloorExempt, "AgentModel.IsFloorExempt");
            var hidCam = Get(() => m.HiddenByCamera, "AgentModel.HiddenByCamera");
            var hidDock = Get(() => m.ColliderHiddenByDock, "AgentModel.ColliderHiddenByDock");
            var hidContainer = Get(() => m.IsModelContainerHidden, "AgentModel.IsModelContainerHidden");

            EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " model: cfg=" + cfgM + " logic=" + logic
                                     + " type=" + type + " floor=" + floor + " viewFloor="
                                     + Get(() => amm.CurrentViewFloor, "AgentModelManager.CurrentViewFloor")
                                     + " colliderId=" + colId
                                     + " collider=" + (col != null
                                         ? Get(() => col.name, "Collider.name") + "@"
                                           + Get(() => col.gameObject.layer, "GameObject.layer")
                                           + " en=" + Get(() => col.enabled, "Collider.enabled")
                                         : "null")
                                     + " bag=" + bag + " floorExempt=" + exempt
                                     + " hiddenCam=" + hidCam + " hiddenDock=" + hidDock
                                     + " containerHidden=" + hidContainer);

            var obj = Get(() => m.Obj, "AgentModel.Obj");
            if (obj == null) { EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " obj=NULL"); return; }
            var rootName = Get(() => obj.name, "GameObject.name");
            var rootLayer = Get(() => obj.layer, "GameObject.layer");
            var kids = Get(() => obj.transform.childCount, "Transform.childCount");
            EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " obj=" + rootName + " layer=" + rootLayer
                                     + " childCount=" + kids + " active=" + Get(() => obj.activeSelf, "GameObject.activeSelf"));
            DumpColliders(amm, tag, obj);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + ": " + e.Message); }
    }

    /// <summary>模型全部 Collider 的注册态对照（射线→模型映射逐个核对；含 enabled/trigger/isTrigger）。</summary>
    private static void DumpColliders(AgentModelManager amm, string tag, GameObject obj)
    {
        try
        {
            var cols = obj.GetComponentsInChildren<Collider>(true);
            int n = 0;
            foreach (var c in cols)
            {
                if (c == null || n >= 6) { if (c != null) n++; continue; }
                n++;
                var m = Get(() => amm.GetModelByColliderId(Get(() => c.GetInstanceID(), "Collider.GetInstanceID")),
                            "AgentModelManager.GetModelByColliderId");
                EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " col=" + Get(() => c.name, "Collider.name")
                                         + "@" + Get(() => c.gameObject.layer, "GameObject.layer")
                                         + " en=" + Get(() => c.enabled, "Collider.enabled")
                                         + " trg=" + Get(() => c.isTrigger, "Collider.isTrigger")
                                         + " reg=" + (m != null)
                                         + (m != null ? "→cfg=" + Get(() => m.configId, "AgentModel.configId") : ""));
            }
            if (n > 6) EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " cols …+" + (n - 6) + " more");
            if (n == 0) EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " cols=NONE（模型树无任何 Collider）");
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL][cmp] " + tag + " cols: " + e.Message); }
    }

    // ── 4. RefreshAgentColliderByFloor（有趣模型 only + 同行 2s 去重 + 4/s）──────

    private static Collider _preCol;
    private static bool _preEn;
    private static string _lastRefreshLine;
    private static float _lastRefreshTrace;

    private static bool Interesting(AgentModel m)
    {
        var cfg = Get(() => m.configId, "AgentModel.configId");
        if (DeviceXpMath.IsTrackedDevice(cfg) || DeviceXpMath.BaseCfgOf(cfg) == 99005) return true;
        var n = SafeObjName(m);
        return n != null && n.IndexOf("Tripo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void RefreshPre(AgentModel model)
    {
        try
        {
            if (!Gate() || model == null || !Interesting(model)) { _preCol = null; return; }
            _preCol = Get(() => model._collider, "AgentModel._collider");
            _preEn = _preCol != null && Get(() => _preCol.enabled, "Collider.enabled");
        }
        catch { _preCol = null; }
    }

    private static void RefreshPost(AgentModelManager amm, AgentModel model)
    {
        try
        {
            if (!Gate() || model == null || _preCol == null) return;
            var col = Get(() => model._collider, "AgentModel._collider");
            var postEn = col != null && Get(() => col.enabled, "Collider.enabled");
            var cfg = Get(() => model.configId, "AgentModel.configId");
            var line = "[SEL] RefreshCollider cfg=" + cfg
                       + " obj=" + SafeObjName(model)
                       + " floor=" + Get(() => model.FloorId, "AgentModel.FloorId")
                       + " view=" + (amm != null ? Get(() => amm.CurrentViewFloor, "AgentModelManager.CurrentViewFloor") : -1)
                       + " col=" + (col != null
                           ? Get(() => col.name, "Collider.name") + "@"
                             + Get(() => col.gameObject.layer, "GameObject.layer")
                           : "null")
                       + " enabled " + (_preEn ? 1 : 0) + "→" + (postEn ? 1 : 0)
                       + (postEn ? "" : " ←射线将打不到（若持续）");
            if (line == _lastRefreshLine && Time.unscaledTime - _lastRefreshTrace < 2f) return;   // 同行 2s 去重
            if (Time.unscaledTime - _lastRefreshTrace < 0.25f) return;                            // 4/s 上限
            _lastRefreshLine = line; _lastRefreshTrace = Time.unscaledTime;
            EndlessGuardPlugin.Trace(line);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[SEL] refresh: " + e.Message); }
        finally { _preCol = null; }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────────

    private static string SafeObjName(AgentModel m)
    {
        var obj = Get(() => m.Obj, "AgentModel.Obj");
        return obj != null ? Get(() => obj.name, "GameObject.name") : null;
    }
}
