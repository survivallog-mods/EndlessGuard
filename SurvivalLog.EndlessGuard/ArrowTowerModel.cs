using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔程序化模型（spec §4.3 v2 精致版，作者 2026-10-30 定案）：内存拼装木质望塔 prefab，
/// 注册名 = 家具行 ResName「P_Tripo05」（Task 6 注入），总高 ≈1.9、占地 1×1（=游戏 1 格）。
/// 零外部资产/零 AssetBundle/零许可；材质运行时取得（克隆已放置设备 renderer 的 URP 材质改色，
/// 不可达则 Shader.Find URP SimpleLit 兜底，再兜 Sprites/Default——三级降级链，见 MakeBaseMaterial）。
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
/// 模板/实例均无 Collider、无 Rigidbody、无 Animator 等任何与家具 FSM 相争的组件（纯 MeshFilter/
/// MeshRenderer）。被移除时官方归还路径 AddCache 会因 objDict 无此 obj 而直接 DestroyImmediate
/// ——等于干净销毁，无泄漏。
/// T8 动画锚点：弩机整件为根下名为「Head」的直接子物体（旋转部，pivot 在平台中央）；旗子为
/// 「Flag」直接子物体（pivot 在旗杆轴）——T8 只动这两个。
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
    private static Material _baseMat;                    // 三级降级链产物（见 MakeBaseMaterial）
    private static readonly Dictionary<Color, Material> Mats = new();
    private static bool _built, _failTraced, _suppliedTraced;

    // ════════════════════════ 注册（EnsureRegistered + Harmony 补丁）════════════════════════

    /// <summary>幂等注册：建一次模板 + 核验三 postfix 是否在挂（ArrowTower.OnStep 2s 巡检调用；
    /// 失败静默重试（仅首次 trace），成功即稳态零开销）。</summary>
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
            EndlessGuardPlugin.Trace("[Tower] model prefab built (parts=" + CountDescendants(Template.transform)
                                     + ", via=" + via + ")");
        }
        catch (Exception e)
        {
            if (!_failTraced)
            {
                _failTraced = true;
                EndlessGuardPlugin.Trace("[Tower] EnsureRegistered: " + e.Message + "（下轮重试）");
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

    /// <summary>补发核心：仅当 path 命中箭塔名且原结果为 null（=官方缓存/资源双 miss，disasm 实证
    /// 此态返 null 不抛）时，Instantiate 模板并镜像官方命中路径的后处理。任何异常→原样放行官方值。</summary>
    private static GameObject Supply(string path, GameObject fallback, GameObject root, bool worldStays,
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
            if (!_built) EnsureRegistered();
            if (Template == null) return fallback;

            var instObj = Object.Instantiate(Template);
            inst = instObj == null ? null : instObj.TryCast<GameObject>();
            if (inst == null) return fallback;
            inst.SetActive(true);                                            // 镜像官方命中路径（SetActive(true)）
            if (root != null)
            {
                inst.transform.SetParent(root.transform, worldStays);        // 重载1 命中路径镜像
                if (localPos.HasValue) inst.transform.localPosition = localPos.Value;       // 重载2 镜像
                if (localEuler.HasValue) inst.transform.localEulerAngles = localEuler.Value;
                if (localScale.HasValue) inst.transform.localScale = localScale.Value;
            }
            if (!_suppliedTraced)
            {
                _suppliedTraced = true;
                // 运行时自证（C1 口径）：本行在 postfix 内打印≠回写成功；ref 回写由 VerifyPatches 静态核验，
                // 实机终证=trace 的 instanceID 对应物出现在场景（而非官方兜底盒）。
                EndlessGuardPlugin.Trace("[Tower] model supplied on first miss: " + path
                                         + " instanceID=" + inst.GetInstanceID());
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

    // ══════════════════════════════ 模板拼装（spec §4.3 结构件清单）═════════════════════════════

    /// <summary>自底向上拼塔（约 92 件，全 Cube/Sphere 图元去 Collider + 手写 4 棱锥/燕尾旗网格）。
    /// 静态件全为根直接子物体；旋转部弩机为子物体「Head」；旗子为「Flag」。</summary>
    private static void BuildTemplate()
    {
        Template = new GameObject(ArrowTower.ResName);
        Template.SetActive(false);                       // 模板永不直发（Supply 每次派发 clone 并转正）
        Object.DontDestroyOnLoad(Template);
        var t = Template.transform;

        // ── 石砌基座 y0-0.28（0.92 方主体 + 0.98 压顶沿 + 四角石块 0.16）──
        Box(new Vector3(0.92f, 0.22f, 0.92f), P(0, 0.11f, 0), StoneLight, t);
        Box(new Vector3(0.98f, 0.06f, 0.98f), P(0, 0.25f, 0), StoneDark, t);
        foreach (var (x, z) in new[] { (-0.38f, -0.38f), (0.38f, -0.38f), (-0.38f, 0.38f), (0.38f, 0.38f) })
            Box(new Vector3(0.16f, 0.28f, 0.16f), P(x, 0.14f, z), StoneDark, t);

        // ── 木塔身三段收分 y0.28-1.07（宽 0.76/0.70/0.64 交替深浅）──
        Box(new Vector3(0.76f, 0.26f, 0.76f), P(0, 0.41f, 0), WoodMid, t);
        Box(new Vector3(0.70f, 0.26f, 0.70f), P(0, 0.67f, 0), WoodLight, t);
        Box(new Vector3(0.64f, 0.27f, 0.64f), P(0, 0.935f, 0), WoodMid, t);
        foreach (var (x, z) in new[] { (-0.35f, -0.35f), (0.35f, -0.35f), (-0.35f, 0.35f), (0.35f, 0.35f) })
            Box(new Vector3(0.06f, 0.79f, 0.06f), P(x, 0.675f, z), WoodDark, t);          // 四角柱
        Box(new Vector3(0.80f, 0.04f, 0.80f), P(0, 0.54f, 0), WoodDark, t);               // 段间横带×2
        Box(new Vector3(0.74f, 0.04f, 0.74f), P(0, 0.80f, 0), WoodDark, t);
        Box(new Vector3(0.045f, 0.66f, 0.075f), P(-0.385f, 0.675f, 0), WoodDark, t, E(40, 0, 0));   // 背面 X 斜撑
        Box(new Vector3(0.045f, 0.66f, 0.075f), P(-0.385f, 0.675f, 0), WoodDark, t, E(-40, 0, 0));
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.46f, 0), IronDark, t);         // 正面射孔×3
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.70f, 0), IronDark, t);
        Box(new Vector3(0.035f, 0.10f, 0.04f), P(0.385f, 0.90f, 0), IronDark, t);
        Box(new Vector3(0.03f, 0.72f, 0.045f), P(-0.40f, 0.64f, -0.09f), WoodDark, t);    // 背面爬梯（2 轨）
        Box(new Vector3(0.03f, 0.72f, 0.045f), P(-0.40f, 0.64f, 0.09f), WoodDark, t);
        for (var i = 0; i < 7; i++)                                                       // 7 踏
            Box(new Vector3(0.03f, 0.035f, 0.16f), P(-0.40f, 0.32f + i * 0.10f, 0), WoodLight, t);

        // ── 挑台托架×4 + 平台 y1.11-1.19（1.04 见方，挑出塔身）──
        foreach (var (x, z) in new[] { (-0.40f, -0.40f), (0.40f, -0.40f), (-0.40f, 0.40f), (0.40f, 0.40f) })
            Box(new Vector3(0.16f, 0.05f, 0.16f), P(x, 1.095f, z), WoodDark, t);
        Box(new Vector3(1.04f, 0.08f, 1.04f), P(0, 1.15f, 0), WoodLight, t);

        // ── 垛口护栏 y1.23-1.44（四边矮墙 0.1 高 + 每边 5 垛齿 + 檐角圆球×4）──
        Box(new Vector3(1.04f, 0.10f, 0.06f), P(0, 1.28f, -0.49f), WoodMid, t);
        Box(new Vector3(1.04f, 0.10f, 0.06f), P(0, 1.28f, 0.49f), WoodMid, t);
        Box(new Vector3(0.06f, 0.10f, 0.92f), P(-0.49f, 1.28f, 0), WoodMid, t);
        Box(new Vector3(0.06f, 0.10f, 0.92f), P(0.49f, 1.28f, 0), WoodMid, t);
        var teeth = new[] { -0.416f, -0.208f, 0f, 0.208f, 0.416f };                       // 每边 5 垛齿
        foreach (var o in teeth)
        {
            Box(new Vector3(0.10f, 0.11f, 0.06f), P(o, 1.385f, -0.49f), WoodDark, t);
            Box(new Vector3(0.10f, 0.11f, 0.06f), P(o, 1.385f, 0.49f), WoodDark, t);
            Box(new Vector3(0.06f, 0.11f, 0.10f), P(-0.49f, 1.385f, o), WoodDark, t);
            Box(new Vector3(0.06f, 0.11f, 0.10f), P(0.49f, 1.385f, o), WoodDark, t);
        }
        foreach (var (x, z) in new[] { (-0.49f, -0.49f), (0.49f, -0.49f), (-0.49f, 0.49f), (0.49f, 0.49f) })
            Ball(0.10f, P(x, 1.45f, z), WoodLight, t);                                    // 檐角圆球×4

        // ── 四坡顶 y1.5-1.91（檐口 + 四短柱 + 4 棱锥手写网格 + 顶饰球）──
        foreach (var (x, z) in new[] { (-0.30f, -0.30f), (0.30f, -0.30f), (-0.30f, 0.30f), (0.30f, 0.30f) })
            Box(new Vector3(0.05f, 0.07f, 0.05f), P(x, 1.475f, z), WoodDark, t);          // 四短柱
        Box(new Vector3(0.92f, 0.05f, 0.92f), P(0, 1.52f, 0), Eave, t);                   // 檐口
        Pyramid(0.66f, 0.36f, P(0, 1.55f, 0), TileRed, t, E(0, 0, 0));
        // （手写网格基座方角天然落在对角线上=四坡面正对四墙，等价「Cone(4) 转45°」的意图，不再额外转）
        Ball(0.07f, P(0, 1.945f, 0), IronDark, t);                                        // 顶饰球（评审口径 0.035 取半径）

        // ── 旗杆 + 红燕尾旗（角柱外 0.52,·,0.52；Flag 子物体 T8 摆动）──
        Box(new Vector3(0.025f, 0.80f, 0.025f), P(0.52f, 1.59f, 0.52f), WoodDark, t);
        Ball(0.06f, P(0.52f, 2.01f, 0.52f), IronDark, t);
        Flag(P(0.532f, 1.86f, 0.52f), FlagRed, t);

        // ── 重型弩机 y1.28 平台中央 → 整件子物体「Head」（T8 旋转部）──
        var head = new GameObject("Head");
        head.transform.SetParent(t, false);
        head.transform.localPosition = P(0, 1.28f, 0);
        var h = head.transform;
        Box(new Vector3(0.10f, 0.10f, 0.10f), P(0, -0.04f, 0), IronDark, h);              // 枢轴柱
        Box(new Vector3(0.46f, 0.05f, 0.07f), P(0.13f, 0.025f, 0), WoodMid, h);           // 托身（+x 朝前）
        Box(new Vector3(0.05f, 0.06f, 0.075f), P(0.02f, 0.025f, 0), IronDark, h);         // 铁件
        Box(new Vector3(0.30f, 0.025f, 0.03f), P(0.20f, 0.06f, 0), IronLight, h);         // 导轨
        Box(new Vector3(0.05f, 0.045f, 0.045f), P(0.30f, 0.065f, 0), IronDark, h);        // 弓脊
        Box(new Vector3(0.03f, 0.03f, 0.24f), P(0.30f, 0.07f, 0.135f), WoodDark, h, E(-28.65f, 0, 0)); // 后掠双弓臂
        Box(new Vector3(0.03f, 0.03f, 0.24f), P(0.30f, 0.07f, -0.135f), WoodDark, h, E(28.65f, 0, 0));
        Ball(0.04f, P(0.30f, 0.127f, 0.24f), IronLight, h);                               // 铁帽×2
        Ball(0.04f, P(0.30f, 0.127f, -0.24f), IronLight, h);
        Box(new Vector3(0.008f, 0.008f, 0.48f), P(0.30f, 0.127f, 0), FletchCream, h);     // 弦
        Box(new Vector3(0.30f, 0.02f, 0.02f), P(0.16f, 0.055f, 0), ArrowShaft, h);        // 上膛箭杆
        Pyramid(0.02f, 0.06f, P(0.31f, 0.055f, 0), IronLight, h, E(0, 0, -90));           // 锥头（+x 朝前）
        Box(new Vector3(0.07f, 0.035f, 0.004f), P(0.055f, 0.055f, 0), FletchCream, h);    // 双羽
        Box(new Vector3(0.07f, 0.004f, 0.035f), P(0.055f, 0.055f, 0), FletchCream, h);
        Box(new Vector3(0.16f, 0.05f, 0.05f), P(-0.06f, 0, 0.075f), WoodDark, h);         // 侧箭匣
        var tailX = new[] { -0.10f, -0.06f, -0.02f };
        foreach (var x in tailX)
            Box(new Vector3(0.014f, 0.05f, 0.014f), P(x, 0.055f, 0.075f), ArrowShaft, h); // 露箭尾×3
        foreach (var x in tailX)
            Box(new Vector3(0.018f, 0.02f, 0.018f), P(x, 0.09f, 0.075f), FletchCream, h); // 箭尾羽帽×3
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

    /// <summary>Cube 图元（去 Collider，localScale=尺寸，贴共享色材质）。internal=T8 箭矢模板复用。</summary>
    internal static GameObject Box(Vector3 size, Vector3 pos, Color color, Transform parent, Vector3? euler = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localScale = size;
        if (euler.HasValue) tr.localEulerAngles = euler.Value;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(color);
        return go;
    }

    /// <summary>Sphere 图元（去 Collider，localScale=直径）。</summary>
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

    /// <summary>4 棱锥手写网格（5 顶点/基座方角在对角线；面片双面各发一套顶点防绕向误判——
    /// 顶点极少量翻倍，免除 Unity 顺/逆时针绕向规则的歧义风险）。用于塔顶与箭头锥（T8 箭矢模板
    /// 经 internal 复用，免重复手写网格）。轴向：锥沿 +y（euler 转位）；箭头处传 E(0,0,-90) 使 +y → +x
    ///（塔载上膛箭）/ E(90,0,0) 使 +y → +z（T8 飞行箭沿 +z 朝前）。</summary>
    internal static void Pyramid(float radius, float height, Vector3 pos, Color color, Transform parent,
        Vector3 euler)
    {
        var s = radius * 0.70710678f;                    // r=对角半径 → 方底半边
        var v0 = new Vector3(s, 0, s); var v1 = new Vector3(s, 0, -s);
        var v2 = new Vector3(-s, 0, -s); var v3 = new Vector3(-s, 0, s);
        var apex = new Vector3(0, height, 0);
        var side = new[] { (v0, v1), (v1, v2), (v2, v3), (v3, v0) };
        var verts = new List<Vector3>(8);
        var tris = new List<int>(24);
        for (var pass = 0; pass < 2; pass++)             // 双绕向两套顶点（set0 正绕/set1 反绕）
        {
            var b = verts.Count;
            foreach (var (a, c) in side)
            {
                verts.Add(pass == 0 ? a : c);
                verts.Add(pass == 0 ? c : a);
                verts.Add(apex);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                b += 3;
            }
        }
        var mesh = new Mesh();
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // 借图元拿现成 MeshFilter/MeshRenderer
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var tr = go.transform;
        tr.SetParent(parent, false);
        tr.localPosition = pos;
        tr.localEulerAngles = euler;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(color);
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

    // ── 材质（7 档共享色材质；底材质三级降级链，见类头）────────────────────────────

    private static Material Mat(Color color)
    {
        if (Mats.TryGetValue(color, out var m) && m != null) return m;
        if (_baseMat == null) _baseMat = MakeBaseMaterial();
        var mat = new Material(_baseMat);                // 逐色克隆一份（互不串色）
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        Mats[color] = mat;
        return mat;
    }

    /// <summary>底材质：①已放置设备 renderer 的 URP 材质克隆（画风自动匹配）→ ②Shader.Find
    /// URP SimpleLit → ③Sprites/Default（内置必在，_Color 染色）。实机建模板时战斗场景已加载，
    /// 一般走①或②；菜单期先建则②兜住。</summary>
    private static Material MakeBaseMaterial()
    {
        try
        {
            var r = Object.FindObjectOfType(Il2CppType.Of<Renderer>(), false)?.TryCast<Renderer>();
            var src = r != null ? r.sharedMaterial : null;
            var sh = src != null ? src.shader : null;
            if (sh != null && sh.name.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0
                && src.HasProperty("_BaseColor"))
            {
                var m = new Material(src);
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);   // 保险：强制不透明
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null); // 抹掉源贴图，纯色可染
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
                EndlessGuardPlugin.Trace("[Tower] material cloned from scene renderer (shader=" + sh.name + ")");
                return m;
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] mat clone fallback: " + e.Message); }
        var lit = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (lit != null)
        {
            EndlessGuardPlugin.Trace("[Tower] material fallback: URP SimpleLit");
            return new Material(lit);
        }
        EndlessGuardPlugin.Trace("[Tower] material fallback: Sprites/Default（最后兜底）");
        return new Material(Shader.Find("Sprites/Default"));
    }
}
