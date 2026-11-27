using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using SurvivalLog.ModShared;

namespace EGAssets
{
    /// <summary>磁暴线圈特效离线自测：跑「与 mod 同一份源码」（ModShared/TeslaFx*，由 selftest.ps1 同步到 Assets/Shared）
    /// 和「与游戏同一份材质」（Assets/TeslaCoilBundle/Fx_*.mat）+ 真实 prefab，用实机标定的相机出审核图
    /// （俯角 40°、竖向 FOV 43°、3840×2160、球心距 11.6m：按实机截图里两颗球心+塔底联立反解）：待机×12 / 警戒 / 放电 / 链电。
    /// 改外观必须先过这里再部署。</summary>
    internal static class EGFxSelfTest
    {
        private const int W = 3840, H = 2160;
        private const float Pitch = 40f;
        private const float Fov = 43f;
        private const float Dist = 11.6f;
        private const float Scale = 0.56f;      // = TeslaCoilModel.InstallScale

        internal static string OutDir()
        {
            var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            var d = Path.Combine(repo, "_candidates", "tesla-coil", "review");
            Directory.CreateDirectory(d);
            return d;
        }

        internal static void Run()
        {
            AssertGeometry();
            EGUrp.Ensure();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.43f, 0.46f);
            RenderSettings.ambientEquatorColor = new Color(0.30f, 0.30f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.14f, 0.14f, 0.15f);
            RenderSettings.fog = false;

            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new Exception("URP Lit shader 缺失");

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.localScale = Vector3.one * 3f;        // 30m×30m
            ground.GetComponent<MeshRenderer>().sharedMaterial = Lit(lit, new Color(0.30f, 0.30f, 0.31f), 0f, 0.2f);
            var gridMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            gridMat.SetColor("_BaseColor", new Color(0.16f, 0.16f, 0.17f));
            for (var i = -8; i <= 8; i++)
            {
                Line(new Vector3(0f, 0.01f, i), new Vector3(17f, 0.02f, 0.04f), gridMat);
                Line(new Vector3(i, 0.01f, 0f), new Vector3(0.04f, 0.02f, 17f), gridMat);
            }

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(52f, -34f, 0f);

            // 真实 prefab + 与插件相同的材质做法（克隆 URP Lit 绑贴图）
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TeslaCoilBundle/TeslaCoilExt.prefab");
            if (prefab == null) throw new Exception("prefab 缺失");
            var model = UnityEngine.Object.Instantiate(prefab);
            model.name = "TeslaCoil";
            model.transform.position = Vector3.zero;
            model.transform.rotation = Quaternion.Euler(0f, 15f, 0f);   // 实机截图里线圈相对机位的偏航
            model.transform.localScale = Vector3.one * Scale;
            EGCoilMaterials.Apply(model);

            // 代理丧尸（身高 1.7，躯干中心 0.9）：一部分在线圈与相机之间（z<0），一部分在后方
            var zmat = Lit(lit, new Color(0.46f, 0.50f, 0.38f), 0f, 0.3f);
            var feet = new List<Vector3>
            {
                new Vector3(1.9f, 0f, -1.3f), new Vector3(3.3f, 0f, -0.4f), new Vector3(0.6f, 0f, -2.6f),
                new Vector3(2.8f, 0f, 1.7f), new Vector3(1.2f, 0f, 2.9f), new Vector3(-2.1f, 0f, 1.1f)
            };
            foreach (var f in feet)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                DestroyImmediate(c.GetComponent<Collider>());
                c.transform.position = f + Vector3.up * 0.85f;
                c.transform.localScale = new Vector3(0.45f, 0.85f, 0.45f);
                c.GetComponent<MeshRenderer>().sharedMaterial = zmat;
            }
            var bodies = feet.ConvertAll(f => f + Vector3.up * 0.9f);

            var cam = new GameObject("ReviewCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.055f, 0.07f, 1f);
            cam.fieldOfView = Fov;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 120f;
            cam.allowHDR = false;
            cam.allowMSAA = true;
            cam.transform.rotation = Quaternion.Euler(Pitch, 0f, 0f);
            // 标定：截图里旧球心 (0,2.928,0) 距相机 11.6m
            cam.transform.position = new Vector3(0f, 2.928f, 0f) - cam.transform.forward * Dist;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            rt.Create();
            cam.targetTexture = rt;
            TeslaFxRig.SetCameraOverride(cam);

            var dir = OutDir();
            foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
            // 环组在画面里的位置：取屏幕坐标定裁剪框
            var stack = cam.WorldToScreenPoint(model.transform.TransformPoint(new Vector3(0f, TeslaFxGeo.RingY, 0f)));
            var cx = Mathf.RoundToInt(stack.x);
            var cy = Mathf.RoundToInt(H - stack.y);
            Debug.Log("[FxSelfTest] stack screen=(" + cx + "," + cy + ") px=" + TeslaFxRig.CamPos());

            var chain = TeslaFxGeo.Chain(bodies, model.transform.position, bodies[0], 3.0f, TeslaFxPainter.MaxChain);
            if (chain.Count < 3) throw new Exception("链电应至少 3 只，实际 " + chain.Count);
            var painter = new TeslaFxPainter(model.transform,
                AssetDatabase.LoadAssetAtPath<Material>("Assets/TeslaCoilBundle/Fx_BoltBlue.mat"),
                AssetDatabase.LoadAssetAtPath<Material>("Assets/TeslaCoilBundle/Fx_BoltCore.mat"),
                AssetDatabase.LoadAssetAtPath<Material>("Assets/TeslaCoilBundle/Fx_BoltGlow.mat"),
                AssetDatabase.LoadAssetAtPath<Material>("Assets/TeslaCoilBundle/Fx_Disc.mat"));
            for (var i = 1; i <= 12; i++)
            {
                painter.DrawIdle(i, i * 0.05f, 0f);
                Render(cam, rt, Path.Combine(dir, "idle_" + i.ToString("D2") + ".png"), cx - 450, cy - 330, 900, 760);
            }
            painter.DrawIdle(3, 0.15f, 0f);
            Render(cam, rt, Path.Combine(dir, "idle_full.png"), 0, 0, W, H);
            for (var i = 1; i <= 4; i++)
            {
                painter.DrawIdle(20 + i, 1f + i * 0.05f, 1f);
                Render(cam, rt, Path.Combine(dir, "alert_" + i + ".png"), cx - 450, cy - 330, 900, 760);
            }
            // 放电：6 帧（40ms 一帧，同游戏内重抖节拍）+ 2 帧衰减；近景看塔，宽景看链电
            for (var i = 1; i <= 8; i++)
            {
                painter.DrawFire(chain, 100 + i * 7, i * 0.04f, i <= 6 ? 1f : i == 7 ? 0.5f : 0.2f);
                Render(cam, rt, Path.Combine(dir, "fire_" + i + ".png"), cx - 450, cy - 420, 900, 900);
                Render(cam, rt, Path.Combine(dir, "chain_" + i + ".png"), cx - 750, cy - 480, 1500, 1000);
                if (i == 1) Render(cam, rt, Path.Combine(dir, "fire_full.png"), 0, 0, W, H);
            }
            painter.Dispose();

            TeslaFxRig.SetCameraOverride(null);
            cam.targetTexture = null;
            rt.Release();
            DestroyImmediate(rt);
            Debug.Log("[FxSelfTest] PASS chain=" + chain.Count + "; frames -> " + dir);
        }

        /// <summary>几何断言：ribbon 顶点/索引/上界随点数成立；闭合环首尾相接；环组电弧首尾落在环外缘。</summary>
        private static void AssertGeometry()
        {
            var fails = new List<string>();
            foreach (var n in new[] { 2, 3, 4, 16, 65 })
            {
                var pts = new List<Vector3>();
                for (var i = 0; i < n; i++) pts.Add(new Vector3(i * 0.1f, Mathf.Sin(i * 0.4f) * 0.2f, 0f));
                var m = TeslaFxGeo.Ribbon(null, pts, new Vector3(0f, 5f, 8f), 0.1f, 0.02f);
                if (m.vertexCount != n * 8) fails.Add("ribbon n=" + n + " verts=" + m.vertexCount);
                if (m.triangles.Length != (n - 1) * 36) fails.Add("ribbon n=" + n + " tris=" + m.triangles.Length);
                var max = 0;
                foreach (var t in m.triangles) max = Math.Max(max, t);
                if (max >= n * 8) fails.Add("ribbon n=" + n + " index " + max + " OOB");
                if (m.bounds.size.x < 0.01f) fails.Add("ribbon n=" + n + " bounds degenerate");
            }
            var loop = TeslaFxCoilArcs.Loop(new System.Random(3), 10, 0.4f, 0.9f, 0.32f, 0.62f, 1.38f, 0f);
            if (loop.Count != 11 || (loop[0] - loop[10]).sqrMagnitude > 1e-10f) fails.Add("loop not closed");
            var rng = new System.Random(5);
            for (var k = 0; k < 200; k++)
            {
                var arc = TeslaFxCoilArcs.StackArc(rng, TeslaFxGeo.Range(rng, -3f, 3f), TeslaFxGeo.Range(rng, -3f, 3f),
                    TeslaFxCoilArcs.RingYs[rng.Next(3)], TeslaFxCoilArcs.RingYs[rng.Next(3)], rng.Next(1, 5), 1.05f, 1.34f,
                    TeslaFxCoilArcs.StackBottom, TeslaFxCoilArcs.StackTop);
                if (arc.Count < 3 || arc.Count > 6) fails.Add("arc len " + arc.Count);
                foreach (var e in new[] { arc[0], arc[arc.Count - 1] })
                {
                    var r = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                    if (Mathf.Abs(r - TeslaFxCoilArcs.RingRadiusAt(e.y)) > 1e-3f) fails.Add("arc end off ring r=" + r);
                }
                for (var i = 1; i < arc.Count - 1; i++)
                    if (arc[i].y < TeslaFxCoilArcs.StackBottom || arc[i].y > TeslaFxCoilArcs.StackTop) fails.Add("arc y oob");
                var stem = TeslaFxCoilArcs.StemArc(rng, 0.3f, 2);
                if (stem.Count != 4) fails.Add("stem len " + stem.Count);
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails.ToArray()));
            Debug.Log("[FxSelfTest] geometry PASS");
        }

        private static void Render(Camera cam, RenderTexture rt, string path, int x, int y, int w, int h)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(x, H - y - h, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            DestroyImmediate(tex);
        }

        private static void Line(Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Material Lit(Shader lit, Color c, float metallic, float smooth)
        {
            var m = new Material(lit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            return m;
        }

        private static void DestroyImmediate(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
