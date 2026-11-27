using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace EGAssets
{
    /// <summary>把外部磁暴线圈（Ska-Ara, CC-BY）打成游戏用 AssetBundle。
    /// 贴图降到 1024/512 控体积；材质只做占位，运行时由插件克隆游戏 URP Lit 并绑这些贴图。</summary>
    internal static class TeslaCoilBundle
    {
        private const string Ext = "Assets/TeslaCoilExternal";
        private const string Dir = "Assets/TeslaCoilBundle";
        private const string BundleName = "endlessguard_tesla";
        private const string OutDir = "BundleOutput";

        internal static void Build()
        {
            ConfigureTextures();

            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "TeslaCoilBundle");
            AssetDatabase.Refresh();

            var matRed = MakeMat("EXT_Red", 0.10f, 0.45f, new Color(1.6f, 1.45f, 1.45f));
            var matMetal = MakeMat("EXT_Metal", 0.70f, 0.74f, new Color(1.08f, 1.08f, 1.08f));
            var matBall = MakeMat("EXT_Ball", 0.85f, 0.78f, Color.white);

            var boltShader = Shader.Find("EG/BoltRibbon");
            var discShader = Shader.Find("EG/UnlitAdditive");
            if (boltShader == null || discShader == null)
                throw new System.Exception("特效 shader 缺失: bolt=" + (boltShader == null) + " disc=" + (discShader == null));
            var fxCore = FXMat("Fx_BoltCore", boltShader, new Color(1f, 1f, 1f, 1f), 1.4f);
            var fxGlow = FXMat("Fx_BoltGlow", boltShader, new Color(0.42f, 0.68f, 1f, 0.55f), 1.5f);
            var fxOuter = FXMat("Fx_BoltOuter", boltShader, new Color(0.16f, 0.38f, 1f, 0.30f), 1.8f);
            var fxDisc = FXMat("Fx_Disc", discShader, Color.white, 1f);
            var bandShader = Shader.Find("EG/BoltAlpha");
            if (bandShader == null) throw new System.Exception("特效 shader 缺失: EG/BoltAlpha");
            var fxBlue = FXMat("Fx_BoltBlue", bandShader, new Color(0.22f, 0.34f, 1.00f, 0.95f), 0f);

            var dae = AssetDatabase.LoadAssetAtPath<GameObject>(Ext + "/model.dae");
            if (dae == null) throw new System.Exception("DAE 未导入: " + Ext + "/model.dae");

            var root = new GameObject("TeslaCoilExt");
            var model = Object.Instantiate(dae);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);

            var bounds = CalcBounds(model);
            var scale = 4.10f / Mathf.Max(bounds.size.y, 1e-4f);
            model.transform.localScale = Vector3.one * scale;
            model.transform.localPosition = new Vector3(-bounds.center.x * scale, -bounds.min.y * scale, -bounds.center.z * scale);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                var n = r.sharedMaterial != null ? r.sharedMaterial.name : "";
                r.sharedMaterial = n.Contains("1002") ? matMetal : matRed;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }

            var mb = new MeshBuilder();
            mb.AddSphere(0.50f, 44, 28, Matrix4x4.identity);
            var ballMesh = mb.Finish("TeslaBallCover");
            var ballPath = Dir + "/TeslaBallCover.asset";
            AssetDatabase.DeleteAsset(ballPath);
            AssetDatabase.CreateAsset(ballMesh, ballPath);
            var ball = new GameObject("BallCover");
            ball.transform.SetParent(root.transform, false);
            ball.transform.localPosition = new Vector3(0f, 3.66f, 0f);
            ball.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(ballPath);
            ball.AddComponent<MeshRenderer>().sharedMaterial = matBall;

            // 与运行时锚点一致：根在原点，正面朝 +Z
            var prefabPath = Dir + "/TeslaCoilExt.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();

            MarkBundle(Ext + "/model.dae");
            foreach (var f in Directory.GetFiles(Ext + "/textures", "*", SearchOption.TopDirectoryOnly))
                if (!f.EndsWith(".meta")) MarkBundle(f.Replace('\\', '/'));
            MarkBundle(Dir + "/TeslaBallCover.asset");
            MarkBundle(Dir + "/EXT_Red.mat");
            MarkBundle(Dir + "/EXT_Metal.mat");
            MarkBundle(Dir + "/EXT_Ball.mat");
            MarkBundle(Dir + "/Fx_BoltCore.mat");
            MarkBundle(Dir + "/Fx_BoltGlow.mat");
            MarkBundle(Dir + "/Fx_BoltOuter.mat");
            MarkBundle(Dir + "/Fx_Disc.mat");
            MarkBundle(Dir + "/Fx_BoltBlue.mat");
            MarkBundle(prefabPath);

            var outFull = Path.GetFullPath(OutDir);
            Directory.CreateDirectory(outFull);
            var manifest = BuildPipeline.BuildAssetBundles(outFull,
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null) throw new System.Exception("BuildAssetBundles 失败");

            var bundlePath = Path.Combine(outFull, BundleName);
            if (!File.Exists(bundlePath)) throw new System.Exception("bundle 未生成: " + bundlePath);
            var size = new FileInfo(bundlePath).Length;
            Debug.Log("[EGAssets] bundle=" + bundlePath + " size=" + size
                      + " assets=" + string.Join(";", manifest.GetAllAssetBundles().Select(b => b).ToArray()));
        }

        private static void ConfigureTextures()
        {
            var dir = new DirectoryInfo(Ext + "/textures");
            foreach (var f in dir.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (f.Extension == ".meta") continue;
                var path = Ext + "/textures/" + f.Name;
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                var isNormal = f.Name.Contains("normal");
                ti.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !isNormal;
                var isAlbedo = f.Name.Contains("albedo");
                ti.maxTextureSize = isAlbedo || isNormal ? 1024 : 512;
                ti.textureCompression = isAlbedo
                    ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
        }

        private static Material MakeMat(string name, float metallic, float gloss, Color tint)
        {
            var path = Dir + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            var m = new Material(Shader.Find("Standard")) { name = name };
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(Ext + "/textures/07___Default_"
                + (name.Contains("Metal") || name.Contains("Ball") ? "1002" : "1001") + "_albedo.jpg"));
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(Ext + "/textures/07___Default_"
                + (name.Contains("Metal") || name.Contains("Ball") ? "1002" : "1001") + "_normal.png");
            if (normal != null && !name.Contains("Metal") && !name.Contains("Ball"))
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }
            m.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Ext + "/textures/07___Default_"
                + (name.Contains("Metal") || name.Contains("Ball") ? "1002" : "1001") + "_AO.jpg"));
            m.SetColor("_Color", tint);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Glossiness", gloss);
            m.SetFloat("_OcclusionStrength", 0.35f);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static Material FXMat(string name, Shader shader, Color color, float power)
        {
            var path = Dir + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            var m = new Material(shader) { name = name };
            m.SetColor("_Color", color);
            m.SetFloat("_Power", power);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static void MarkBundle(string path)
        {
            var ai = AssetImporter.GetAtPath(path);
            if (ai == null)
            {
                Debug.LogWarning("[EGAssets] 无 importer: " + path);
                return;
            }
            ai.assetBundleName = BundleName;
            ai.SaveAndReimport();
        }

        private static Bounds CalcBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            for (var i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }
    }
}
