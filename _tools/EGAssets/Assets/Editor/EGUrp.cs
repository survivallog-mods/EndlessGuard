using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EGAssets
{
    /// <summary>离线工程按 URP 出图（与游戏管线一致）：确保管线资产存在并设为当前。自测出图前调用。</summary>
    internal static class EGUrp
    {
        private const string RendererPath = "Assets/EGUrpRenderer.asset";
        private const string PipelinePath = "Assets/EGUrpAsset.asset";

        internal static void Ensure()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, RendererPath);
                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
                AssetDatabase.SaveAssets();
                Debug.Log("[EGUrp] created URP asset");
            }
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
        }
    }
}
