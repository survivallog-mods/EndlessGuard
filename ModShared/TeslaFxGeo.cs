using System;
using System.Collections.Generic;
using UnityEngine;

namespace SurvivalLog.ModShared
{
    /// <summary>
    /// 特效通用几何（mod 运行时与 Unity 离线自测共用同一份源码，保证「本地审的图 = 游戏里画的几何」）。
    /// 只依赖 UnityEngine 核心类型，不依赖 interop/游戏类型。块级 namespace：自测工程 Unity 侧为 C# 9，
    /// 不支持文件级 namespace。
    /// 模型锚点=线圈 prefab 局部坐标：高 4.1m、根在原点、+Y 向上（数值取自 prefab 各部件包围盒实测）。
    /// </summary>
    public static class TeslaFxGeo
    {
        public const float BallY = 3.66f;    // 电极球心（半径 0.5）
        public const float BallR = 0.52f;    // 球体闪白壳半径（略大于球面）
        public const float RingY = 2.28f;    // 环组中心高度（线宽的像素密度、自测裁剪中心按这里取）
        public const float BodyY = 0.80f;    // 塔身中心（y 0.2~1.4，半径 0.33）
        public const float BodyH = 1.25f;
        public const float BodyR = 0.35f;

        public static float Range(System.Random rng, float a, float b)
        {
            return a + (b - a) * (float)rng.NextDouble();
        }

        /// <summary>折线细化：每段三等分，两个内分点沿垂直于该段的方向随机位移——给折线加小尺度锯齿
        ///（像素闪电的毛刺感），否则是干净的几何多边形。</summary>
        public static List<Vector3> Roughen(System.Random rng, List<Vector3> pts, float amp)
        {
            var o = new List<Vector3>(pts.Count * 3);
            for (var i = 0; i < pts.Count - 1; i++)
            {
                var p = pts[i];
                var d = pts[i + 1] - p;
                if (d.sqrMagnitude < 1e-8f) continue;
                var dir = d.normalized;
                var p1 = Vector3.Cross(dir, Vector3.up);
                if (p1.sqrMagnitude < 1e-4f) p1 = Vector3.Cross(dir, Vector3.right);
                p1.Normalize();
                var p2 = Vector3.Cross(dir, p1).normalized;
                o.Add(p);
                for (var k = 1; k <= 2; k++)
                    o.Add(p + d * (k / 3f) + p1 * Range(rng, -amp, amp) + p2 * Range(rng, -amp, amp));
            }
            o.Add(pts[pts.Count - 1]);
            return o;
        }

        /// <summary>闪电折线 a→b：中点位移细分 levels 次，位移幅度=长度×ampRatio 起步、逐级 ×0.68。
        /// 6 级=65 个点，各级都有与段长可比的位移，粗折角和细碎棱角并存——降得太快（0.6/4 级）只剩几个大弯，像根弯电线。</summary>
        public static List<Vector3> Bolt(System.Random rng, Vector3 a, Vector3 b, int levels, float ampRatio)
        {
            var pts = new List<Vector3> { a, b };
            var disp = Vector3.Distance(a, b) * ampRatio;
            for (var l = 0; l < levels; l++)
            {
                var next = new List<Vector3>(pts.Count * 2);
                for (var i = 0; i < pts.Count - 1; i++)
                {
                    var p = pts[i];
                    var q = pts[i + 1];
                    var dir = (q - p).normalized;
                    var p1 = Vector3.Cross(dir, Vector3.up);
                    if (p1.sqrMagnitude < 1e-4f) p1 = Vector3.Cross(dir, Vector3.right);
                    p1.Normalize();
                    var p2 = Vector3.Cross(dir, p1).normalized;
                    next.Add(p);
                    next.Add((p + q) * 0.5f
                             + p1 * Range(rng, -1f, 1f) * disp
                             + p2 * Range(rng, -1f, 1f) * disp * 0.7f);
                }
                next.Add(pts[pts.Count - 1]);
                pts = next;
                disp *= 0.68f;
            }
            return pts;
        }

        /// <summary>链电路径：起点=游戏实际开火目标（无则离塔座最近者），其后每次跳到「上一目标 hop 米内
        /// 最近的未选目标」，最多 max 个——是逐只串联，不是从塔顶各拉一条。</summary>
        public static List<Vector3> Chain(IList<Vector3> bodies, Vector3 towerPos, Vector3? first,
            float hop, int max)
        {
            var path = new List<Vector3>();
            if (bodies == null || bodies.Count == 0) return path;
            var used = new bool[bodies.Count];
            var cur = -1;
            if (first.HasValue) cur = Nearest(bodies, used, first.Value, float.MaxValue);
            if (cur < 0) cur = Nearest(bodies, used, towerPos, float.MaxValue);
            while (cur >= 0 && path.Count < max)
            {
                used[cur] = true;
                path.Add(bodies[cur]);
                cur = Nearest(bodies, used, bodies[cur], hop);
            }
            return path;
        }

        private static int Nearest(IList<Vector3> bodies, bool[] used, Vector3 from, float maxDist)
        {
            var best = -1;
            var bestD = maxDist * maxDist;
            for (var i = 0; i < bodies.Count; i++)
            {
                if (used[i]) continue;
                var d = (bodies[i] - from).sqrMagnitude;
                if (d >= bestD) continue;
                bestD = d;
                best = i;
            }
            return best;
        }

        /// <summary>闪电带网格：世界坐标顶点。每点 8 顶点（两组互相垂直的面 × 各 4 列），每段 36 索引。
        /// uv.x=0/0.34/0.66/1 供 shader 做横截面衰减（中间实、两边虚）。两组面保证任意视角不退化成线。</summary>
        public static Mesh Ribbon(Mesh mesh, List<Vector3> pts, Vector3 camPos, float wStart, float wEnd)
        {
            var n = pts.Count;
            var verts = new Vector3[n * 8];
            var uvs = new Vector2[n * 8];
            var tris = new int[(n - 1) * 36];
            for (var i = 0; i < n; i++)
            {
                var tangent = i == 0 ? pts[1] - pts[0]
                    : i == n - 1 ? pts[i] - pts[i - 1] : pts[i + 1] - pts[i - 1];
                if (tangent.sqrMagnitude < 1e-10f) tangent = Vector3.up;
                tangent.Normalize();
                var view = camPos - pts[i];
                if (view.sqrMagnitude < 1e-6f) view = Vector3.forward;
                view.Normalize();
                var s1 = Vector3.Cross(tangent, view);
                if (s1.sqrMagnitude < 1e-6f) s1 = Vector3.Cross(tangent, Vector3.up);
                if (s1.sqrMagnitude < 1e-6f) s1 = Vector3.right;
                s1.Normalize();
                var s2 = Vector3.Cross(tangent, s1);
                if (s2.sqrMagnitude < 1e-6f) s2 = Vector3.up;
                s2.Normalize();
                var t = n > 1 ? (float)i / (n - 1) : 0f;
                var w = Mathf.Lerp(wStart, wEnd, Mathf.Sqrt(t)) * 0.5f;
                if (w < 1e-5f) w = 1e-5f;
                var b = i * 8;
                verts[b] = pts[i] - s1 * w;
                verts[b + 1] = pts[i] - s1 * w * 0.30f;
                verts[b + 2] = pts[i] + s1 * w * 0.30f;
                verts[b + 3] = pts[i] + s1 * w;
                verts[b + 4] = pts[i] - s2 * w;
                verts[b + 5] = pts[i] - s2 * w * 0.30f;
                verts[b + 6] = pts[i] + s2 * w * 0.30f;
                verts[b + 7] = pts[i] + s2 * w;
                uvs[b] = new Vector2(0f, 0.5f);
                uvs[b + 1] = new Vector2(0.34f, 0.5f);
                uvs[b + 2] = new Vector2(0.66f, 0.5f);
                uvs[b + 3] = new Vector2(1f, 0.5f);
                uvs[b + 4] = new Vector2(0f, 0.5f);
                uvs[b + 5] = new Vector2(0.34f, 0.5f);
                uvs[b + 6] = new Vector2(0.66f, 0.5f);
                uvs[b + 7] = new Vector2(1f, 0.5f);
            }
            for (var i = 0; i < n - 1; i++)
            {
                var a = i * 8;
                var c = a + 8;
                var k = i * 36;
                for (var q = 0; q < 3; q++)
                {
                    var p0 = a + q;
                    var p1 = c + q;
                    tris[k + q * 6] = p0;
                    tris[k + q * 6 + 1] = p0 + 1;
                    tris[k + q * 6 + 2] = p1 + 1;
                    tris[k + q * 6 + 3] = p0;
                    tris[k + q * 6 + 4] = p1 + 1;
                    tris[k + q * 6 + 5] = p1;
                }
                var k2 = k + 18;
                for (var q = 0; q < 3; q++)
                {
                    var p0 = a + 4 + q;
                    var p1 = c + 4 + q;
                    tris[k2 + q * 6] = p0;
                    tris[k2 + q * 6 + 1] = p0 + 1;
                    tris[k2 + q * 6 + 2] = p1 + 1;
                    tris[k2 + q * 6 + 3] = p0;
                    tris[k2 + q * 6 + 4] = p1 + 1;
                    tris[k2 + q * 6 + 5] = p1;
                }
            }
            if (mesh == null) mesh = new Mesh { name = "TeslaRibbon" };
            else mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
