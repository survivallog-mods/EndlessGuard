using System;
using System.Collections.Generic;
using System.Reflection;
using GameCore.HotUpdate;                 // Furniture / BaseSingleton（F2 场景内设备材质克隆）
using GameCore.HotUpdate.Battle.Logic;    // BattleLogicWorld / TransformComponent（F2）
using HarmonyLib;
using Il2CppInterop.Runtime;
using GameCore.Scripts;                   // fixwave6：ShowState/ModelAnimator/StateChild/SpecChild（官方根契约类型）
using UnityEngine.AI;                     // fixwave6：NavMeshObstacle
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔程序化模型（spec §4.3 v2 精致版，作者 2026-10-30 定案）：内存拼装木质望塔 prefab，
/// 注册名 = 家具行 ResName「P_Tripo05」（Task 6 注入），总高 ≈1.9、占地 1×1（=游戏 1 格）。
/// 零外部资产/零 AssetBundle/零许可；材质运行时取得（四级降级链，见 MakeBaseMaterial）。
/// F2（2026-10-31 playtest 实证后改版）：模板/材质【懒建】——OnStep 只 Arm 旗标，首次 supply
///（场景内、设备在场）才 BuildTemplate：菜单期建模板时 FindObjectOfType&lt;Renderer&gt;()=空、
/// IL2CPP 下 URP Shader.Find=null，全塔落 Sprites/Default（"木盒子、箭看不见"的根因）。
/// 已建塔若仍是兜底材质，EnsureGoodMaterial 在每次供给时刻再试升级（设备克隆优先）。
/// F3：每次供给测世界高（mesh 8 角点+renderer.bounds 双口径 trace 前 3 次），父链非 1 缩放时
/// 按比例校根，目标 ≈1.9（±15% 死区）。
/// fixwave3（W3A，2026-10-31 playtest：wave2 加 BoxCollider 后塔仍不可点选/搬动/卸装）：dump.cs 取证
/// 官方选取管线=物理射线（MouseRaycastService.GetMouseHits，层掩码）→ Collider instanceID →
/// AgentModelManager.GetModelByColliderId → AgentModel；官方家具模型全链处于 GameLayer.Furniture 层，
/// 我方图元实例默认 Default(0) 层在掩码外=「碰撞体在而射线永不命中」。镜像：供给实例全链递归设为
/// GameLayer.Furniture（读不到退层名，再退不动+trace）；一次性取证 dump 在 TowerModelDiag（官方设备
/// 与我方塔各一次，含 AgentModelManager 注册探针）。W3B：程序化木纹/瓦纹贴图（TowerTextures）按
/// 贴图角色挂材质（Mat(color, role)），木件 ±3% 亮度桶抖动破均匀感（ArrowTowerFxMath.VarBucket）。
/// fixwave4（playtest round-4 裁定）：模型重构为官方 TowerDevice 状态组契约——根直下互斥
/// State01/State02/State03/StateBroken（各 Mesh/G_Tripo05X），仅 State01 激活；语义=耐久分层
///（Furniture_TowerDeviceStateCheck：STATE01_PERCENT=0.7 / STATE02_PERCENT=0.3；死亡=StateBroken；
/// 供电外观另走 SyncTowerDeviceModelState(bool)，dump.cs:376481）。布局-防御选取/悬停名/耐久 FSM
/// 通路依赖此契约（wave3 实证 Collider 已注册而不可选中=缺状态组）。
/// fixwave6（根因修复，2026-10-31 live dump 定案）：官方设备模型根 = Transform,BoxCollider,
/// NavMeshObstacle,ModelAnimator,ShowState + 子 State04/FX_BladeTrap/CenterPoint/HeadBar/FX_State03/
/// State01/02/03/StateBroken；我方根只有 Transform,BoxCollider + 状态组——TowerDevice 耐久 FSM 驱动件
///（AgentModel.ModelState/Animator，dump.cs:352394/352392）与悬停头栏世界锚（ModelAnimator.HeadBar/
/// CenterPoint 字段，dump.cs:1333608）全缺 = 不可选/无悬停的根因。镜像补齐：根上 AddComponent
/// ShowState/ModelAnimator/NavMeshObstacle + 空锚子节点 HeadBar(y≈2.1)/CenterPoint(y≈0.95) +
/// State04 克隆组（GameKey.State04 常量在 dump.cs:57781 而 TowerDeviceStateCheck 仅三档——契约形
/// 补齐防按名切换落空）；ShowState/ModelAnimator 默认 ctor=共享空桩（RVA 0x60BF70）字段全 null →
/// 补非空空表防官方侧遍历 NRE。EnsureModelContract 幂等（模板建一次克隆继承 + 每次供给核验，
/// Instantiate 原生重映射 HeadBar/CenterPoint/Renderers 引用）。详见 EnsureModelContract 方法注。
///
/// ══ Spike 结论（2026-10-31，对 10-29 现行 build 实测：Il2CppDumper 重跑现行 GameAssembly.dll +
/// global-metadata.dat 得现行 script.json，Iced（suite/tools/disasm_tool）全链逐条核读）══
/// 1. 缓存结构（BaseResCache&lt;T&gt;，gamekb dump 两字段偏移缺失，实测 cache=+0x18 objDict=+0x20）：
///    cache:Dictionary&lt;string,List&lt;GameObject&gt;&gt;（池，键=传入 Load* 的【原样路径串】）、
///    objDict:Dictionary&lt;GameObject,string&gt;（归属登记）。TryGetCache 是「弹出」不是「查询」——
///    取列表末尾 RemoveAt 返回（跳过已毁条目）：这是归还复用池，不是模板注册表。
/// 2. StorePrefab → AddCache 直通（tail-jmp 实证）。AddCache 语义：objDict 已有的 obj=归还入池
///    （位置归零/挂 CacheObj 根/失活/入 cache[key] 列）；objDict 没有的 obj=DestroyImmediate 杀掉
///    （父已挂 CacheObj 下则空操作）——直接 StorePrefab(我方模板) 是陷阱：模板被杀、缓存也不收。
/// 3. 三个 LoadGameObject 重载同构：TryGetCache 未命中 → LoadPrefabWithFallback（ResourceManager
///    HasAssetHandle？无则 LogWarning + LoadHandelSync 兜底 → Load）→ prefab 为 null 时
///    LogError + return null——【不抛异常】。命中/实例化路径：SetActive(true) + SetParent(root,…)
///    （重载1/2 另施加局部位姿；AddDict 只登记 objDict，不入 cache 列表）。
/// 4. 现行 RVA（10-29 build）：LoadPrefabWithFallback=0x2F3F820，LoadGameObject(string)=0x2F3EFF0、
///    (string,GO,bool)=0x2F3EDB0、(string,GO,v3,v3,v3)=0x2F3E8C0，StorePrefab=0x2F3FAC0，
///    TryGetCache/AddDict/AddCache=0x20557A0/0x2055380/0x2055020。10-23 gamekb 的 0x2EE3910 已漂移
///    （实测该地址现为别处静态初始化序列）。
/// 机制裁定（依据 3 的「miss 返 null 不抛」）：【Harmony POSTFIX】挂三个 LoadGameObject 重载——
/// path 含「P_Tripo05」（子串宽松匹配：家具行 ResName 原样进 LoadGameObject 的具体串格式无旁证，
/// 全路径/裸名两种可能都兼容）且 __result 为 null 时，补发 Instantiate(隐藏模板) 并镜像命中路径的
/// 后处理（SetActive(true)；重载1 补 SetParent(root,worldStays)；重载2 再补局部位姿）。
/// 选 POSTFIX 而非 PREFIX 的理由：原方法全量照跑、其余路径零扰动；PREFIX skip 需自补三重载的
/// SetParent/AddDict 语义（漂移面更大）。失败模式（两态均隔离）：补丁未挂载（interop 漂移/签名
/// 变化）→ 原方法照常返 null → 官方兜底盒 + EnsureRegistered 补丁核验 trace 告警；postfix 内
/// 异常 → catch 后原样放行 __result，游戏无感。
/// 模板：首次注册建一次（静态字段，DontDestroyOnLoad+失活隐藏），每次请求 Instantiate 派发；
/// 模板/实例无 Rigidbody、无 Animator 等任何与家具 FSM 相争的动态组件（纯 MeshFilter/MeshRenderer）；
/// fixwave2 起根上有唯一 BoxCollider（官方选取=物理射线，实测缺它塔无法点选/搬动/卸装）——散件图元
/// Collider 仍逐个拆除（旧裁定的"相争"指散件物理体，罩体是官方设备模型的同款形态）。被移除时官方
/// 归还路径 AddCache 会因 objDict 无此 obj 而直接 DestroyImmediate——等于干净销毁，无泄漏。
/// T8 动画锚点（fixwave4 更新）：弩机整件为 State01/Mesh/G_Tripo05A 下名为「Head」的直接子物体
///（旋转部，pivot 在平台中央）；旗子为同层「Flag」（pivot 在旗杆轴）——T8 只动这两个；Fx 在
/// 【当前激活状态组】内锚定（仅 State01 期有效，见 ArrowTowerFx）。
/// 校验口径（C1 加固）：VerifyPatches 双重核验=①三重载 postfix 在挂（PatchInfo.Postfixes）②我方
/// Postfix 的 __result 参数 IsByRef（ref 缺失=回写无效的静态防线）；运行时自证=首次 supply trace 带
/// instanceID（postfix 内打印≠回写成功的终证是场景出现该实例而非兜底盒，作者试玩核对）。
/// 注意：[HarmonyPatch] 只挂在下方三个嵌套补丁类上（EndlessGuardPlugin.Load 逐类自动装配按
/// 该特性扫描）；本外层类不带，否则会触发"TargetMethod 未找到"的假告警。
/// </summary>
internal static class ArrowTowerModel
{
    // ── 色板（预览页 7 档定案 + 瓦红/檐深/旗红/箭羽米/箭杆，spec §4.3）──────────────
    private static readonly Color StoneLight = new Color32(0x8d, 0x8a, 0x82, 0xff);
    private static readonly Color StoneDark = new Color32(0x6f, 0x6c, 0x66, 0xff);
    private static readonly Color WoodDark = new Color32(0x7d, 0x5a, 0x3f, 0xff);
    private static readonly Color WoodMid = new Color32(0x96, 0x71, 0x4c, 0xff);
    private static readonly Color WoodLight = new Color32(0xb0, 0x8a, 0x5e, 0xff);
    internal static readonly Color IronLight = new Color32(0x5b, 0x61, 0x67, 0xff);   // internal=T8 箭矢模板复用色板
    private static readonly Color IronDark = new Color32(0x3d, 0x42, 0x47, 0xff);
    private static readonly Color TileRed = new Color32(0x9b, 0x5a, 0x4a, 0xff);
    private static readonly Color Eave = new Color32(0x7c, 0x4a, 0x3e, 0xff);
    private static readonly Color FlagRed = new Color32(0xa3, 0x5b, 0x4e, 0xff);
    internal static readonly Color FletchCream = new Color32(0xdd, 0xd5, 0xc4, 0xff);   // internal=T8 箭矢模板复用色板
    internal static readonly Color ArrowShaft = new Color32(0xc9, 0xb4, 0x8a, 0xff);    // 同上

    internal static GameObject Template;                 // 隐藏模板（DontDestroyOnLoad+失活），只 Clone 不直发
    private static Material _baseMat;                    // 三级降级链产物（见 MakeBaseMaterial；F2 可后升级）
    private static readonly Dictionary<(Color, TowerTextures.Role), Material> Mats = new();
    private static bool _built, _armed, _failTraced, _suppliedTraced;
    private static int _supplyTraced;                    // F3：前 3 次 supply 的缩放/高度自证计数
    private static string _templateContract = "n/a";     // fixwave6：模板根契约摘要（进 prefab-built trace）

    private const float TargetHeight = 1.9f;             // F3：设计总高（spec §4.3），±15% 外才校正

    // ════════════════════════ 注册（Arm/EnsureRegistered + Harmony 补丁）════════════════════════

    /// <summary>F2 懒建武装：ArrowTower.OnStep 2s 巡检只置旗，实际 BuildTemplate 推迟到首次 supply
    ///（=场景内、设备在场的时刻）。playtest 2026-10-31 实证：主菜单期建模板时 FindObjectOfType<Renderer>
    /// 找不到任何 renderer、URP Shader.Find 在 IL2CPP 返 null → 全塔 Sprites/Default（"木盒子"）。
    /// 建模板本身就是首次 Mat() 消费点——推迟它=材质天然在场景内定级。</summary>
    internal static void Arm() => _armed = true;

    /// <summary>幂等注册：建一次模板 + 核验三 postfix 是否在挂（调用点=①supply 路径首次 miss（F2 懒建）
    /// ②ArrowTowerFx.BuildArrowTemplate 兜底）。失败静默重试（仅首次 trace），成功即一次性 trace。</summary>
    internal static void EnsureRegistered()
    {
        if (_built) return;
        try
        {
            BuildTemplate();
            _built = true;
            var via = VerifyPatches()
                ? "Harmony postfix LoadGameObject×3(miss→supply, __result ref 已核验)"
                : "POSTFIX 未在挂/回写核验失败（interop 漂移？）——实机将显示官方兜底盒";
            EndlessGuardPlugin.Trace("[Tower] model prefab built (states=5 State01/02/03/04/Broken"
                                     + ", parts=" + CountDescendants(Template.transform)
                                     + ", collider=Box(0.9,1.9,0.9)@y0.95"
                                     + ", contract=" + _templateContract
                                     + ", via=" + via + ")");
        }
        catch (Exception e)
        {
            if (!_failTraced)
            {
                _failTraced = true;
                EndlessGuardPlugin.Trace("[Tower] EnsureRegistered: " + e.Message + "（下次 supply 重试）");
            }
        }
    }

    /// <summary>核验三重载补丁闭环（C1 加固后口径，见类头「校验口径」）：①目标方法在、postfix 在挂；
    /// ②我方 Postfix 的 __result 参数为 ref（IsByRef）——按值声明=只读副本，回写无效（成功假象防线）。</summary>
    private static bool VerifyPatches()
    {
        try
        {
            var t = typeof(GameCore.HotUpdate.Battle.BattleResource);
            (Func<MethodBase>, Type)[] checks =
            {
                (() => AccessTools.Method(t, "LoadGameObject", new[] { typeof(string) }),
                    typeof(LoadGameObject_String)),
                (() => AccessTools.Method(t, "LoadGameObject",
                        new[] { typeof(string), typeof(GameObject), typeof(bool) }),
                    typeof(LoadGameObject_Root_Bool)),
                (() => AccessTools.Method(t, "LoadGameObject",
                        new[]
                        {
                            typeof(string), typeof(GameObject), typeof(Vector3),
                            typeof(Vector3), typeof(Vector3)
                        }),
                    typeof(LoadGameObject_Root_PosEulerScale))
            };
            foreach (var (finder, patchType) in checks)
            {
                var mi = finder();
                var info = mi == null ? null : Harmony.GetPatchInfo(mi);
                if (info == null || info.Postfixes.Count == 0) return false;
                var pm = patchType.GetMethod("Postfix",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (pm == null) return false;
                var byRef = false;
                foreach (var p in pm.GetParameters())
                    if (p.Name == "__result" && p.ParameterType.IsByRef) { byRef = true; break; }
                if (!byRef) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ── 三个重载的 postfix（插值参数用 __N 位置绑定，免疫 interop 参数名漂移）──

    [HarmonyPatch]
    private sealed class LoadGameObject_String
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GameCore.HotUpdate.Battle.BattleResource), "LoadGameObject",
                new[] { typeof(string) });

        private static void Postfix(string __0, ref GameObject __result) =>
            __result = Supply(path: __0, fallback: __result, root: null, worldStays: false,
                localPos: null, localEuler: null, localScale: null);
    }

    [HarmonyPatch]
    private sealed class LoadGameObject_Root_Bool
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GameCore.HotUpdate.Battle.BattleResource), "LoadGameObject",
                new[] { typeof(string), typeof(GameObject), typeof(bool) });

        private static void Postfix(string __0, GameObject __1, bool __2, ref GameObject __result) =>
            __result = Supply(path: __0, fallback: __result, root: __1, worldStays: __2,
                localPos: null, localEuler: null, localScale: null);
    }

    [HarmonyPatch]
    private sealed class LoadGameObject_Root_PosEulerScale
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GameCore.HotUpdate.Battle.BattleResource), "LoadGameObject",
                new[]
                {
                    typeof(string), typeof(GameObject), typeof(Vector3),
                    typeof(Vector3), typeof(Vector3)
                });

        private static void Postfix(string __0, GameObject __1, Vector3 __2, Vector3 __3, Vector3 __4,
            ref GameObject __result) =>
            __result = Supply(path: __0, fallback: __result, root: __1, worldStays: false,
                localPos: __2, localEuler: __3, localScale: __4);
    }

    /// <summary>补发入口（fixwave5 F3 增补：结果包装一层无答 miss 取证——P_Tripo05 请求拿到 null 且
    /// 我方未补发=竞态/门控状态新线索通道，每次会话只记一行）；核心见 SupplyCore。</summary>
    private static GameObject Supply(string path, GameObject fallback, GameObject root, bool worldStays,
        Vector3? localPos, Vector3? localEuler, Vector3? localScale)
    {
        var result = SupplyCore(path, fallback, root, worldStays, localPos, localEuler, localScale);
        NoteUnansweredMiss(path, result);
        return result;
    }

    /// <summary>F3 取证（一次性）：P_Tripo05 路径请求最终返 null（官方 miss 且我方未补发——含门未开
    /// armed/built 未就绪等状态）时打一行快照，之后静默。mod 总门关闭不算线索（人为关功能，非竞态）。
    /// 任何异常吞掉——取证行绝不干扰供给路径。</summary>
    private static bool _missTraced;

    private static void NoteUnansweredMiss(string path, GameObject result)
    {
        try
        {
            if (_missTraced || result != null || string.IsNullOrEmpty(path)) return;
            if (path.IndexOf(ArrowTower.ResName, StringComparison.Ordinal) < 0) return;
            if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;
            if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value) return;
            _missTraced = true;
            EndlessGuardPlugin.Trace("[Tower] supply miss unanswered: " + path
                                     + " armed=" + _armed + " built=" + _built
                                     + "（P_Tripo05 请求返 null 且我方未补发——竞态/门控线索，fixwave5 F3）");
        }
        catch { }
    }

    /// <summary>补发核心：仅当 path 命中箭塔名且原结果为 null（=官方缓存/资源双 miss，disasm 实证
    /// 此态返 null 不抛）时，Instantiate 模板并镜像官方命中路径的后处理。任何异常→原样放行官方值。
    /// F2：模板在此（首次 miss=场景内）才懒建（门=_armed，ArrowTower.OnStep 置位）；
    /// F3：供给后测高自证，父链非 1 缩放把世界高校回 ≈1.9。</summary>
    private static GameObject SupplyCore(string path, GameObject fallback, GameObject root, bool worldStays,
        Vector3? localPos, Vector3? localEuler, Vector3? localScale)
    {
        GameObject inst = null;
        try
        {
            if (fallback != null || path == null ||
                path.IndexOf(ArrowTower.ResName, StringComparison.Ordinal) < 0) return fallback;
            if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return fallback;
            if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value)
                return fallback;
            if (!_built)
            {
                if (_armed) EnsureRegistered();       // F2 懒建：首次 miss 才拼模板（材质同刻在场景内定级）
                if (Template == null) return fallback;
            }
            EnsureGoodMaterial();                     // F2：基材质若仍是兜底（非 URP），借本场景时刻再升一级

            var instObj = Object.Instantiate(Template);
            inst = instObj == null ? null : instObj.TryCast<GameObject>();
            if (inst == null) return fallback;
            inst.SetActive(true);                                            // 镜像官方命中路径（SetActive(true)）
            EnsureSelectableCollider(inst);                                  // fixwave2：选择碰撞体兜底（正常=零写）
            var contract = EnsureModelContract(inst, out var contractAdded); // fixwave6：官方根契约核验
            MirrorSelectableStructure(inst);                                 // fixwave3 W3A：全链镜像官方层（选取射线层掩码，见方法注）
            TowerModelDiag.NoteSuppliedTower(inst.transform);                // fixwave3 W3A：登记实例供一次性取证 dump
            if (root != null)
            {
                inst.transform.SetParent(root.transform, worldStays);        // 重载1 命中路径镜像
                if (localPos.HasValue) inst.transform.localPosition = localPos.Value;       // 重载2 镜像
                if (localEuler.HasValue) inst.transform.localEulerAngles = localEuler.Value;
                if (localScale.HasValue) inst.transform.localScale = localScale.Value;
                CompensateScale(inst);                                       // F3：世界高校正（±15% 外才动）
            }
            if (!_suppliedTraced)
            {
                _suppliedTraced = true;
                // 运行时自证（C1 口径）：本行在 postfix 内打印≠回写成功；ref 回写由 VerifyPatches 静态核验，
                // 实机终证=trace 的 instanceID 对应物出现在场景（而非官方兜底盒）。
                EndlessGuardPlugin.Trace("[Tower] model supplied on first miss: " + path
                                         + " instanceID=" + inst.GetInstanceID());
            }
            if (_supplyTraced < 3)                 // F3 自证（前 3 次）：根链 lossyScale + 实测世界高
            {
                _supplyTraced++;
                EndlessGuardPlugin.Trace("[Tower] supplied scale=" + inst.transform.lossyScale
                                         + " rendererH=" + RendererHeight(inst).ToString("0.00")
                                         + " meshH=" + MeshHeight(inst).ToString("0.00")
                                         + " (目标≈" + TargetHeight.ToString("0.0") + ")");
            }
            if (!_contractTraced)                  // fixwave6：会话首供给一次性完整契约自证
            {
                _contractTraced = true;
                EndlessGuardPlugin.Trace("[Tower] contract attach: " + contract);
            }
            else if (contractAdded)                // 修复路径（克隆体缺件被补回）单独可见
            {
                EndlessGuardPlugin.Trace("[Tower] contract re-attached on supplied instance: " + contract);
            }
            return inst;
        }
        catch (Exception e)
        {
            if (inst != null) Object.Destroy(inst);   // M4：半途异常不留激活孤儿克隆
            EndlessGuardPlugin.Trace("[Tower] supply: " + e.Message);
            return fallback;
        }
    }

    // ══════════════════════════ F3 高度自证/校正 ══════════════════════════

    /// <summary>世界高校正（F3）：实测塔总高偏离目标 1.9 超 ±15% 时按比例缩根（父链 lossyScale≠1 的
    /// 兜底补偿；正常链 = 恒等无写）。实测优先 mesh 包围盒 8 角点世界变换（确定性、不依赖引擎剔除态），
    /// 退化用 renderer.bounds。调整后前 3 次 trace 自证。</summary>
    private static void CompensateScale(GameObject inst)
    {
        try
        {
            var h = MeshHeight(inst);
            if (h <= 0.01f) h = RendererHeight(inst);
            if (h <= 0.01f) return;                                // 量不到不动（宁高勿矮的错误补偿）
            if (Math.Abs(h - TargetHeight) <= TargetHeight * 0.15f) return;
            var k = TargetHeight / h;
            var ls = inst.transform.localScale;
            inst.transform.localScale = new Vector3(ls.x * k, ls.y * k, ls.z * k);
            if (_supplyTraced < 3)
                EndlessGuardPlugin.Trace("[Tower] scale compensated ×" + k.ToString("0.###")
                                         + " height " + h.ToString("0.00") + "→" + MeshHeight(inst).ToString("0.00"));
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] scale: " + e.Message); }
    }

    /// <summary>mesh 包围盒估高（F3）：全部 MeshFilter 的 sharedMesh.bounds 8 角点经 localToWorldMatrix
    /// 变换取 y 极差——不依赖 renderer.bounds 的剔除/可见性懒更新，新建未渲染帧也可测。
    /// fixwave4：GetComponentsInChildren 默认只取激活子树=状态组下恰只量激活的 State01 全量塔身（口径正确，
    /// 失活克隆/残骸不计入）。</summary>
    private static float MeshHeight(GameObject go)
    {
        try
        {
            var filters = go.GetComponentsInChildren<MeshFilter>();
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var any = false;
            foreach (var mf in filters)
            {
                if (mf == null) continue;
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var b = mesh.bounds;
                var tr = mf.transform;
                for (int i = 0; i < 8; i++)
                {
                    var corner = tr.TransformPoint(new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z));
                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                }
                any = true;
            }
            return any ? max.y - min.y : 0f;
        }
        catch { return 0f; }
    }

    /// <summary>renderer.bounds 估高（F3 trace 口径；无有效 bounds=0）。</summary>
    private static float RendererHeight(GameObject go)
    {
        try
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            float min = float.MaxValue, max = float.MinValue;
            foreach (var r in rs)
            {
                if (r == null) continue;
                var b = r.bounds;
                if (b.size.y <= 0f) continue;
                min = Math.Min(min, b.min.y);
                max = Math.Max(max, b.max.y);
            }
            return max > min ? max - min : 0f;
        }
        catch { return 0f; }
    }

    // ══════════════════════════ F2 材质升级（场景内再定级）══════════════════════════

    /// <summary>基材质升级（F2）：当前基材质已 URP=直返（稳态零开销）；仍非 URP（首建时全链兜底
    /// 都失败的遗留态）则按 MakeBaseMaterial 降级链再试一次，只有真升到 URP 才收货——清逐色缓存、
    /// 重染模板、毁箭矢模板（下支箭用新材质重建）、重染已放置塔（ArrowTowerFx 根注册表）。</summary>
    private static void EnsureGoodMaterial()
    {
        if (_baseMat != null && IsUrpShader(_baseMat.shader)) return;
        try
        {
            var better = MakeBaseMaterial();
            if (better == null) return;
            if (!IsUrpShader(better.shader))
            {
                Object.Destroy(better);               // 升不动=丢弃这次试产的兜底材质（防每次 supply 泄一份）
                return;                               // 维持旧底（防重建抖动）
            }
            _baseMat = better;
            Mats.Clear();                             // 逐色缓存全部作废（Retint 按 OldMat 色值经 Mat() 重建=新底）
            if (Template != null) RetintDescendants(Template.transform);
            try { ArrowTowerFx.RetintRootsAndResetArrowTemplate(); }
            catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] retint roots: " + e.Message); }
            EndlessGuardPlugin.Trace("[Tower] material upgraded in-scene: " + better.shader.name);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] material upgrade: " + e.Message); }
    }

    private static bool IsUrpShader(Shader sh) =>
        sh != null && sh.name.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>重染后代（F2 升级路径）：每 renderer 的旧材质读回色值 → Mat(色, 旧材质反推的角色) 从新底
    /// 重建并回挂——fixwave3 W3B 起角色感知，贴图与平铺在材质升级（Sprites/Default→URP）后原样保留。
    /// fixwave4：按 Transform 递归（childCount/GetChild/GetComponent 与激活态无关）=等价
    /// GetComponentsInChildren(true)，State02/03/Broken 失活组的克隆 renderer 同样全覆盖。
    /// Template 与已放置塔根共用（结构件材质全走 Mats 缓存这一条路）。internal=ArrowTowerFx 回调重染已放置塔。</summary>
    internal static void RetintDescendants(Transform t)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c == null) continue;
            var r = c.GetComponent<MeshRenderer>();
            if (r != null && r.sharedMaterial != null)
            {
                var old = r.sharedMaterial;
                r.sharedMaterial = Mat(ColorOf(old), RoleOfMat(old));
            }
            RetintDescendants(c);
        }
    }

    /// <summary>从结构件旧材质读回色值（_BaseColor 优先，退 _Color，再退白）。</summary>
    private static Color ColorOf(Material m)
    {
        try
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
        }
        catch { }
        return Color.white;
    }

    // ══════════════════════════════ 模板拼装（spec §4.3 结构件清单 + fixwave4 状态组契约）═════════════════════════════

    /// <summary>fixwave4 重构为官方 TowerDevice 模型契约（playtest round-4 + diag trace 11:46:04）：
    /// 官方电锯层级=根直下互斥状态组 State01/Mesh/G_Tripo04A、State02/…、State03/…、StateBroken/…，
    /// 二进制字面量 State01/State02/State03/StateBroken（各 ×3）。语义（dump.cs 实证，不猜）：
    /// Furniture_TowerDeviceStateCheck（TypeDefIndex 6383）常量 STATE01_PERCENT=0.7、STATE02_PERCENT=0.3、
    /// args.LastStateKey(string)——按耐久百分比换态：&gt;70%=State01（完好）、30~70%=State02、≤30%=State03
    ///（重损）；死亡=StateBroken 残骸；供电外观另走 Furniture.SyncTowerDeviceModelState(bool powered)
///（dump.cs:376481）。布局-防御选取/悬停名/耐久通路依赖此状态组契约（wave3 实证：Collider 已注册
/// modelCfg=99153 而仍不可选中=缺状态组）。我方结构：State01/Mesh/G_Tripo05A=全量塔身（弩机「Head」、
/// 旗「Flag」在内）；State02/Mesh/G_Tripo05B、State03/Mesh/G_Tripo05C、State04/Mesh/G_Tripo05E
///（fixwave6）=全量静态克隆（Head 不参与动画——控制方裁定）；StateBroken/Mesh/G_Tripo05D=残骸堆。
/// 仅 State01 激活（FSM 按名切换开闭）。
/// 性能注：4 份全量克隆 ≈370 对象/塔（fixwave6 加 State04 组）——少数塔可承受；未来 LOD/共享化候选
///（裁定留档）。
    /// W3B 贴图角色不变（克隆共享同一批缓存材质；Retint 按 Transform 递归，失活组同样覆盖）。</summary>
    private static void BuildTemplate()
    {
        Template = new GameObject(ArrowTower.ResName);
        Template.SetActive(false);                       // 模板永不直发（Supply 每次派发 clone 并转正）
        Object.DontDestroyOnLoad(Template);
        var t = Template.transform;

        var s1 = NewStateGroup(t, "State01");            // 完好态（唯一激活组，Fx 动画锚所在）
        var a = NewStateMesh(s1, "G_Tripo05A");
        BuildTowerBody(a);

        var s2 = NewStateGroup(t, "State02");            // 耐久 30~70% 外观 = 全量静态克隆
        var bMesh = NewStateMesh(s2, "G_Tripo05B");
        var b = CloneTower(a, "G_Tripo05B");
        if (b != null) b.SetParent(bMesh, false);
        s2.gameObject.SetActive(false);

        var s3 = NewStateGroup(t, "State03");            // 耐久 ≤30% 外观 = 全量静态克隆
        var cMesh = NewStateMesh(s3, "G_Tripo05C");
        var c = CloneTower(a, "G_Tripo05C");
        if (c != null) c.SetParent(cMesh, false);
        s3.gameObject.SetActive(false);

        var s4 = NewStateGroup(t, "State04");            // fixwave6：第 4 档外观组（官方 GameKey.State04
        var e4Mesh = NewStateMesh(s4, "G_Tripo05E");     // 常量在 dump.cs:57781 而 TowerDeviceStateCheck
        var e4 = CloneTower(a, "G_Tripo05E");            // 仅三档——未见切换但契约形补齐，防通用状态链按名
        if (e4 != null) e4.SetParent(e4Mesh, false);     // 切换落空塔隐形；默认失活零渲染开销）
        s4.gameObject.SetActive(false);

        var sB = NewStateGroup(t, "StateBroken");        // 死亡残骸（简堆，总高 ≈0.3）
        var dMesh = NewStateMesh(sB, "G_Tripo05D");
        BuildDebris(dMesh);
        sB.gameObject.SetActive(false);

        // ── 选择碰撞体（fixwave2 起在根上；wave3 diag-reg 实证 AgentModel 收编根 Collider=注册链闭合，
        // wave4 补状态组=选取/悬停 FSM 通路闭合）：罩住塔身（占地 1×1=一格，高 1.9；旗杆/顶饰球略超出
        // 不计入）。子物件图元的 Collider 仍在 Box/Ball/Pyramid 里逐个拆除（防 FSM 相争旧裁定只针对
        // 散件碰撞体；根上这一个罩体=官方设备模型的同款形态）。
        var col = Template.AddComponent<BoxCollider>();
        col.size = new Vector3(0.9f, 1.9f, 0.9f);
        col.center = new Vector3(0f, 0.95f, 0f);

        // ── 官方根契约补齐（fixwave6）：ShowState/ModelAnimator/NavMeshObstacle + HeadBar/CenterPoint
        // 锚点——挂在模板根上，Clone 时 Instantiate 原生序列化重映射组件内引用（HeadBar/CenterPoint/
        // Renderers 指向克隆体自己的节点）。幂等（见 EnsureModelContract）；模板路径失败随 EnsureRegistered
        // 下次供给重试。返回串仅诊断用（模板期不单独 trace，实例首供给统一打）。
        try { _templateContract = EnsureModelContract(Template, out _); }
        catch (Exception e)
        {
            _templateContract = "fail:" + e.Message;
            EndlessGuardPlugin.Trace("[Tower] template contract: " + e.Message);
        }
    }

    /// <summary>根直下状态组节点 StateXX（官方 FSM SetModelState/按名开闭契约，dump.cs 字面量）。</summary>
    private static Transform NewStateGroup(Transform root, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        return go.transform;
    }

    /// <summary>状态组内「Mesh / G_Tripo05X」两层包装（镜像官方 State01/Mesh/G_Tripo04A 层级形态）。</summary>
    private static Transform NewStateMesh(Transform group, string nodeName)
    {
        var mesh = new GameObject("Mesh");
        mesh.transform.SetParent(group, false);
        var node = new GameObject(nodeName);
        node.transform.SetParent(mesh.transform, false);
        return node.transform;
    }

    /// <summary>全量塔身克隆（State02/03 用）：材质/贴图共享同一批缓存件（W3B），名字抹掉 Instantiate 的
    /// " (Clone)" 后缀。克隆件保持激活（可见性由状态组开闭管，FSM 只切组；组失活=整组隐藏）。
    /// 返 null=克隆异常（该组留空壳——FSM 切过去无显示不炸，模板重建自愈）。</summary>
    private static Transform CloneTower(Transform body, string newName)
    {
        try
        {
            var obj = Object.Instantiate(body.gameObject);
            var go = obj.TryCast<GameObject>();
            if (go == null) return null;
            go.name = newName;
            return go.transform;
        }
        catch (Exception e)
        {
            EndlessGuardPlugin.Trace("[Tower] clone " + newName + ": " + e.Message);
            return null;
        }
    }

    /// <summary>全量塔身（≈92 件，全 Cube/Sphere 图元去 Collider + 手写 4 棱锥/燕尾旗网格），挂在
    /// State01/Mesh/G_Tripo05A 下；旋转部弩机为直接子物体「Head」（pivot 平台中央）、旗为「Flag」
    ///（pivot 旗杆轴）——fixwave4 起二者在 State01 组内，Fx 于激活组内锚定。fixwave3 W3B：木件按
    /// 大面/小件挂 PlankBig/PlankSmall 程序化木纹，塔顶挂 Roof 瓦纹（铁/石/旗/羽 Flat 纯色）。</summary>
    private static void BuildTowerBody(Transform t)
    {
        var PB = TowerTextures.Role.PlankBig;            // 木纹大面 (1.5,1.5)
        var PS = TowerTextures.Role.PlankSmall;          // 木纹小件 (1,1)
        var RF = TowerTextures.Role.Roof;                // 瓦纹 (1.5,1.5)

        // ── 石砌基座 y0-0.28（0.92 方主体 + 0.98 压顶沿 + 四角石块 0.16；石件 Flat 纯色）──
        Box(new Vector3(0.92f, 0.22f, 0.92f), P(0, 0.11f, 0), StoneLight, t);
        Box(new Vector3(0.98f, 0.06f, 0.98f), P(0, 0.25f, 0), StoneDark, t);
        foreach (var (x, z) in new[] { (-0.38f, -0.38f), (0.38f, -0.38f), (-0.38f, 0.38f), (0.38f, 0.38f) })
            Box(new Vector3(0.16f, 0.28f, 0.16f), P(x, 0.14f, z), StoneDark, t);

        // ── 木塔身三段收分 y0.28-1.07（宽 0.76/0.70/0.64 交替深浅）──
        Box(new Vector3(0.76f, 0.26f, 0.76f), P(0, 0.41f, 0), WoodMid, t, role: PB);
        Box(new Vector3(0.70f, 0.26f, 0.70f), P(0, 0.67f, 0), WoodLight, t, role: PB);
        Box(new Vector3(0.64f, 0.27f, 0.64f), P(0, 0.935f, 0), WoodMid, t, role: PB);
        foreach (var (x, z) in new[] { (-0.35f, -0.35f), (0.35f, -0.35f), (-0.35f, 0.35f), (0.35f, 0.35f) })
            Box(new Vector3(0.06f, 0.79f, 0.06f), P(x, 0.675f, z), WoodDark, t, role: PS);          // 四角柱
        Box(new Vector3(0.80f, 0.04f, 0.80f), P(0, 0.54f, 0), WoodDark, t, role: PB);               // 段间横带×2
        Box(new Vector3(0.74f, 0.04f, 0.74f), P(0, 0.80f, 0), WoodDark, t, role: PB);
        Box(new Vector3(0.045f, 0.66f, 0.075f), P(-0.385f, 0.675f, 0), WoodDark, t, E(40, 0, 0), PS);   // 背面 X 斜撑
        Box(new Vector3(0.045f, 0.66f, 0.075f), P(-0.385f, 0.675f, 0), WoodDark, t, E(-40, 0, 0), PS);
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.46f, 0), IronDark, t);         // 正面射孔×3（铁 Flat）
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.70f, 0), IronDark, t);
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.90f, 0), IronDark, t);
        Box(new Vector3(0.03f, 0.72f, 0.045f), P(-0.40f, 0.64f, -0.09f), WoodDark, t, role: PS);    // 背面爬梯（2 轨）
        Box(new Vector3(0.03f, 0.72f, 0.045f), P(-0.40f, 0.64f, 0.09f), WoodDark, t, role: PS);
        for (var i = 0; i < 7; i++)                                                       // 7 踏
            Box(new Vector3(0.03f, 0.035f, 0.16f), P(-0.40f, 0.32f + i * 0.10f, 0), WoodLight, t, role: PS);

        // ── 挑台托架×4 + 平台 y1.11-1.19（1.04 见方，挑出塔身）──
        foreach (var (x, z) in new[] { (-0.40f, -0.40f), (0.40f, -0.40f), (-0.40f, 0.40f), (0.40f, 0.40f) })
            Box(new Vector3(0.16f, 0.05f, 0.16f), P(x, 1.095f, z), WoodDark, t, role: PS);
        Box(new Vector3(1.04f, 0.08f, 1.04f), P(0, 1.15f, 0), WoodLight, t, role: PB);

        // ── 垛口护栏 y1.23-1.44（四边矮墙 0.1 高 + 每边 5 垛齿 + 檐角圆球×4）──
        Box(new Vector3(1.04f, 0.10f, 0.06f), P(0, 1.28f, -0.49f), WoodMid, t, role: PB);
        Box(new Vector3(1.04f, 0.10f, 0.06f), P(0, 1.28f, 0.49f), WoodMid, t, role: PB);
        Box(new Vector3(0.06f, 0.10f, 0.92f), P(-0.49f, 1.28f, 0), WoodMid, t, role: PB);
        Box(new Vector3(0.06f, 0.10f, 0.92f), P(0.49f, 1.28f, 0), WoodMid, t, role: PB);
        var teeth = new[] { -0.416f, -0.208f, 0f, 0.208f, 0.416f };                       // 每边 5 垛齿
        foreach (var o in teeth)
        {
            Box(new Vector3(0.10f, 0.11f, 0.06f), P(o, 1.385f, -0.49f), WoodDark, t, role: PS);
            Box(new Vector3(0.10f, 0.11f, 0.06f), P(o, 1.385f, 0.49f), WoodDark, t, role: PS);
            Box(new Vector3(0.06f, 0.11f, 0.10f), P(-0.49f, 1.385f, o), WoodDark, t, role: PS);
            Box(new Vector3(0.06f, 0.11f, 0.10f), P(0.49f, 1.385f, o), WoodDark, t, role: PS);
        }
        foreach (var (x, z) in new[] { (-0.49f, -0.49f), (0.49f, -0.49f), (-0.49f, 0.49f), (0.49f, 0.49f) })
            Ball(0.10f, P(x, 1.45f, z), WoodLight, t);                                    // 檐角圆球×4（Flat）

        // ── 四坡顶 y1.5-1.91（檐口 + 四短柱 + 4 棱锥手写网格 + 顶饰球）──
        foreach (var (x, z) in new[] { (-0.30f, -0.30f), (0.30f, -0.30f), (-0.30f, 0.30f), (0.30f, 0.30f) })
            Box(new Vector3(0.05f, 0.07f, 0.05f), P(x, 1.475f, z), WoodDark, t, role: PS);          // 四短柱
        Box(new Vector3(0.92f, 0.05f, 0.92f), P(0, 1.52f, 0), Eave, t, role: PB);                   // 檐口
        Pyramid(0.66f, 0.36f, P(0, 1.55f, 0), TileRed, t, E(0, 0, 0), RF);
        // （手写网格基座方角天然落在对角线上=四坡面正对四墙，等价「Cone(4) 转45°」的意图，不再额外转）
        Ball(0.07f, P(0, 1.945f, 0), IronDark, t);                                        // 顶饰球（评审口径 0.035 取半径）

        // ── 旗杆 + 红燕尾旗（角柱外 0.52,·,0.52；Flag 子物体 T8 摆动；旗面/旗杆球 Flat）──
        Box(new Vector3(0.025f, 0.80f, 0.025f), P(0.52f, 1.59f, 0.52f), WoodDark, t, role: PS);
        Ball(0.06f, P(0.52f, 2.01f, 0.52f), IronDark, t);
        Flag(P(0.532f, 1.86f, 0.52f), FlagRed, t);

        // ── 重型弩机 y1.28 平台中央 → 整件子物体「Head」（T8 旋转部；木件 PS，铁件 Flat）──
        var head = new GameObject("Head");
        head.transform.SetParent(t, false);
        head.transform.localPosition = P(0, 1.28f, 0);
        var h = head.transform;
        Box(new Vector3(0.10f, 0.10f, 0.10f), P(0, -0.04f, 0), IronDark, h);              // 枢轴柱
        Box(new Vector3(0.46f, 0.05f, 0.07f), P(0.13f, 0.025f, 0), WoodMid, h, role: PS); // 托身（+x 朝前）
        Box(new Vector3(0.05f, 0.06f, 0.075f), P(0.02f, 0.025f, 0), IronDark, h);         // 铁件
        Box(new Vector3(0.30f, 0.025f, 0.03f), P(0.20f, 0.06f, 0), IronLight, h);         // 导轨
        Box(new Vector3(0.05f, 0.045f, 0.045f), P(0.30f, 0.065f, 0), IronDark, h);        // 弓脊
        Box(new Vector3(0.03f, 0.03f, 0.24f), P(0.30f, 0.07f, 0.135f), WoodDark, h, E(-28.65f, 0, 0), PS); // 后掠双弓臂
        Box(new Vector3(0.03f, 0.03f, 0.24f), P(0.30f, 0.07f, -0.135f), WoodDark, h, E(28.65f, 0, 0), PS);
        Ball(0.04f, P(0.30f, 0.127f, 0.24f), IronLight, h);                               // 铁帽×2
        Ball(0.04f, P(0.30f, 0.127f, -0.24f), IronLight, h);
        Box(new Vector3(0.008f, 0.008f, 0.48f), P(0.30f, 0.127f, 0), FletchCream, h);     // 弦
        Box(new Vector3(0.30f, 0.02f, 0.02f), P(0.16f, 0.055f, 0), ArrowShaft, h, role: PS);  // 上膛箭杆（W3B 木纹杆）
        Pyramid(0.02f, 0.06f, P(0.31f, 0.055f, 0), IronLight, h, E(0, 0, -90));           // 锥头（+x 朝前）
        Box(new Vector3(0.07f, 0.035f, 0.004f), P(0.055f, 0.055f, 0), FletchCream, h);    // 双羽
        Box(new Vector3(0.07f, 0.004f, 0.035f), P(0.055f, 0.055f, 0), FletchCream, h);
        Box(new Vector3(0.16f, 0.05f, 0.05f), P(-0.06f, 0, 0.075f), WoodDark, h, role: PS);   // 侧箭匣
        var tailX = new[] { -0.10f, -0.06f, -0.02f };
        foreach (var x in tailX)
            Box(new Vector3(0.014f, 0.05f, 0.014f), P(x, 0.055f, 0.075f), ArrowShaft, h, role: PS);   // 露箭尾×3
        foreach (var x in tailX)
            Box(new Vector3(0.018f, 0.02f, 0.018f), P(x, 0.09f, 0.075f), FletchCream, h); // 箭尾羽帽×3

        // （选择碰撞体不在塔身内——fixwave4 起塔身是状态组的内层，根上唯一 BoxCollider 见 BuildTemplate 尾部。）
    }

    /// <summary>StateBroken 残骸堆（G_Tripo05D）：8 件倾倒/半埋箱体+散板（木深/中色，PlankSmall 木纹）
    /// + 2 铁件，总高 ≈0.3；欧拉角全确定性（无随机），每次重建同形。</summary>
    private static void BuildDebris(Transform t)
    {
        var PS = TowerTextures.Role.PlankSmall;
        Box(new Vector3(0.34f, 0.2f, 0.3f), P(0.04f, 0.1f, 0.02f), WoodDark, t, E(12, 25, -8), PS);    // 主残堆
        Box(new Vector3(0.22f, 0.14f, 0.26f), P(-0.16f, 0.07f, -0.12f), WoodMid, t, E(-14, -30, 10), PS);
        Box(new Vector3(0.18f, 0.1f, 0.18f), P(0.2f, 0.05f, -0.18f), WoodDark, t, E(20, 70, 15), PS);
        Box(new Vector3(0.26f, 0.08f, 0.2f), P(-0.05f, 0.04f, 0.22f), WoodMid, t, E(-8, 100, -12), PS);
        Box(new Vector3(0.3f, 0.025f, 0.07f), P(0.28f, 0.015f, 0.12f), WoodDark, t, E(0, 40, 3), PS);   // 散板×3
        Box(new Vector3(0.26f, 0.025f, 0.06f), P(-0.3f, 0.015f, 0.05f), WoodMid, t, E(2, -65, -4), PS);
        Box(new Vector3(0.22f, 0.025f, 0.06f), P(0.1f, 0.012f, -0.3f), WoodDark, t, E(-3, 15, 5), PS);
        Box(new Vector3(0.12f, 0.05f, 0.07f), P(-0.22f, 0.03f, -0.26f), IronDark, t, E(18, -15, 22));   // 铁件×2
        Ball(0.05f, P(0.3f, 0.03f, -0.05f), IronDark, t);
    }

    /// <summary>供给实例的选择碰撞体兜底（fixwave2）：Instantiate 原样带模板 BoxCollider；此处只兜
    /// 模板重建/异常半途等边角态缺 Collider 的实例（补同一个尺寸/中心，一次性 trace）。正常路径
    /// 一次组件存在性检查即出，零写。</summary>
    private static bool _colliderFixTraced;

    private static void EnsureSelectableCollider(GameObject inst)
    {
        try
        {
            if (inst == null || inst.GetComponent<BoxCollider>() != null) return;
            var col = inst.AddComponent<BoxCollider>();
            col.size = new Vector3(0.9f, 1.9f, 0.9f);
            col.center = new Vector3(0f, 0.95f, 0f);
            if (!_colliderFixTraced)
            {
                _colliderFixTraced = true;
                EndlessGuardPlugin.Trace("[Tower] collider re-added on supplied instance (模板态异常?)");
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] collider: " + e.Message); }
    }

    // ══════════════════ fixwave3 W3A：官方选取结构镜像（层）══════════════════

    /// <summary>官方选取管线（dump.cs 取证，2026-10-31）：MouseRaycastService.GetMouseHits（TypeDefIndex
    /// 5064，RVA 0x2FA4180，帧缓存物理射线）→ hit 的 Collider instanceID → AgentModelManager.
    /// GetModelByColliderId（TypeDefIndex 4794，RVA 0x2F16D70；索引=_modelByColliderId 字典，
    /// AddColliderIndex RVA 0x2F149D0 由 AgentModel（_collider/_colliderId 字段 0x48/0x40）在模型
    /// 注册期收编）→ AgentModel → 悬停/选中/菜单。RenovationShowManager.GetDyeCandidate(
    /// AgentModelManager, int colliderId)（RVA 0x2FA7580）佐证同一条 colliderId 解析路径。
    /// ⇒ 物理射线走层掩码（GameLayer 类：Furniture/Item/NPC/各 HitLayer 静态层表，TypeDefIndex 553），
    /// 官方家具模型全部处于 GameLayer.Furniture 层；我方图元实例默认 Default(0) 层=射线掩码外，
    /// wave2 的 BoxCollider 因此"在而不可点"。镜像：全链（根+全部子孙）设为 GameLayer.Furniture
    ///（读不到退 LayerMask.NameToLayer("Furniture")，再退=不动并一次性 trace）。改层只在供给时刻
    /// 对我方实例执行（官方数据/官方实例零触碰）。BoxCollider 保留（AgentModel 收编的是 collider 本体，
    /// 官方模型同款"模型上带 Collider"形态）。</summary>
    private static bool _layerTraced;

    private static void MirrorSelectableStructure(GameObject inst)
    {
        try
        {
            if (inst == null) return;
            var layer = ResolveFurnitureLayer();
            if (layer < 0)
            {
                if (!_layerTraced)
                {
                    _layerTraced = true;
                    EndlessGuardPlugin.Trace("[Tower] layer mirror: GameLayer.Furniture 不可读（保持 Default，dump 将对照官方层）");
                }
                return;
            }
            if (inst.layer == layer) return;                    // 幂等：已在目标层零写
            SetLayerRecursive(inst.transform, layer);
            if (!_layerTraced)
            {
                _layerTraced = true;
                EndlessGuardPlugin.Trace("[Tower] layer mirrored: 全链 → layer " + layer
                                         + " (GameLayer.Furniture，官方选取射线层；wave2 不可点选根因=Default 层在射线掩码外)");
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] layer mirror: " + e.Message); }
    }

    /// <summary>官方家具层索引：①GameLayer.Furniture 静态表（游戏自己的映射，权威）；②层名兜底。</summary>
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

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c != null) SetLayerRecursive(c, layer);
        }
    }

    // ══════════════ fixwave6：官方模型根契约补齐（ShowState/ModelAnimator/NavMeshObstacle/锚点）══════════════
    //
    // 根因（2026-10-31 live dump 定案）：官方设备模型根组件 = Transform,BoxCollider,NavMeshObstacle,
    // ModelAnimator,ShowState，children = State04,FX_BladeTrap,CenterPoint,HeadBar,FX_State03,
    // State01,State02,State03,StateBroken；我方供给根只有 Transform,BoxCollider + State01/02/03/Broken。
    // TowerDevice 耐久 FSM 驱动件（AgentModel.ModelState=ShowState / AgentModel.Animator=ModelAnimator，
    // dump.cs:352394/352392）与悬停头栏世界锚（ModelAnimator.HeadBar/CenterPoint 字段，dump.cs:1333608）
    // 全缺 ⇒ 不可选中 + 无悬停信息。逐件 dump.cs 考证：
    // • ModelAnimator（GameCore.Scripts，dump.cs:1333608）：MonoBehaviour，字段 HeadBar/CenterPoint/
    //   FlashLight(GameObject)、AnimatorPlayer(Animator)、Renderers/Skinneds(List)。默认 .ctor 与
    //   ShowState 等同类=共享空桩（RVA 0x60BF70）⇒ AddComponent 后字段全 null：引用字段必须回填
    //   （HeadBar/CenterPoint 指锚点；Renderers 填本塔激活 renderer 清单，Skinneds 补非空空表）——
    //   否则官方侧遍历/取锚即 NRE。AnimatorPlayer/FlashLight 留空（我方程序化网格无 Animator/闪光件，
    //   官方 WaitModelStateOnce 有 30s fallback（dump.cs:352507）兜底）——部分契约也是进步，trace 记缺口。
    // • ShowState（GameCore.Scripts，dump.cs:1333856）：MonoBehaviour，States(List<StateChild>)/
    //   SpecChilds(List<SpecChild>)，公开 ChangeState(string)/OpenTag(string,bool)。链路：耐久 FSM
    //   Furniture_TowerDeviceStateCheck（dump.cs:414763）→ AgentModel.SetModelState(stateKey)
    //  （dump.cs:352694）→ AgentModel.ModelState 驱动。本轮只补组件+非空空表：StateChild 条目
    //   （Key/Open_GameObjects/Close_GameObjects 映射）是官方预制体内的序列化数据、方法体不可见，
    //   语义未证不猜（下一轮实机 lever，见 fixwave5 报告 wave6 节）——空表 ChangeState 落空=无伤，
    //   null 表=官方侧 NRE，两害取其轻。
    // • NavMeshObstacle（UnityEngine.AI，dump.cs:1460540，sealed Behaviour）：官方根同款原生组件；
    //   运行时开合由 AgentModel.ApplyTowerDeviceNavObstacle（dump.cs:2EF8F80）/
    //   OnTowerDeviceBlockingChanged（dump.cs:2F02870）接管——我方只按根 BoxCollider 同 footprint
    //   预配置（Box 0.9×1.9×0.9 @y0.95，carving=false 不抢官方语义）。
    // • HeadBar/CenterPoint 锚点：空 GameObject。HeadBar y≈2.1（塔顶上方——悬停名/血条的世界投影源；
    //   UI_FurnitureHeadBar_Vm（dump.cs:241331）的 Obj_Hp/Text_Name 全是 UI prefab 侧子件，世界侧
    //   只需锚 Transform，无「HeadBar 之下还要子节点」的证据——按 brief「anchors first」不过度搭建）；
    //   CenterPoint y≈0.95（塔身中心，与根罩体中心同高）。
    // 幂等：全部走 GetComponent 存在检查/锚点名查找；模板建成时挂一次（Clone 继承，Instantiate 原生
    // 序列化重映射组件内引用到克隆体自己的节点），每次供给再核验一遍（修复=补一行 trace；重染路径
    // EnsureGoodMaterial 只动 sharedMaterial，与组件无关，天然不重复）。

    /// <summary>锚点高度（HeadBar=塔顶上方 y≈2.1；CenterPoint=塔身中心 y≈0.95，brief 定值）。</summary>
    private const float HeadBarAnchorY = 2.1f;
    private const float CenterPointAnchorY = 0.95f;

    /// <summary>会话首供给一次性完整契约 trace（与 _suppliedTraced 同款防刷屏纪律）。</summary>
    private static bool _contractTraced;

    /// <summary>确保 root 具备官方模型根契约（组件+锚点），幂等；返回摘要串
    /// "ShowState=… ModelAnimator=… NavMeshObstacle=… HeadBar/CenterPoint=…"（每件 ok / ok(缺口) /
    /// fail:原因）。added=true=本次实际补了组件/锚点（修复路径，trace 用）。单件隔离：一件 fail 不拖垮
    /// 其余（部分契约也是进步）。调用点=①BuildTemplate 尾（模板根，克隆继承）②SupplyCore 每次供给。</summary>
    internal static string EnsureModelContract(GameObject root, out bool added)
    {
        added = false;
        if (root == null)
            return "ShowState=fail:null ModelAnimator=fail:null NavMeshObstacle=fail:null HeadBar/CenterPoint=fail:null";

        string ss, ma, nmo, anch;
        bool a1, a2, a3, a4;
        try { ss = EnsureShowState(root, out a1); } catch (Exception e) { ss = "fail:" + e.Message; a1 = false; }
        try { anch = EnsureAnchors(root, out a4); } catch (Exception e) { anch = "fail:" + e.Message; a4 = false; }
        try { ma = EnsureModelAnimator(root, out a2); } catch (Exception e) { ma = "fail:" + e.Message; a2 = false; }
        try { nmo = EnsureNavObstacle(root, out a3); } catch (Exception e) { nmo = "fail:" + e.Message; a3 = false; }
        added = a1 || a2 || a3 || a4;
        return "ShowState=" + ss + " ModelAnimator=" + ma + " NavMeshObstacle=" + nmo
             + " HeadBar/CenterPoint=" + anch;
    }

    /// <summary>ShowState：缺则 AddComponent（interop AddComponent(Type)，原生类型即建即活）；
    /// 空桩 ctor 不建表 ⇒ States/SpecChilds 补非空空表（null 表会让官方 ChangeState 遍历 NRE）。</summary>
    private static string EnsureShowState(GameObject root, out bool added)
    {
        added = false;
        var t = Il2CppType.Of<ShowState>();
        var comp = root.GetComponent(t)?.TryCast<ShowState>();
        if (comp == null)
        {
            comp = root.AddComponent(t).TryCast<ShowState>();
            added = true;
        }
        if (comp.States == null)
            comp.States = new Il2CppSystem.Collections.Generic.List<StateChild>();   // 空表：ChangeState 落空=无伤
        if (comp.SpecChilds == null)
            comp.SpecChilds = new Il2CppSystem.Collections.Generic.List<SpecChild>();
        return "ok";
    }

    /// <summary>ModelAnimator：缺则 AddComponent；回填 HeadBar/CenterPoint 锚引用 + Renderers 激活清单
    ///（State01 组，Instantiate 克隆时原生重映射到克隆体节点）+ Skinneds 非空空表。
    /// AnimatorPlayer/FlashLight 无源可填（程序化网格无 Animator/闪光件）——缺口入 trace（partial）。</summary>
    private static string EnsureModelAnimator(GameObject root, out bool added)
    {
        added = false;
        var t = Il2CppType.Of<ModelAnimator>();
        var comp = root.GetComponent(t)?.TryCast<ModelAnimator>();
        if (comp == null)
        {
            comp = root.AddComponent(t).TryCast<ModelAnimator>();
            added = true;
        }
        if (comp.HeadBar == null)
            comp.HeadBar = CreateOrFindAnchor(root, "HeadBar", HeadBarAnchorY);
        if (comp.CenterPoint == null)
            comp.CenterPoint = CreateOrFindAnchor(root, "CenterPoint", CenterPointAnchorY);
        if (comp.Renderers == null)
        {
            var list = new Il2CppSystem.Collections.Generic.List<MeshRenderer>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                if (r != null) list.Add(r);
            comp.Renderers = list;
        }
        if (comp.Skinneds == null)
            comp.Skinneds = new Il2CppSystem.Collections.Generic.List<SkinnedMeshRenderer>();
        return "ok(AnimatorPlayer=null)";            // 部分契约明示：官方动画链对我方模型无源，fallback 兜底
    }

    /// <summary>NavMeshObstacle：缺则 AddComponent 并按根罩体同 footprint 预配置（Box 0.9×1.9×0.9@y0.95、
    /// carving=false）；已在=零写（运行时开合归官方 ApplyTowerDeviceNavObstacle）。</summary>
    private static string EnsureNavObstacle(GameObject root, out bool added)
    {
        added = false;
        var t = Il2CppType.Of<NavMeshObstacle>();
        var comp = root.GetComponent(t)?.TryCast<NavMeshObstacle>();
        if (comp != null) return "ok";
        comp = root.AddComponent(t).TryCast<NavMeshObstacle>();
        added = true;
        comp.shape = NavMeshObstacleShape.Box;
        comp.size = new Vector3(0.9f, 1.9f, 0.9f);
        comp.center = new Vector3(0f, CenterPointAnchorY, 0f);
        comp.carving = false;
        return "ok";
    }

    /// <summary>锚点子节点：按名查（幂等），缺则建空 GO 摆到位（HeadBar y≈2.1 / CenterPoint y≈0.95）。</summary>
    private static string EnsureAnchors(GameObject root, out bool added)
    {
        added = false;
        var head = CreateOrFindAnchor(root, "HeadBar", HeadBarAnchorY, ref added);
        var center = CreateOrFindAnchor(root, "CenterPoint", CenterPointAnchorY, ref added);
        return head != null && center != null ? "ok" : "fail:anchor-null";
    }

    /// <summary>按名找锚点，缺则新建空 GO（localPos=(0,y,0)）。新建时把 added 置 true（修复路径 trace 用）。
    /// 无 ref 重载供 ModelAnimator 回填引用时复用（彼时 added 语义归组件本身）。</summary>
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

    private static GameObject CreateOrFindAnchor(GameObject root, string name, float y)
    {
        var dummy = false;
        return CreateOrFindAnchor(root, name, y, ref dummy);
    }

    // ── 图元/网格小工具 ────────────────────────────────────────────────────────────

    private static Vector3 P(float x, float y, float z) => new(x, y, z);
    private static Vector3 E(float x, float y, float z) => new(x, y, z);

    /// <summary>全后代结构件数（M1：root.childCount 不含 Head 内 21 件，报告/trace 口径统一用全后代）。</summary>
    private static int CountDescendants(Transform t)
    {
        var n = 0;
        for (var i = 0; i < t.childCount; i++)
        {
            n++;
            n += CountDescendants(t.GetChild(i));
        }
        return n;
    }

    /// <summary>Cube 图元（去 Collider，localScale=尺寸，贴共享色/角色材质；木/瓦角色按位置加 ±3% 亮度桶
    /// 抖动破均匀感）。internal=T8 箭矢模板复用。</summary>
    internal static GameObject Box(Vector3 size, Vector3 pos, Color color, Transform parent,
        Vector3? euler = null, TowerTextures.Role role = TowerTextures.Role.Flat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localScale = size;
        if (euler.HasValue) tr.localEulerAngles = euler.Value;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(Varied(color, pos, role), role);
        return go;
    }

    /// <summary>Sphere 图元（去 Collider，localScale=直径；纯色装饰件，不走贴图角色）。</summary>
    private static GameObject Ball(float diameter, Vector3 pos, Color color, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localScale = new Vector3(diameter, diameter, diameter);
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(color);
        return go;
    }

    /// <summary>木/瓦角色的部件色抖动（W3B）：VarBucket(位置) → ±3% 亮度乘数；Flat（铁/石/旗/羽）原样。
    /// 同件恒同桶（位置不变）=模板重建幂等；邻近件大概率异桶=破 94 块同色的均匀感。</summary>
    private static Color Varied(Color c, Vector3 p, TowerTextures.Role role)
    {
        if (role == TowerTextures.Role.Flat) return c;
        var mul = 1f + (ArrowTowerFxMath.VarBucket(p.x, p.y, p.z) - 1) * 0.03f;
        return new Color(c.r * mul, c.g * mul, c.b * mul, c.a);
    }

    /// <summary>4 棱锥手写网格（5 顶点/基座方角在对角线；面片双面各发一套顶点防绕向误判——
    /// 顶点极少量翻倍，免除 Unity 顺/逆时针绕向规则的歧义风险）。用于塔顶与箭头锥（T8 箭矢模板
    /// 经 internal 复用，免重复手写网格）。轴向：锥沿 +y（euler 转位）；箭头处传 E(0,0,-90) 使 +y → +x
    ///（塔载上膛箭）/ E(90,0,0) 使 +y → +z（T8 飞行箭沿 +z 朝前）。fixwave3 W3B：补 UV（每侧面三角
    /// (0,0)(1,0)(0.5,1)，图元自带 cube UV 对锥面无意义）——塔顶 Role.Roof 挂瓦纹图必需；箭头锥
    /// Flat 不受影响。</summary>
    internal static void Pyramid(float radius, float height, Vector3 pos, Color color, Transform parent,
        Vector3 euler, TowerTextures.Role role = TowerTextures.Role.Flat)
    {
        var s = radius * 0.70710678f;                    // r=对角半径 → 方底半边
        var v0 = new Vector3(s, 0, s); var v1 = new Vector3(s, 0, -s);
        var v2 = new Vector3(-s, 0, -s); var v3 = new Vector3(-s, 0, s);
        var apex = new Vector3(0, height, 0);
        var side = new[] { (v0, v1), (v1, v2), (v2, v3), (v3, v0) };
        var verts = new List<Vector3>(8);
        var uvs = new List<Vector2>(8);
        var tris = new List<int>(24);
        for (var pass = 0; pass < 2; pass++)             // 双绕向两套顶点（set0 正绕/set1 反绕）
        {
            var b = verts.Count;
            foreach (var (a, c) in side)
            {
                verts.Add(pass == 0 ? a : c);
                verts.Add(pass == 0 ? c : a);
                verts.Add(apex);
                uvs.Add(new Vector2(0f, 0f));            // 每侧面同一 UV 三角：底两角 (0,0)(1,0)，锥尖 (0.5,1)
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(0.5f, 1f));
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                b += 3;
            }
        }
        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // 借图元拿现成 MeshFilter/MeshRenderer
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localEulerAngles = euler;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(Varied(color, pos, role), role);
    }

    /// <summary>红燕尾旗手写网格（燕尾缺口在旗梢；双面；pivot=旗杆轴 → 「Flag」子物体供 T8 摆动）。</summary>
    private static void Flag(Vector3 pos, Color color, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // 借图元拿现成 MeshFilter/MeshRenderer
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = "Flag";                                  // T8 摆动锚点名（CreatePrimitive 默认名是 "Cube"）
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localScale = Vector3.one;

        Vector3 a = new(0, 0.07f, 0), b = new(0.28f, 0.07f, 0), c = new(0.225f, 0, 0),
                d = new(0.28f, -0.07f, 0), e = new(0, -0.07f, 0);
        var fan = new[] { (a, b, c), (a, c, d), (a, d, e) };
        var verts = new List<Vector3>(10);
        var tris = new List<int>(18);
        for (var pass = 0; pass < 2; pass++)             // 双绕向两套顶点（同 Pyramid 手法）
        {
            var baseI = verts.Count;
            foreach (var (p1, p2, p3) in fan)
            {
                verts.Add(p1);
                verts.Add(pass == 0 ? p2 : p3);
                verts.Add(pass == 0 ? p3 : p2);
                tris.Add(baseI); tris.Add(baseI + 1); tris.Add(baseI + 2);
                baseI += 3;
            }
        }
        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(color);
    }

    // ── 材质（7 档共享色材质；底材质三级降级链，见类头；fixwave3 W3B 起按角色挂程序化贴图）──

    private static Material Mat(Color color) => Mat(color, TowerTextures.Role.Flat);

    /// <summary>逐色+逐角色克隆材质（fixwave3 W3B）：缓存键=(色, 贴图角色)——木/瓦角色挂 TowerTextures
    /// 亮度图（三档木色经 _BaseColor 乘色共用一张木纹图）并按角色设 _BaseMap_ST 平铺；Flat（铁/石/旗/羽）
    /// 纯色零贴图。色抖动（±3% 亮度桶）在 Box/Ball/Pyramid 建件时拍进 color，缓存自然分桶不膨胀。</summary>
    private static Material Mat(Color color, TowerTextures.Role role)
    {
        var key = (color, role);
        if (Mats.TryGetValue(key, out var m) && m != null) return m;
        if (_baseMat == null) _baseMat = MakeBaseMaterial();
        var mat = new Material(_baseMat);                // 逐色克隆一份（互不串色）
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        ApplyTexture(mat, role);
        Mats[key] = mat;
        return mat;
    }

    /// <summary>按角色挂程序化贴图 + 平铺（W3B）：URP 走 _BaseMap（URP Lit _BaseMap × _BaseColor 同色相乘
    /// =灰度亮度图语义）；兜底 shader（Sprites/Default 等）走 mainTexture/mainTextureScale。贴图建不出
    /// （TowerTextures 返 null）=自动退纯色。首次挂图打一行 trace（材质升级重挂不打——底图引用同张）。</summary>
    private static bool _texTraced;

    private static void ApplyTexture(Material mat, TowerTextures.Role role)
    {
        try
        {
            if (role == TowerTextures.Role.Flat) return;
            var tex = TowerTextures.TexOf(role);
            if (tex == null) return;
            var st = TowerTextures.ScaleOf(role);
            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", tex);
                mat.SetTextureScale("_BaseMap", st);
                mat.SetTextureOffset("_BaseMap", Vector2.zero);
            }
            else
            {
                mat.mainTexture = tex;
                mat.mainTextureScale = st;
            }
            if (!_texTraced)
            {
                _texTraced = true;
                EndlessGuardPlugin.Trace("[Tower][tex] material textured role=" + role + " shader=" + mat.shader.name);
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower][tex] apply: " + e.Message); }
    }

    /// <summary>从旧材质反推贴图角色（W3B 材质升级重染用）：看 _BaseMap/mainTexture 引用是哪张程序图，
    /// 木纹再按 ST 平铺分大/小件组——重染后角色与平铺原样保留。</summary>
    private static TowerTextures.Role RoleOfMat(Material m)
    {
        try
        {
            if (m == null) return TowerTextures.Role.Flat;
            var tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
            if (tex == null) return TowerTextures.Role.Flat;
            if (TowerTextures.Roof != null && tex == TowerTextures.Roof) return TowerTextures.Role.Roof;
            if (TowerTextures.Wood != null && tex == TowerTextures.Wood)
            {
                var st = m.HasProperty("_BaseMap") ? m.GetTextureScale("_BaseMap") : m.mainTextureScale;
                return st.x > 1.05f ? TowerTextures.Role.PlankBig : TowerTextures.Role.PlankSmall;
            }
        }
        catch { }
        return TowerTextures.Role.Flat;
    }

    /// <summary>底材质四级降级链（F2 改版 + fixwave2 克隆源卫生化）：
    /// ①场景内已放置受追踪设备的 renderer 材质克隆（供给时刻设备必在场——AgentManager 直达，
    ///画风自动匹配）→ ②场景任意 URP renderer（FindObjectOfType，战斗场景 3D 物体兜住）→
    /// ③Shader.Find 链（SimpleLit→Lit；IL2CPP 加密 bundle 下常 null，仅留作兜）→
    /// ④Sprites/Default（内置必在，_Color 染色）。每级 trace 命中路径与 shader 名。
    /// fixwave2 源卫生（playtest 实证旧①选中电锯的 Particles/Unlit renderer → 克隆出透明幽灵塔）：
    /// 候选材质必须"不透明 Lit"——shader 名含 Particles/Unlit/Transparent 一票否决 + renderQueue&lt;2450
    ///（2450=AlphaTest 起始，透明家族整体排除）；候选设备偏好序 沙包36001 → 电锯36004 → 其余。</summary>
    private static Material MakeBaseMaterial()
    {
        // ① 已放置设备克隆（F2 核心）：迭代在场受追踪家具（偏好序 36001→36004→其余）→ 候选 renderer
        //    打分取最优 → 克隆去贴图/强不透明四件套（CloneStripped）
        try
        {
            if (GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated)
            {
                var all = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent();
                if (all != null)
                {
                    Renderer bestR = null;
                    int bestCfg = 0, bestPref = int.MaxValue;
                    for (int i = 0; i < all.Count; i++)
                    {
                        Furniture f = null;
                        try
                        {
                            var a = all[i];
                            if (a == null || a.IsDead) continue;
                            f = a.TryCast<Furniture>();
                        }
                        catch { continue; }   // 单代理病态不拖垮取材
                        if (f == null || !DeviceXpMath.IsTrackedDevice(f.AgentConfigId)) continue;
                        var baseCfg = DeviceXpMath.BaseCfgOf(f.AgentConfigId);
                        var pref = CloneSourcePref(baseCfg);
                        if (pref >= bestPref) continue;                 // 已有更优设备，不再深扫
                        var r = FirstCloneSourceRenderer(TransformOf(f)?.transform);
                        if (r == null) continue;
                        bestR = r; bestCfg = baseCfg; bestPref = pref;  // 偏好序打分（同分后见者不越位）
                    }
                    if (bestR != null)
                    {
                        var m = CloneStripped(bestR.sharedMaterial);
                        EndlessGuardPlugin.Trace("[Tower] material cloned from placed device cfg=" + bestCfg
                                                 + " shader=" + bestR.sharedMaterial.shader.name
                                                 + " queue=" + bestR.sharedMaterial.renderQueue);
                        return m;
                    }
                }
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] mat device-clone: " + e.Message); }

        // ② 场景任意 URP renderer（旧路径，保留为次级；fixwave2 同样过不透明 Lit 闸）
        try
        {
            var r = Object.FindObjectOfType(Il2CppType.Of<Renderer>(), false)?.TryCast<Renderer>();
            var src = r != null ? r.sharedMaterial : null;
            if (IsCloneSourceMaterial(src))
            {
                var m = CloneStripped(src);
                EndlessGuardPlugin.Trace("[Tower] material cloned from scene renderer (shader=" + src.shader.name + ")");
                return m;
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] mat clone fallback: " + e.Message); }

        // ③ Shader.Find 链（IL2CPP 下 URP shader 常不可按名查——加密 bundle，实证多为 null）
        foreach (var name in new[] { "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Lit" })
        {
            var sh = Shader.Find(name);
            if (sh != null)
            {
                EndlessGuardPlugin.Trace("[Tower] material fallback: Shader.Find(" + name + ")");
                return new Material(sh);
            }
        }

        // ④ 最后兜底（内置必在；EnsureGoodMaterial 会在后续供给时刻继续尝试升级）
        EndlessGuardPlugin.Trace("[Tower] material fallback: Sprites/Default（最后兜底，待场景内升级）");
        return new Material(Shader.Find("Sprites/Default"));
    }

    /// <summary>克隆源设备偏好序（fixwave2；brief 定：沙包 36001 最优，电锯 36004 身体次之，其余再后）。
    /// 返回值越小越优。</summary>
    private static int CloneSourcePref(int baseCfg) =>
        baseCfg == 36001 ? 0 : baseCfg == 36004 ? 1 : 2;

    /// <summary>透明/特效家族一票否决子串（fixwave2；shader 名不区分大小写匹配）。</summary>
    private static readonly string[] BadShaderSubstrings = { "Particles", "Unlit", "Transparent" };

    /// <summary>renderQueue 闸（fixwave2）：≥2450（Unity AlphaTest 起，Transparent 3000）一律不取。</summary>
    private const int OpaqueQueueLimit = 2450;

    /// <summary>候选克隆材质闸（fixwave2 源卫生）：URP Lit（非 Particles/Unlit/Transparent 家族）+
    /// renderQueue&lt;2450 + 可染（_BaseColor）。</summary>
    private static bool IsCloneSourceMaterial(Material m)
    {
        try
        {
            if (m == null || !IsUrpShader(m.shader) || !m.HasProperty("_BaseColor")) return false;
            var n = m.shader.name;
            foreach (var bad in BadShaderSubstrings)
                if (n.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return m.renderQueue < OpaqueQueueLimit;
        }
        catch { return false; }
    }

    /// <summary>克隆源材质并抹成可染纯色：强不透明四件套（_Surface=0 + _SrcBlend=1/_DstBlend=0/_ZWrite=1 +
    /// renderQueue=2000）+ 抹 _BaseMap/_MainTex。源已被 IsCloneSourceMaterial 闸过，此处是保险定格
    ///（克隆件无论源态一律落 Geometry 不透明）。</summary>
    private static Material CloneStripped(Material src)
    {
        var m = new Material(src);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);     // URP 不透明面
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);   // One
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 0f);   // Zero
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);       // 写深度（透明幽灵的根治）
        m.renderQueue = 2000;                                          // Geometry 队列
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null); // 抹掉源贴图，纯色可染
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
        return m;
    }

    /// <summary>受追踪家具的 Unity 根（TransformComponent.transform；组件按 Type 索引直达，
    /// DeviceXpRuntime.GetComponent 已验证模式）。</summary>
    private static TransformComponent TransformOf(Furniture f)
    {
        try
        {
            var dict = f != null ? f.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppType.From(typeof(TransformComponent)), out var raw)
                ? raw?.TryCast<TransformComponent>() : null;
        }
        catch { return null; }
    }

    /// <summary>后代第一个过克隆源闸的 renderer（深度优先；官方家具模型 renderer 挂子物体下）。
    /// fixwave2：闸=IsCloneSourceMaterial（URP Lit+不透明+可染），不再只看 URP+_BaseColor。</summary>
    private static Renderer FirstCloneSourceRenderer(Transform t)
    {
        if (t == null) return null;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c == null) continue;
            Renderer r = null;
            try
            {
                r = c.GetComponent<Renderer>();
                if (r != null && !IsCloneSourceMaterial(r.sharedMaterial)) r = null;
            }
            catch { r = null; }
            if (r != null) return r;
            var sub = FirstCloneSourceRenderer(c);
            if (sub != null) return sub;
        }
        return null;
    }
}
