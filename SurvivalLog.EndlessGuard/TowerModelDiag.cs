using System;
using GameCore.HotUpdate;                    // Furniture / GameLayer
using GameCore.HotUpdate.Battle.Logic;       // BattleLogicWorld / TransformComponent
using GameCore.HotUpdate.Battle.Show;        // BattleShowWorld / AgentModelManager
using Il2CppInterop.Runtime;                 // Il2CppType
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔选取问题一次性取证（fixwave3 W3A Step-1，作者 trace 迭代材料）：
/// 场景内出现官方受追踪设备（AgentConfigId 基础行 ∈ {36001..36004}，偏好 36004 电锯）时，把它的
/// Unity 模型层级逐节点 trace 一遍（3 层深）：路径/名/层/tag/激活态/组件清单（Collider 附类型+尺寸+
/// 中心），同口径再 dump 我方供给的塔实例（ArrowTowerModel.NoteSuppliedTower 登记）——两相对照即
/// "官方模型带了什么我方没带"。节流：官方设备 ONE 台 ONCE per session；我方塔 ONCE per session；
/// 探测期每 1s 一轮，双 dump 完成后零开销。另有注册探针：把我方塔每个 Collider 的 instanceID 查
/// AgentModelManager.GetModelByColliderId（dump.cs TypeDefIndex 4794，RVA 0x2F16D70——官方选取管线
/// 的 colliderId→AgentModel 索引），核验"射线命中→选中"链路在我方塔上是否闭合。
/// 行格式（brief 定）：`[Tower][diag] 36004/&lt;path&gt; layer=L tag=T active=1 comps=A,B,C col=Box(w,h,d@cx,cy,cz)`。
/// 全程只读（dump 不写任何场景态）；每行 try/catch，总失败一次性熔断防刷屏。
/// </summary>
internal static class TowerModelDiag
{
    private const int MaxDepth = 3;        // brief：3 层深
    private const int MaxLines = 60;       // 单次 dump 行数上限（trace 防刷屏）
    private const int MaxChildren = 12;    // 每节点子数上限（超出打 …+N more）

    private static bool _officialDone, _oursDone, _failTraced, _regDone;
    private static float _acc;
    private static int _lines;

    /// <summary>我方最近一次供给的塔实例根（Supply 成功时登记；场景切换销毁后 Unity null=跳过）。</summary>
    private static Transform _ours;

    internal static void NoteSuppliedTower(Transform t)
    {
        if (t != null) _ours = t;
    }

    /// <summary>逐帧入口（ArrowTowerFx.Update 调；完成态一次布尔比较即出）。</summary>
    public static void Update(float dt)
    {
        try
        {
            if (_officialDone && _oursDone) return;
            if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;
            _acc += dt;
            if (_acc < 1f) return;
            _acc = 0f;
            if (!_officialDone) TryDumpOfficial();
            if (!_oursDone) TryDumpOurs();
        }
        catch (Exception e)
        {
            EndlessGuardPlugin.Trace("[Tower][diag] update: " + e.Message);
            _officialDone = _oursDone = true;   // 熔断：本会话不再试（取证是尽力而为，不拖累塔本体）
        }
    }

    // ── 官方设备 dump ──────────────────────────────────────────────────────────────

    private static void TryDumpOfficial()
    {
        if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return;
        Il2CppSystem.Collections.Generic.List<BaseAgent> all = null;
        try { all = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent(); }
        catch { return; }
        if (all == null) return;

        Furniture best = null;
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
            var cfg = f.AgentConfigId;
            if (cfg < 36001 || cfg > 36004) continue;          // 官方基础行设备（brief 指定集合）
            if (best == null || cfg > best.AgentConfigId) best = f;   // 偏好 36004（电锯=官方可交互参照）
        }
        if (best == null)
        {
            if (!_failTraced)
            {
                _failTraced = true;
                EndlessGuardPlugin.Trace("[Tower][diag] waiting official tracked device (36001..36004) in scene…");
            }
            return;
        }
        _officialDone = true;
        try
        {
            var t = RootTransformOf(best);
            EndlessGuardPlugin.Trace("[Tower][diag] official cfg=" + best.AgentConfigId
                                     + " GameLayer.Furniture=" + SafeFurnitureLayer()
                                     + " found=" + (t != null));
            if (t != null) DumpTree(t, best.AgentConfigId.ToString());
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower][diag] official: " + e.Message); }
    }

    // ── 我方塔 dump + 注册探针 ─────────────────────────────────────────────────────

    private static void TryDumpOurs()
    {
        if (_ours == null) return;                             // 本会话尚无供给塔（放置后下轮再 dump）
        var alive = false;
        try { alive = _ours.gameObject != null && _ours.gameObject.activeInHierarchy; } catch { }
        if (!alive) { _oursDone = true; return; }              // 已销毁（换场景）=不空转
        _oursDone = true;
        try
        {
            EndlessGuardPlugin.Trace("[Tower][diag] ours instanceID=" + _ours.gameObject.GetInstanceID()
                                     + " layer=" + _ours.gameObject.layer + " (mirror target="
                                     + SafeFurnitureLayer() + ")");
            DumpTree(_ours, ArrowTower.ResName);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower][diag] ours: " + e.Message); }
        TryRegistrationProbe();
    }

    /// <summary>注册探针（一次性）：我方塔的 Collider 是否进了官方 colliderId→AgentModel 索引。
    /// registered=false 而塔在场=AgentModel 未收编我方 Collider（选取链路断在注册，不在射线）。</summary>
    private static void TryRegistrationProbe()
    {
        if (_regDone) return;
        _regDone = true;
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.IsInstanceCreated) return;
            var amm = GameCore.HotUpdate.BaseSingleton<BattleShowWorld>.Instance?._AgentModelManager;
            if (amm == null) return;
            var cols = _ours.GetComponentsInChildren<Collider>();
            int found = 0, registered = 0;
            foreach (var c in cols)
            {
                if (c == null) continue;
                found++;
                var m = amm.GetModelByColliderId(c.GetInstanceID());
                if (m != null) registered++;
                if (found <= 3)
                    EndlessGuardPlugin.Trace("[Tower][diag-reg] collider id=" + c.GetInstanceID()
                                             + " type=" + c.GetIl2CppType().Name
                                             + " registered=" + (m != null)
                                             + (m != null ? " modelCfg=" + m.configId : ""));
            }
            EndlessGuardPlugin.Trace("[Tower][diag-reg] colliders=" + found + " registered=" + registered
                                     + (registered == 0 ? " ← 未注册：选中链断在 AgentModel 收编前" : ""));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower][diag-reg] probe: " + e.Message); }
    }

    // ── 层级 dump（3 层深）────────────────────────────────────────────────────────

    private static void DumpTree(Transform root, string prefix)
    {
        _lines = 0;
        DumpNode(root, prefix, 0);
    }

    private static void DumpNode(Transform t, string path, int depth)
    {
        if (t == null || _lines >= MaxLines) return;
        _lines++;
        try
        {
            var go = t.gameObject;
            var sb = new System.Text.StringBuilder();
            sb.Append("[Tower][diag] ").Append(path)
              .Append(" layer=").Append(go.layer)
              .Append(" tag=").Append(go.tag)
              .Append(" active=").Append(go.activeSelf ? 1 : 0)
              .Append(" comps=");
            var comps = go.GetComponents<Component>();
            var colDesc = "";
            var n = 0;
            foreach (var c in comps)
            {
                if (c == null) { sb.Append("null"); continue; }
                if (n++ > 0) sb.Append(",");
                sb.Append(SafeTypeName(c));
                var cd = ColliderDesc(c);
                if (cd != null) colDesc = " col=" + cd;
            }
            if (!string.IsNullOrEmpty(colDesc)) sb.Append(colDesc);
            if (go.GetComponent<Camera>() != null) sb.Append(" [Camera]");
            EndlessGuardPlugin.Trace(sb.ToString());
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower][diag] node " + path + ": " + e.Message); }

        if (depth >= MaxDepth) return;
        var kids = t.childCount;
        var shown = 0;
        for (int i = 0; i < kids; i++)
        {
            var c = t.GetChild(i);
            if (c == null) continue;
            if (shown >= MaxChildren)
            {
                EndlessGuardPlugin.Trace("[Tower][diag] " + path + " …+" + (kids - shown) + " more children");
                return;
            }
            shown++;
            DumpNode(c, path + "/" + SafeName(c), depth + 1);
        }
    }

    /// <summary>Collider 描述（brief 格式 Box(w,h,d@cx,cy,cz)；非 Collider=null）。</summary>
    private static string ColliderDesc(Component c)
    {
        try
        {
            var b = c.TryCast<BoxCollider>();
            if (b != null)
                return "Box(" + F(b.size.x) + "," + F(b.size.y) + "," + F(b.size.z)
                       + "@" + F(b.center.x) + "," + F(b.center.y) + "," + F(b.center.z) + ")"
                       + (b.enabled ? "" : "!en") + (b.isTrigger ? "!trg" : "");
            var s = c.TryCast<SphereCollider>();
            if (s != null)
                return "Sphere(r=" + F(s.radius) + "@" + F(s.center.x) + "," + F(s.center.y) + "," + F(s.center.z) + ")"
                       + (s.enabled ? "" : "!en");
            var cp = c.TryCast<CapsuleCollider>();
            if (cp != null)
                return "Capsule(r=" + F(cp.radius) + ",h=" + F(cp.height) + ",ax=" + cp.direction + ")"
                       + (cp.enabled ? "" : "!en");
            var mc = c.TryCast<MeshCollider>();
            if (mc != null) return "Mesh" + (mc.enabled ? "" : "!en");
            return null;
        }
        catch { return null; }
    }

    // ── 解析助手（interop 摩擦兜底，DeviceXpRuntime 同款）─────────────────────────

    /// <summary>受追踪家具的 Unity 根（TransformComponent.transform，ArrowTowerFx.TryResolve 已验证）。</summary>
    private static Transform RootTransformOf(Furniture f)
    {
        try
        {
            var dict = f != null ? f.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppType.From(typeof(TransformComponent)), out var raw)
                ? raw?.TryCast<TransformComponent>()?.transform : null;
        }
        catch { return null; }
    }

    /// <summary>GameLayer.Furniture（dump.cs TypeDefIndex 553 静态层表；不可读=-1）。</summary>
    internal static int SafeFurnitureLayer()
    {
        try { var l = GameLayer.Furniture; return l >= 0 && l <= 31 ? l : -1; }
        catch { return -1; }
    }

    private static string SafeName(Transform t)
    {
        try { return t.name; } catch { return "?"; }
    }

    private static string SafeTypeName(Component c)
    {
        try { return c.GetIl2CppType().Name; } catch { return "?"; }
    }

    private static string F(float v) => v.ToString("0.##");
}
