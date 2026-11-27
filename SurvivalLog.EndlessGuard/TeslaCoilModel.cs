using System;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>磁暴线圈模型供应：拦截 BattleResource.LoadGameObject 的 P_TeslaCoil 请求，
/// 用 bundle 里的 prefab 实例化，克隆游戏 URP Lit 绑 bundle 贴图，补官方模型根契约（DeviceModel，缺了就点不选），
/// 挂攻击特效组件。</summary>
internal static class TeslaCoilModel
{
    private const float InstallScale = 0.56f;   // bundle 内 4.1m → 安装 2.3m

    /// <summary>外形参数（prefab 局部坐标，随安装缩放）：罩体罩住塔身与红板，头栏锚在球顶上方。</summary>
    private static readonly DeviceModel.Shape CoilShape =
        new(new Vector3(1.4f, 4.1f, 1.4f), new Vector3(0f, 2.05f, 0f), 4.5f, 2.0f);

    private static bool _contractTraced;

    internal static GameObject Template { get; private set; }

    private static AssetBundle _bundle;
    private static Texture2D _albedo1, _normal1, _ao1, _albedo2, _ao2;
    private static Material _urpLit;
    private static bool _loaded, _armed, _loadTraced, _matTraced;
    private static int _spawned;

    internal static void Arm() => _armed = true;

    /// <summary>Harmony postfix 进入点。返回 null=未接管（调用方还原官方结果）。</summary>
    internal static GameObject Supply(string path, GameObject root, bool worldStays,
        Vector3? localPos, Vector3? localEuler, Vector3? localScale)
    {
        if (!_armed) return null;
        if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return null;
        if (EndlessGuardPlugin.TeslaCoilEnabled != null && !EndlessGuardPlugin.TeslaCoilEnabled.Value) return null;
        try
        {
            EnsureLoaded();
            if (Template == null) return null;

            var obj = Object.Instantiate(Template);
            var inst = obj == null ? null : obj.TryCast<GameObject>();
            if (inst == null) return null;
            inst.name = "TeslaCoil";

            if (root != null)
            {
                inst.transform.SetParent(root.transform, false);
                if (localPos.HasValue) inst.transform.localPosition = localPos.Value;
                if (localEuler.HasValue) inst.transform.localEulerAngles = localEuler.Value;
                inst.transform.localScale = localScale.HasValue
                    ? localScale.Value * InstallScale
                    : Vector3.one * InstallScale;
            }
            else
            {
                if (localPos.HasValue) inst.transform.position = localPos.Value;
                if (localEuler.HasValue) inst.transform.eulerAngles = localEuler.Value;
                inst.transform.localScale = Vector3.one * InstallScale;
            }

            ApplyMaterials(inst, force: false);
            var contract = DeviceModel.Finalize(inst, CoilShape, "coil", out _);   // 官方根契约（选取/耐久/修理/藤蔓的前提）
            AttachFx(inst);                                                        // 特效壳后挂：不进 ModelAnimator.Renderers
            if (!_contractTraced)
            {
                _contractTraced = true;
                Trace("[Coil] contract: " + contract);
            }
            _spawned++;
            if (_spawned <= 3)
                Trace("[Coil] model supplied #" + _spawned + " pos=" + inst.transform.position
                      + " scale=" + inst.transform.localScale);
            return inst;
        }
        catch (Exception e)
        {
            Trace("[Coil] supply: " + e.Message);
            return null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<TeslaCoilFx>())
                ClassInjector.RegisterTypeInIl2Cpp<TeslaCoilFx>();

            var path = Path.Combine(Paths.PluginPath, "EndlessGuard", "assets", "endlessguard_tesla");
            if (!File.Exists(path))
            {
                Trace("[Coil] bundle missing: " + path);
                return;
            }
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                Trace("[Coil] LoadFromFile null: " + path);
                return;
            }

            var names = _bundle.GetAllAssetNames();
            for (var i = 0; i < names.Length; i++)
            {
                var n = names[i];
                if (!n.ToLowerInvariant().EndsWith(".prefab")) continue;
                var o = _bundle.LoadAsset(n);
                Template = o == null ? null : o.TryCast<GameObject>();
                break;
            }
            if (Template == null)
            {
                Trace("[Coil] prefab missing in bundle");
                return;
            }

            _albedo1 = LoadTex("Assets/TeslaCoilExternal/textures/07___Default_1001_albedo.jpg");
            _normal1 = LoadTex("Assets/TeslaCoilExternal/textures/07___Default_1001_normal.png");
            _ao1 = LoadTex("Assets/TeslaCoilExternal/textures/07___Default_1001_AO.jpg");
            _albedo2 = LoadTex("Assets/TeslaCoilExternal/textures/07___Default_1002_albedo.jpg");
            _ao2 = LoadTex("Assets/TeslaCoilExternal/textures/07___Default_1002_AO.jpg");
            Trace("[Coil] bundle loaded prefab=" + Template.name + " tex=" + (_albedo1 != null)
                  + "/" + (_normal1 != null) + "/" + (_ao2 != null));
        }
        catch (Exception e)
        {
            if (!_loadTraced)
            {
                _loadTraced = true;
                Trace("[Coil] load: " + e.Message);
            }
        }
    }

    internal static void ApplyMaterials(GameObject inst, bool force)
    {
        try
        {
            if (_urpLit == null || force) _urpLit = FindLit();
            if (_urpLit == null)
            {
                if (!_matTraced)
                {
                    _matTraced = true;
                    Trace("[Coil] 找不到 URP Lit 模板，保留占位材质（可能显示异常）");
                }
                return;
            }
            var rs = inst.GetComponentsInChildren<MeshRenderer>();
            for (var i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                var oldName = r.sharedMaterial != null ? r.sharedMaterial.name : "";
                var isMetal = oldName.Contains("Metal") || oldName.Contains("Ball");
                var m = new Material(_urpLit);
                var albedo = isMetal ? _albedo2 : _albedo1;
                var ao = isMetal ? _ao2 : _ao1;
                if (albedo != null) m.SetTexture("_BaseMap", albedo);
                if (ao != null)
                {
                    m.SetTexture("_OcclusionMap", ao);
                    m.EnableKeyword("_OCCLUSIONMAP");
                }
                if (isMetal)
                {
                    m.SetColor("_BaseColor", Color.white);
                    m.SetFloat("_Metallic", 0.70f);
                    m.SetFloat("_Smoothness", 0.74f);
                }
                else
                {
                    m.SetColor("_BaseColor", new Color(1.55f, 1.40f, 1.40f));
                    m.SetFloat("_Metallic", 0.10f);
                    m.SetFloat("_Smoothness", 0.45f);
                    if (_normal1 != null)
                    {
                        m.SetTexture("_BumpMap", _normal1);
                        m.EnableKeyword("_NORMALMAP");
                    }
                }
                r.sharedMaterial = m;
            }
        }
        catch (Exception e) { Trace("[Coil] material: " + e.Message); }
    }

    private static void AttachFx(GameObject inst)
    {
        try
        {
            if (inst.GetComponent<TeslaCoilFx>() != null) return;
            var fx = inst.AddComponent<TeslaCoilFx>();
            fx.Init(_bundle, inst);
        }
        catch (Exception e) { Trace("[Coil] fx: " + e.Message); }
    }

    private static Texture2D LoadTex(string name)
    {
        try
        {
            var o = _bundle.LoadAsset(name);
            return o == null ? null : o.TryCast<Texture2D>();
        }
        catch { return null; }
    }

    private static Material FindLit()
    {
        Material fallback = null;
        var rs = Object.FindObjectsOfType<Renderer>();
        for (var i = 0; i < rs.Length; i++)
        {
            var m = rs[i] != null ? rs[i].sharedMaterial : null;
            if (m == null || m.shader == null) continue;
            var sn = m.shader.name;
            if (sn == "Universal Render Pipeline/Lit") return m;
            if (fallback == null && sn != null
                && sn.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0
                && m.HasProperty("_BaseColor"))
                fallback = m;
        }
        return fallback;
    }

    private static void Trace(string s) => EndlessGuardPlugin.Trace(s);
}
