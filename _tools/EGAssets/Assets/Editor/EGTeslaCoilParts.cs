using UnityEditor;
using UnityEngine;

namespace EGAssets
{
    /// <summary>打印磁暴线圈 prefab 各 Renderer 的世界包围盒（prefab 已归一到 4.1m 高、根在原点），
    /// 用于确定球心/环组/塔身/红板在模型局部坐标里的准确区间——特效锚点不再靠估。</summary>
    internal static class EGTeslaCoilParts
    {
        internal static void Dump()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TeslaCoilBundle/TeslaCoilExt.prefab");
            if (prefab == null) { Debug.LogError("[Parts] prefab missing"); return; }
            var inst = Object.Instantiate(prefab);
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                var b = r.bounds;
                var mesh = r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null;
                var vc = mesh != null ? mesh.vertexCount : 0;
                Debug.Log("[Parts] " + r.gameObject.name
                    + " min=(" + b.min.x.ToString("F3") + "," + b.min.y.ToString("F3") + "," + b.min.z.ToString("F3") + ")"
                    + " max=(" + b.max.x.ToString("F3") + "," + b.max.y.ToString("F3") + "," + b.max.z.ToString("F3") + ")"
                    + " verts=" + vc
                    + " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "null"));
                // 按高度分层统计最大半径，定位环组
                if (mesh != null && vc > 0)
                {
                    var mv = mesh.vertices;
                    var l2w = r.transform.localToWorldMatrix;
                    var bands = new float[41];
                    for (var i = 0; i < mv.Length; i++)
                    {
                        var w = l2w.MultiplyPoint3x4(mv[i]);
                        var bi = Mathf.Clamp(Mathf.RoundToInt(w.y / 0.1f), 0, 40);
                        var rad = Mathf.Sqrt(w.x * w.x + w.z * w.z);
                        if (rad > bands[bi]) bands[bi] = rad;
                    }
                    var sb = new System.Text.StringBuilder("[Parts] radius-by-height " + r.gameObject.name + ":");
                    for (var bi = 0; bi < 41; bi++) sb.Append(" y").Append((bi * 0.1f).ToString("F1")).Append('=').Append(bands[bi].ToString("F2"));
                    Debug.Log(sb.ToString());
                }
            }
            Object.DestroyImmediate(inst);
        }
    }
}
