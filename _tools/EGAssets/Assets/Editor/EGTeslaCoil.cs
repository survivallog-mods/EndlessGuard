using UnityEditor;
using UnityEngine;

namespace EGAssets
{
    /// <summary>批处理入口：Unity.exe -batchmode -quit -projectPath _tools/EGAssets -executeMethod EGAssets.EGTeslaCoil.BuildBundle</summary>
    public static class EGTeslaCoil
    {
        /// <summary>外部模型 → prefab + 材质 → AssetBundle（BundleOutput/endlessguard_tesla）。</summary>
        public static void BuildBundle()
        {
            Run(TeslaCoilBundle.Build);
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
            catch (System.Exception e)
            {
                Debug.LogError("[EGAssets] FAILED: " + e);
                EditorApplication.Exit(1);
            }
        }
    }
}
