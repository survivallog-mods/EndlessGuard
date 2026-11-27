using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace EGAssets
{
    /// <summary>批处理入口：Unity.exe -batchmode -quit -projectPath _tools/EGAssets -executeMethod EGAssets.EGTeslaCoil.BuildBundle
    /// （其余方法：BuildIcon 渲染图标源图、DumpParts 导出部件包围盒、FxSelfTest 特效自测出图）。</summary>
    public static class EGTeslaCoil
    {
        /// <summary>外部模型 → prefab + 材质 → AssetBundle（BundleOutput/endlessguard_tesla）。</summary>
        public static void BuildBundle()
        {
            Run(TeslaCoilBundle.Build);
        }

        /// <summary>渲染线圈专属图标的源图（_candidates/tesla-coil/icon/），再用 _tools/make_icon.py 裁成 256 图标。</summary>
        public static void BuildIcon()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TeslaCoilBundle/TeslaCoilExt.prefab");
            if (prefab == null) throw new Exception("prefab 缺失");
            var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            EGIcon.Render(prefab, EGCoilMaterials.Apply,
                Path.Combine(repo, "_candidates", "tesla-coil", "icon", "eg_tesla_coil_src.png"), 1024, 40f, 34f);
            EditorApplication.Exit(0);
        }

        /// <summary>导出 prefab 各部件的包围盒与按高度的半径（特效锚点的数值来源）。</summary>
        public static void DumpParts()
        {
            Run(EGTeslaCoilParts.Dump);
        }

        /// <summary>特效离线自测：几何断言 + 标定相机出审核图。</summary>
        public static void FxSelfTest()
        {
            Run(EGFxSelfTest.Run);
        }

        private static void Run(System.Action action)
        {
            try
            {
                action();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[EGAssets] FAILED: " + e);
                EditorApplication.Exit(1);
            }
        }
    }
}
