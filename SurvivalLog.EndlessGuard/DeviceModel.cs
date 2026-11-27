using System;
using GameCore.Scripts;                   // ShowState/ModelAnimator（官方模型根契约类型）
using UnityEngine.AI;                     // NavMeshObstacle
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 设备模型的官方根契约——选取、拆除、搬动、耐久界面、修理、藤蔓的公共前提（逻辑与数值从箭塔 fixwave2~7 原样抽出）。
/// 官方设备模型根 = Transform + BoxCollider + NavMeshObstacle + ModelAnimator + ShowState，根下有
/// State01~04/StateBroken 状态组与 HeadBar/CenterPoint 锚点，全链处于 GameLayer.Furniture 层。
/// 官方选取管线：MouseRaycastService.GetMouseHits（层掩码）→ Collider → AgentModelManager.GetModelByColliderId
/// → AgentModel → 家具菜单；耐久 FSM 与悬停头栏读 AgentModel.ModelState/Animator。
/// 缺层=射线打不到；缺 ShowState/ModelAnimator/锚点=碰撞体已注册而仍不可选、没有悬停耐久。
/// 任何 mod 设备模型供给后都要调一次 Finalize——漏了就是「模型在、能开火，但点不选、修不了、僵尸不咬」。
/// </summary>
internal static class DeviceModel
{
    /// <summary>设备外形参数（模型局部单位，随实例缩放）。</summary>
    internal readonly struct Shape
    {
        /// <param name="boxSize">根罩体碰撞体尺寸（官方设备模型同款「模型根带 Collider」）</param>
        /// <param name="boxCenter">罩体中心</param>
        /// <param name="headBarY">悬停头栏锚点高度（塔顶上方，名字与血条的世界投影源）</param>
        /// <param name="centerY">中心锚点高度（塔身中部）</param>
        internal Shape(Vector3 boxSize, Vector3 boxCenter, float headBarY, float centerY)
        {
            BoxSize = boxSize;
            BoxCenter = boxCenter;
            HeadBarY = headBarY;
            CenterY = centerY;
        }

        internal readonly Vector3 BoxSize;
        internal readonly Vector3 BoxCenter;
        internal readonly float HeadBarY;
        internal readonly float CenterY;
    }

    private static readonly string[] StateGroups = { "State01", "State02", "State03", "State04", "StateBroken" };

    /// <summary>供给实例收口：状态组 → 罩体碰撞体 → 根组件与锚点 → 全链清扫（剥子件碰撞体 + 层镜像）。
    /// 返回契约摘要（trace 用），added=true 表示本次补了件。要在挂特效之前调用：ModelAnimator.Renderers 取的是调用当时激活的渲染器清单。</summary>
    internal static string Finalize(GameObject inst, in Shape shape, string tag, out bool added)
    {
        var contract = EnsureContract(inst, shape, out added);
        Sweep(inst, tag, out _);
        return contract;
    }

    /// <summary>确保 root 具备官方模型根契约，幂等；返回摘要串，added=true 表示本次实际补了件（修复路径，trace 用）。
    /// 单件隔离：一件失败不拖垮其余。调用点=模板建成时（克隆继承）与每次供给。</summary>
    internal static string EnsureContract(GameObject root, in Shape shape, out bool added)
    {
        added = false;
        if (root == null)
            return "ShowState=fail:null ModelAnimator=fail:null NavMeshObstacle=fail:null HeadBar/CenterPoint=fail:null";

        try { EnsureStateGroups(root); } catch (Exception e) { EndlessGuardPlugin.Trace("[Device] state groups: " + e.Message); }

        string ss, ma, nmo, anch;
        bool a1, a2, a3, a4;
        try { ss = EnsureShowState(root, out a1); } catch (Exception e) { ss = "fail:" + e.Message; a1 = false; }
        try { anch = EnsureAnchors(root, shape, out a4); } catch (Exception e) { anch = "fail:" + e.Message; a4 = false; }
        try { ma = EnsureModelAnimator(root, shape, out a2); } catch (Exception e) { ma = "fail:" + e.Message; a2 = false; }
        try { nmo = EnsureNavObstacle(root, shape, out a3); } catch (Exception e) { nmo = "fail:" + e.Message; a3 = false; }
        try { EnsureBoxCollider(root, shape); } catch (Exception e) { EndlessGuardPlugin.Trace("[Device] collider: " + e.Message); }
        added = a1 || a2 || a3 || a4;
        return "ShowState=" + ss + " ModelAnimator=" + ma + " NavMeshObstacle=" + nmo
             + " HeadBar/CenterPoint=" + anch;
    }

    /// <summary>全层级清扫（幂等）：遍历 root 全部 Transform（含失活状态组），①非根子件上的 Collider 一律销毁
    ///（根上唯一 BoxCollider 罩体保留，那是官方的选取注册件；子件上的野碰撞体会抢射线、把点选链截断）；
    /// ②全链层镜像 GameLayer.Furniture（官方选取射线的层掩码）。返回剥掉的 Collider 数，层修复数走 out。</summary>
    internal static int Sweep(GameObject root, string tag, out int layerFixed, bool traceAlways = true)
    {
        layerFixed = 0;
        var stripped = 0;
        if (root == null) return 0;
        try
        {
            var layer = ResolveFurnitureLayer();
            var rootT = root.transform;
            var all = rootT.GetComponentsInChildren<Transform>(true);
            foreach (var tr in all)
            {
                if (tr == null) continue;
                var go = tr.gameObject;
                if (tr != rootT && !ReferenceEquals(tr, rootT))      // 根上的罩体保留；两种等价判定防 interop 包装差异
                {
                    var cols = go.GetComponents<Collider>();            // 逐件全剥（GetComponent 只回首个）
                    foreach (var c in cols)
                    {
                        if (c == null) continue;
                        Object.Destroy(c);
                        stripped++;
                    }
                }
                if (layer >= 0 && go.layer != layer)
                {
                    go.layer = layer;
                    layerFixed++;
                }
            }
            if (traceAlways || stripped > 0 || layerFixed > 0)
                EndlessGuardPlugin.Trace("[Device] sweep: stripped=" + stripped
                                         + " layerFixed=" + layerFixed + " on " + tag);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Device] sweep: " + e.Message + " on " + tag); }
        return stripped;
    }

    /// <summary>状态组（官方根直下 State01~04/StateBroken 的形状）：缺 State01 时把现有子件整体归入 State01
    ///（各子件局部变换不变），再补齐缺失的其余组（失活空组）。已有即零写。</summary>
    private static void EnsureStateGroups(GameObject root)
    {
        var rt = root.transform;
        var s1 = rt.Find(StateGroups[0]);
        if (s1 == null)
        {
            s1 = new GameObject(StateGroups[0]).transform;
            s1.SetParent(rt, false);
            for (var i = rt.childCount - 1; i >= 0; i--)
            {
                var c = rt.GetChild(i);
                if (c != s1) c.SetParent(s1, false);
            }
        }
        for (var i = 1; i < StateGroups.Length; i++)
        {
            if (rt.Find(StateGroups[i]) != null) continue;
            var go = new GameObject(StateGroups[i]);
            go.transform.SetParent(rt, false);
            go.SetActive(false);
        }
    }

    /// <summary>罩体碰撞体：缺则按外形补（正常路径零写）。</summary>
    private static void EnsureBoxCollider(GameObject root, in Shape shape)
    {
        if (root.GetComponent<BoxCollider>() != null) return;
        var col = root.AddComponent<BoxCollider>();
        col.size = shape.BoxSize;
        col.center = shape.BoxCenter;
    }

    /// <summary>ShowState：缺则 AddComponent（原生类型即建即活）；空桩 ctor 不建表，States/SpecChilds 补非空空表
    ///（官方 ChangeState 遍历该表，null 表会 NRE；空表=状态切换落空，无伤）。</summary>
    private static string EnsureShowState(GameObject root, out bool added)
    {
        added = false;
        var t = Il2CppInterop.Runtime.Il2CppType.Of<ShowState>();
        var comp = root.GetComponent(t)?.TryCast<ShowState>();
        if (comp == null)
        {
            comp = root.AddComponent(t).TryCast<ShowState>();
            added = true;
        }
        if (comp.States == null)
            comp.States = new Il2CppSystem.Collections.Generic.List<StateChild>();
        if (comp.SpecChilds == null)
            comp.SpecChilds = new Il2CppSystem.Collections.Generic.List<SpecChild>();
        return "ok";
    }

    /// <summary>ModelAnimator：缺则 AddComponent；回填 HeadBar/CenterPoint 锚引用、Renderers 清单、Skinneds 非空空表。
    /// AnimatorPlayer/FlashLight 无源可填（外部模型无官方动画件），官方有超时兜底。</summary>
    private static string EnsureModelAnimator(GameObject root, in Shape shape, out bool added)
    {
        added = false;
        var t = Il2CppInterop.Runtime.Il2CppType.Of<ModelAnimator>();
        var comp = root.GetComponent(t)?.TryCast<ModelAnimator>();
        if (comp == null)
        {
            comp = root.AddComponent(t).TryCast<ModelAnimator>();
            added = true;
        }
        if (comp.HeadBar == null)
            comp.HeadBar = CreateOrFindAnchor(root, "HeadBar", shape.HeadBarY);
        if (comp.CenterPoint == null)
            comp.CenterPoint = CreateOrFindAnchor(root, "CenterPoint", shape.CenterY);
        if (comp.Renderers == null)
        {
            var list = new Il2CppSystem.Collections.Generic.List<MeshRenderer>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                if (r != null) list.Add(r);
            comp.Renderers = list;
        }
        if (comp.Skinneds == null)
            comp.Skinneds = new Il2CppSystem.Collections.Generic.List<SkinnedMeshRenderer>();
        return "ok(AnimatorPlayer=null)";
    }

    /// <summary>NavMeshObstacle：缺则 AddComponent，按罩体同尺寸预配置；运行时开合归官方
    /// AgentModel.ApplyTowerDeviceNavObstacle 接管。已在则零写。</summary>
    private static string EnsureNavObstacle(GameObject root, in Shape shape, out bool added)
    {
        added = false;
        var t = Il2CppInterop.Runtime.Il2CppType.Of<NavMeshObstacle>();
        var comp = root.GetComponent(t)?.TryCast<NavMeshObstacle>();
        if (comp != null) return "ok";
        comp = root.AddComponent(t).TryCast<NavMeshObstacle>();
        added = true;
        comp.shape = NavMeshObstacleShape.Box;
        comp.size = shape.BoxSize;
        comp.center = shape.BoxCenter;
        comp.carving = false;
        return "ok";
    }

    /// <summary>锚点子节点：按名查（幂等），缺则建空 GameObject 摆到位。</summary>
    private static string EnsureAnchors(GameObject root, in Shape shape, out bool added)
    {
        added = false;
        var head = CreateOrFindAnchor(root, "HeadBar", shape.HeadBarY, ref added);
        var center = CreateOrFindAnchor(root, "CenterPoint", shape.CenterY, ref added);
        return head != null && center != null ? "ok" : "fail:anchor-null";
    }

    private static GameObject CreateOrFindAnchor(GameObject root, string name, float y)
    {
        var dummy = false;
        return CreateOrFindAnchor(root, name, y, ref dummy);
    }

    private static GameObject CreateOrFindAnchor(GameObject root, string name, float y, ref bool added)
    {
        var tr = root.transform.Find(name);
        if (tr != null) return tr.gameObject;
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = new Vector3(0f, y, 0f);
        added = true;
        return go;
    }

    /// <summary>官方家具层索引：①GameLayer.Furniture 静态表（权威）；②层名兜底；都读不到=-1（不动层）。</summary>
    private static int ResolveFurnitureLayer()
    {
        try
        {
            var l = GameCore.HotUpdate.GameLayer.Furniture;
            if (l >= 0 && l <= 31) return l;
        }
        catch { }
        try
        {
            var l = LayerMask.NameToLayer("Furniture");
            if (l >= 0) return l;
        }
        catch { }
        return -1;
    }
}
