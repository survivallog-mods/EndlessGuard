using System.Collections.Generic;
using UnityEngine;

namespace SurvivalLog.ModShared
{
    /// <summary>特效用的基础网格（不依赖贴图）：圆盘/球/圆柱。与 TeslaFxGeo 同为 mod 运行时与离线自测共用源码。</summary>
    public static class TeslaFxMeshes
    {
        /// <summary>直径 1 的圆盘，顶点色 alpha 自中心向边缘衰减（命中闪光/闪白壳用，不依赖贴图）。</summary>
        public static Mesh Disc(int rings, int seg)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            verts.Add(Vector3.zero);
            colors.Add(new Color(1f, 1f, 1f, 1f));
            uvs.Add(new Vector2(0.5f, 0.5f));
            for (var r = 1; r <= rings; r++)
            {
                var t = (float)r / rings;
                var a = Mathf.Pow(1f - t, 3f);
                for (var i = 0; i < seg; i++)
                {
                    var ang = Mathf.PI * 2f * i / seg;
                    verts.Add(new Vector3(Mathf.Cos(ang) * t * 0.5f, Mathf.Sin(ang) * t * 0.5f, 0f));
                    colors.Add(new Color(1f, 1f, 1f, a));
                    uvs.Add(new Vector2(0.5f, 0.5f));
                }
            }
            for (var i = 0; i < seg; i++)
            {
                tris.Add(0);
                tris.Add(1 + i);
                tris.Add(1 + (i + 1) % seg);
            }
            for (var r = 0; r < rings - 1; r++)
            {
                for (var i = 0; i < seg; i++)
                {
                    var a = 1 + r * seg + i;
                    var b = 1 + r * seg + (i + 1) % seg;
                    var c = 1 + (r + 1) * seg + (i + 1) % seg;
                    var d = 1 + (r + 1) * seg + i;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(a); tris.Add(c); tris.Add(d);
                }
            }
            var m = new Mesh { name = "TeslaDisc" };
            m.vertices = verts.ToArray();
            m.colors = colors.ToArray();
            m.uv = uvs.ToArray();
            m.triangles = tris.ToArray();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>单位球（闪白壳）。</summary>
        public static Mesh Sphere(int lon, int lat)
        {
            var verts = new Vector3[(lat + 1) * (lon + 1)];
            var uvs = new Vector2[verts.Length];
            var tris = new List<int>(lat * lon * 6);
            for (var j = 0; j <= lat; j++)
            {
                var v = Mathf.PI * j / lat;
                for (var i = 0; i <= lon; i++)
                {
                    var u = Mathf.PI * 2f * i / lon;
                    var idx = j * (lon + 1) + i;
                    verts[idx] = new Vector3(Mathf.Sin(v) * Mathf.Cos(u), Mathf.Cos(v), Mathf.Sin(v) * Mathf.Sin(u));
                    uvs[idx] = new Vector2((float)i / lon, (float)j / lat);
                }
            }
            for (var j = 0; j < lat; j++)
            {
                for (var i = 0; i < lon; i++)
                {
                    var a = j * (lon + 1) + i;
                    var b = a + lon + 1;
                    tris.Add(a); tris.Add(b); tris.Add(b + 1);
                    tris.Add(a); tris.Add(b + 1); tris.Add(a + 1);
                }
            }
            var m = new Mesh { name = "TeslaSphere" };
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris.ToArray();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>开口圆柱（塔身充能白化壳），轴向 Y、高 1、半径 1，中心在原点。</summary>
        public static Mesh Cylinder(int seg)
        {
            var verts = new Vector3[(seg + 1) * 2];
            var uvs = new Vector2[verts.Length];
            var tris = new int[seg * 6];
            for (var i = 0; i <= seg; i++)
            {
                var a = Mathf.PI * 2f * i / seg;
                var x = Mathf.Cos(a);
                var z = Mathf.Sin(a);
                verts[i * 2] = new Vector3(x, -0.5f, z);
                verts[i * 2 + 1] = new Vector3(x, 0.5f, z);
                uvs[i * 2] = new Vector2((float)i / seg, 0f);
                uvs[i * 2 + 1] = new Vector2((float)i / seg, 1f);
            }
            for (var i = 0; i < seg; i++)
            {
                var a = i * 2;
                var b = a + 2;
                var k = i * 6;
                tris[k] = a; tris[k + 1] = a + 1; tris[k + 2] = b + 1;
                tris[k + 3] = a; tris[k + 4] = b + 1; tris[k + 5] = b;
            }
            var m = new Mesh { name = "TeslaCylinder" };
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }
    }
}
