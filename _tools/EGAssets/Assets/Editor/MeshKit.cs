using System.Collections.Generic;
using UnityEngine;

namespace EGAssets
{

/// <summary>离线建模用网格构造（不参与运行时）。所有面法线按解析/几何计算，输出可直接烘焙为资产。</summary>
internal sealed class MeshBuilder
{
    private const float TwoPi = Mathf.PI * 2f;
    private readonly List<Vector3> _v = new();
    private readonly List<Vector3> _n = new();
    private readonly List<Vector2> _uv = new();
    private readonly List<int> _t = new();

    public Mesh Finish(string name)
    {
        var m = new Mesh { name = name };
        if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.SetVertices(_v);
        m.SetNormals(_n);
        m.SetUVs(0, _uv);
        m.SetTriangles(_t, 0);
        m.RecalculateBounds();
        return m;
    }

    private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc,
        Vector2 ua, Vector2 ub, Vector2 uc)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0f)
        {
            (b, c) = (c, b);
            (nb, nc) = (nc, nb);
            (ub, uc) = (uc, ub);
        }
        var i = _v.Count;
        _v.Add(a); _v.Add(b); _v.Add(c);
        _n.Add(na); _n.Add(nb); _n.Add(nc);
        _uv.Add(ua); _uv.Add(ub); _uv.Add(uc);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd,
        Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc + nd) < 0f)
        {
            (b, d) = (d, b);
            (nb, nd) = (nd, nb);
            (ub, ud) = (ud, ub);
        }
        var i = _v.Count;
        _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
        _n.Add(na); _n.Add(nb); _n.Add(nc); _n.Add(nd);
        _uv.Add(ua); _uv.Add(ub); _uv.Add(uc); _uv.Add(ud);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
    }

    private static Vector3 Xf(Matrix4x4 m, Vector3 p) => m.MultiplyPoint3x4(p);
    private static Vector3 XfN(Matrix4x4 m, Vector3 n) => m.inverse.transpose.MultiplyVector(n).normalized;

    /// <summary>旋转体：profile 为 (半径, 高度)，自下而上；平滑法线。</summary>
    public void AddLathe(Vector2[] profile, int seg, Matrix4x4 m, bool capBottom, bool capTop)
    {
        var count = profile.Length;
        var pn = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            Vector2 d;
            if (i == 0) d = profile[1] - profile[0];
            else if (i == count - 1) d = profile[count - 1] - profile[count - 2];
            else d = profile[i + 1] - profile[i - 1];
            if (d.sqrMagnitude < 1e-10f) d = new Vector2(0f, 1f);
            d.Normalize();
            pn[i] = new Vector2(d.y, -d.x);      // 外法线
        }

        var vv = new float[count];
        var total = 0f;
        for (var i = 1; i < count; i++) total += Vector2.Distance(profile[i], profile[i - 1]);
        total = Mathf.Max(total, 1e-5f);
        var acc = 0f;
        for (var i = 1; i < count; i++)
        {
            acc += Vector2.Distance(profile[i], profile[i - 1]);
            vv[i] = acc / total;
        }

        var start = _v.Count;
        for (var k = 0; k < count; k++)
        {
            var r = Mathf.Max(profile[k].x, 0f);
            for (var i = 0; i <= seg; i++)
            {
                var a = TwoPi * i / seg;
                var cs = Mathf.Cos(a);
                var sn = Mathf.Sin(a);
                _v.Add(Xf(m, new Vector3(r * cs, profile[k].y, r * sn)));
                _n.Add(XfN(m, new Vector3(pn[k].x * cs, pn[k].y, pn[k].x * sn)));
                _uv.Add(new Vector2((float)i / seg, vv[k]));
            }
        }
        for (var k = 0; k < count - 1; k++)
        {
            for (var i = 0; i < seg; i++)
            {
                var a = start + k * (seg + 1) + i;
                var b = a + 1;
                var d = start + (k + 1) * (seg + 1) + i;
                var c = d + 1;
                Quad(_v[a], _v[b], _v[c], _v[d], _n[a], _n[b], _n[c], _n[d], _uv[a], _uv[b], _uv[c], _uv[d]);
            }
        }
        if (capBottom) AddDisc(start, profile[0].y, profile[0].x, seg, m, -1f);
        if (capTop) AddDisc(start + (count - 1) * (seg + 1), profile[count - 1].y, profile[count - 1].x, seg, m, 1f);
    }

    private void AddDisc(int ringStart, float y, float r, int seg, Matrix4x4 m, float dir)
    {
        if (r < 1e-5f) return;
        var center = Xf(m, new Vector3(0f, y, 0f));
        var cn = XfN(m, new Vector3(0f, dir, 0f));
        for (var i = 0; i < seg; i++)
        {
            var a = ringStart + i;
            var b = ringStart + i + 1;
            var ua = new Vector2(0.5f, 0.5f);
            Tri(center, _v[a], _v[b], cn, cn, cn, ua, ua, ua);
        }
    }

    /// <summary>UV 球（极点用极小半径环代替）。</summary>
    public void AddSphere(float radius, int lon, int lat, Matrix4x4 m)
    {
        var profile = new Vector2[lat + 1];
        for (var i = 0; i <= lat; i++)
        {
            var t = Mathf.PI * i / lat;
            profile[i] = new Vector2(Mathf.Max(Mathf.Sin(t) * radius, 1e-4f), -Mathf.Cos(t) * radius);
        }
        AddLathe(profile, lon, m, false, false);
    }

    /// <summary>圆角矩形截面的倒角柱（基座/台/铭牌等硬表面）。</summary>
    public void AddChamferBox(float w, float d, float h, float corner, float chamfer, int seg, Matrix4x4 m)
    {
        var outline = new List<Vector2>();
        RoundedRect(w * 0.5f, d * 0.5f, Mathf.Min(corner, Mathf.Min(w, d) * 0.45f), seg, outline);
        var n = outline.Count;
        var outN = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var p = outline[(i + 1) % n] - outline[(i - 1 + n) % n];
            outN[i] = new Vector2(p.y, -p.x).normalized;
        }

        var levels = new List<(float y, float s)>();
        if (chamfer > 1e-4f) levels.Add((0f, 1f - chamfer / Mathf.Min(w, d)));
        else levels.Add((0f, 1f));
        if (chamfer > 1e-4f) levels.Add((chamfer, 1f));
        if (chamfer > 1e-4f) levels.Add((h - chamfer, 1f));
        if (chamfer > 1e-4f) levels.Add((h, 1f - chamfer / Mathf.Min(w, d)));
        else levels.Add((h, 1f));

        var start = _v.Count;
        foreach (var (y, s) in levels)
        {
            for (var i = 0; i < n; i++)
            {
                _v.Add(Xf(m, new Vector3(outline[i].x * s, y, outline[i].y * s)));
                _n.Add(XfN(m, new Vector3(outN[i].x, 0f, outN[i].y)));
                _uv.Add(new Vector2((float)i / n, y / Mathf.Max(h, 1e-4f)));
            }
        }
        for (var k = 0; k < levels.Count - 1; k++)
        {
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                var a = start + k * n + i;
                var b = start + k * n + j;
                var c = start + (k + 1) * n + j;
                var dd = start + (k + 1) * n + i;
                Quad(_v[a], _v[b], _v[c], _v[dd], _n[a], _n[b], _n[c], _n[dd], _uv[a], _uv[b], _uv[c], _uv[dd]);
            }
        }
        CapOutline(start, n, levels[0].y, levels[0].s, outline, m, -1f);
        CapOutline(start + (levels.Count - 1) * n, n, levels[^1].y, levels[^1].s, outline, m, 1f);
    }

    private void CapOutline(int ringStart, int n, float y, float s, List<Vector2> outline, Matrix4x4 m, float dir)
    {
        var center = Xf(m, new Vector3(0f, y, 0f));
        var cn = XfN(m, new Vector3(0f, dir, 0f));
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            Tri(center, _v[ringStart + i], _v[ringStart + j], cn, cn, cn,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        }
    }

    private static void RoundedRect(float a, float b, float r, int seg, List<Vector2> pts)
    {
        if (r <= 1e-4f)
        {
            pts.Add(new Vector2(a, -b)); pts.Add(new Vector2(a, b));
            pts.Add(new Vector2(-a, b)); pts.Add(new Vector2(-a, -b));
            return;
        }
        var cx = a - r;
        var cy = b - r;
        var corners = new[] { (cx, cy, 0f), (-cx, cy, 90f), (-cx, -cy, 180f), (cx, -cy, 270f) };
        var steps = Mathf.Max(2, seg / 4);
        foreach (var (x, y, deg) in corners)
        {
            for (var i = 0; i <= steps; i++)
            {
                if (i == steps && (deg > 0f || pts.Count > 0)) { }
                var ang = (deg + 90f * i / steps) * Mathf.Deg2Rad;
                var p = new Vector2(x + r * Mathf.Cos(ang), y + r * Mathf.Sin(ang));
                if (pts.Count > 0 && (pts[^1] - p).sqrMagnitude < 1e-10f) continue;
                pts.Add(p);
            }
        }
    }

    /// <summary>圆环。</summary>
    public void AddTorus(float major, float minor, int majorSeg, int minorSeg, Matrix4x4 m)
    {
        var start = _v.Count;
        for (var i = 0; i <= majorSeg; i++)
        {
            var u = TwoPi * i / majorSeg;
            for (var j = 0; j <= minorSeg; j++)
            {
                var v = TwoPi * j / minorSeg;
                var cu = Mathf.Cos(u); var su = Mathf.Sin(u);
                var cv = Mathf.Cos(v); var sv = Mathf.Sin(v);
                var p = new Vector3((major + minor * cv) * cu, minor * sv, (major + minor * cv) * su);
                var nn = new Vector3(cv * cu, sv, cv * su);
                _v.Add(Xf(m, p));
                _n.Add(XfN(m, nn));
                _uv.Add(new Vector2((float)i / majorSeg, (float)j / minorSeg));
            }
        }
        for (var i = 0; i < majorSeg; i++)
        {
            for (var j = 0; j < minorSeg; j++)
            {
                var a = start + i * (minorSeg + 1) + j;
                var b = a + 1;
                var d = start + (i + 1) * (minorSeg + 1) + j;
                var c = d + 1;
                Quad(_v[a], _v[b], _v[c], _v[d], _n[a], _n[b], _n[c], _n[d], _uv[a], _uv[b], _uv[c], _uv[d]);
            }
        }
    }

    /// <summary>沿 a→b 的圆杆。</summary>
    public void AddRod(Vector3 a, Vector3 b, float r, int seg, bool capA = true, bool capB = true)
    {
        var dir = (b - a).normalized;
        var rot = Quaternion.FromToRotation(Vector3.up, dir);
        var len = Vector3.Distance(a, b);
        var m = Matrix4x4.TRS(a, rot, Vector3.one);
        AddLathe(new[] { new Vector2(r, 0f), new Vector2(r, len) }, seg, m, capA, capB);
    }

    /// <summary>锥形方柱（红色支腿等）：a/b 为两端中心，xAxis 为截面宽度方向。</summary>
    public void AddTaperedBox(Vector3 a, Vector3 b, Vector3 xAxis, float w0, float d0, float w1, float d1)
    {
        var dir = (b - a).normalized;
        var x = (xAxis - dir * Vector3.Dot(xAxis, dir)).normalized;
        var z = Vector3.Cross(dir, x).normalized;

        Vector3 P(Vector3 c, float sx, float sz, float w, float d)
            => c + x * (sx * w * 0.5f) + z * (sz * d * 0.5f);

        var bot = new[] { P(a, -1, -1, w0, d0), P(a, 1, -1, w0, d0), P(a, 1, 1, w0, d0), P(a, -1, 1, w0, d0) };
        var top = new[] { P(b, -1, -1, w1, d1), P(b, 1, -1, w1, d1), P(b, 1, 1, w1, d1), P(b, -1, 1, w1, d1) };

        Vector3 N(Vector3 p, Vector3 q, Vector3 r) => Vector3.Cross(q - p, r - p).normalized;

        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            var n = N(bot[i], bot[j], top[j]);
            Quad(bot[i], bot[j], top[j], top[i], n, n, n, n,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
        }
        var nb = N(bot[0], bot[1], bot[2]);
        Quad(bot[3], bot[2], bot[1], bot[0], nb, nb, nb, nb,
            new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f));
        var nt = N(top[0], top[1], top[2]);
        Quad(top[0], top[1], top[2], top[3], nt, nt, nt, nt,
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
    }

    /// <summary>沿 a→b 的锥体（尖刺）。</summary>
    public void AddCone(Vector3 a, Vector3 b, float r, int seg)
    {
        var dir = (b - a).normalized;
        var rot = Quaternion.FromToRotation(Vector3.up, dir);
        var len = Vector3.Distance(a, b);
        var m = Matrix4x4.TRS(a, rot, Vector3.one);
        AddLathe(new[]
        {
            new Vector2(r, 0f), new Vector2(r * 0.55f, len * 0.45f), new Vector2(0.004f, len)
        }, seg, m, true, false);
    }

    /// <summary>沿 Y 轴的螺旋管。</summary>
    public void AddHelix(float helixR, float tubeR, float y0, float y1, float turns, int segPerTurn, int tubeSeg)
    {
        var n = Mathf.Max(8, Mathf.CeilToInt(turns * segPerTurn));
        var pts = new Vector3[n + 1];
        var tan = new Vector3[n + 1];
        var dy = (y1 - y0) / (TwoPi * turns);
        for (var i = 0; i <= n; i++)
        {
            var f = (float)i / n;
            var a = TwoPi * turns * f;
            pts[i] = new Vector3(helixR * Mathf.Cos(a), Mathf.Lerp(y0, y1, f), helixR * Mathf.Sin(a));
            tan[i] = new Vector3(-Mathf.Sin(a), dy, Mathf.Cos(a)).normalized;
        }
        var start = _v.Count;
        for (var i = 0; i <= n; i++)
        {
            var t = tan[i];
            var nrm = Vector3.Cross(t, Vector3.up);
            if (nrm.sqrMagnitude < 1e-6f) nrm = Vector3.right;
            nrm.Normalize();
            var bnm = Vector3.Cross(t, nrm).normalized;
            for (var j = 0; j <= tubeSeg; j++)
            {
                var v = TwoPi * j / tubeSeg;
                var off = nrm * Mathf.Cos(v) + bnm * Mathf.Sin(v);
                _v.Add(pts[i] + off * tubeR);
                _n.Add(off.normalized);
                _uv.Add(new Vector2((float)i / n, (float)j / tubeSeg));
            }
        }
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < tubeSeg; j++)
            {
                var a = start + i * (tubeSeg + 1) + j;
                var b = a + 1;
                var d = start + (i + 1) * (tubeSeg + 1) + j;
                var c = d + 1;
                Quad(_v[a], _v[b], _v[c], _v[d], _n[a], _n[b], _n[c], _n[d], _uv[a], _uv[b], _uv[c], _uv[d]);
            }
        }
    }
}
}
