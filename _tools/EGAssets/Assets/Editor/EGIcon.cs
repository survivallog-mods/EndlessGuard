using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace EGAssets
{
    /// <summary>与 mod 侧 TeslaCoilModel.ApplyMaterials 同一套材质做法（克隆 URP Lit + 绑外部模型贴图），
    /// 供离线自测与图标渲染共用，保证出图即游戏内观感。</summary>
    internal static class EGCoilMaterials
    {
        internal static void Apply(GameObject model)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new Exception("URP Lit shader 缺失");
            var matRed = Lit(lit, new Color(1.55f, 1.40f, 1.40f), 0.10f, 0.45f);
            matRed.SetTexture("_BaseMap", Tex("07___Default_1001_albedo.jpg"));
            var matMetal = Lit(lit, Color.white, 0.70f, 0.74f);
            matMetal.SetTexture("_BaseMap", Tex("07___Default_1002_albedo.jpg"));
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                var n = r.sharedMaterial != null ? r.sharedMaterial.name : "";
                r.sharedMaterial = (n.Contains("Metal") || n.Contains("Ball")) ? matMetal : matRed;
            }
        }

        private static Texture2D Tex(string name)
        {
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TeslaCoilExternal/textures/" + name);
            if (t == null) throw new Exception("贴图缺失 " + name);
            return t;
        }

        private static Material Lit(Shader lit, Color c, float metallic, float smooth)
        {
            var m = new Material(lit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            return m;
        }
    }

    /// <summary>把 prefab 按物品图标的 3/4 俯视机位渲染成透明背景 PNG。官方图标（WebUI/Res/Material）是
    /// 256×256 透明底，这里出大图再由 _tools/make_icon.py 裁边缩放，边缘才有抗锯齿。</summary>
    internal static class EGIcon
    {
        /// <param name="yawDeg">模型绕竖轴的偏转（让图标同时看到两个侧面）</param>
        /// <param name="pitchDeg">相机俯角</param>
        /// <param name="prepare">实例化后、渲染前对实例的加工（绑材质等）</param>
        internal static void Render(GameObject prefab, Action<GameObject> prepare, string outFile, int size,
            float yawDeg, float pitchDeg)
        {
            EGUrp.Ensure();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.56f, 0.60f);
            RenderSettings.ambientEquatorColor = new Color(0.40f, 0.40f, 0.42f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.20f, 0.21f);
            RenderSettings.fog = false;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var inst = UnityEngine.Object.Instantiate(prefab);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            inst.transform.localScale = Vector3.one;
            if (prepare != null) prepare(inst);

            var cam = new GameObject("IconCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.allowHDR = false;
            cam.transform.rotation = Quaternion.Euler(pitchDeg, 0f, 0f);

            // 取全部渲染器包围盒的 8 个角点，在相机空间里求半宽半高 → 正交尺寸（留 8% 边距）
            var b = new Bounds(); var has = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                if (has) b.Encapsulate(r.bounds); else { b = r.bounds; has = true; }
            }
            if (!has) throw new Exception("prefab 没有渲染器");
            cam.transform.position = b.center - cam.transform.forward * 30f;
            var ext = new Vector2();
            for (var i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                var local = cam.transform.InverseTransformPoint(corner);
                ext.x = Mathf.Max(ext.x, Mathf.Abs(local.x));
                ext.y = Mathf.Max(ext.y, Mathf.Abs(local.y));
            }
            cam.orthographicSize = Mathf.Max(ext.x, ext.y) * 1.08f;

            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllBytes(outFile, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            Debug.Log("[EGAssets] icon source -> " + outFile + " (" + size + "x" + size + ")");
        }
    }
}
