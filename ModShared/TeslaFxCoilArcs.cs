using System;
using System.Collections.Generic;
using UnityEngine;

namespace SurvivalLog.ModShared
{
    /// <summary>
    /// 线圈环组上的电弧几何（prefab 局部坐标：高 4.1m、根在原点、+Y 向上）。
    /// 包络数值取自金属件顶点实测：三层环 y≈1.82/2.22/2.48、外缘半径 0.69/0.60/0.52，环间收窄。
    /// 只依赖 UnityEngine 核心类型；块级 namespace（Unity 离线工程侧为 C# 9，不支持文件级 namespace）。
    /// </summary>
    public static class TeslaFxCoilArcs
    {
        public const float StackBottom = 1.66f;
        public const float StackTop = 2.60f;
        public static readonly float[] RingYs = { 1.82f, 2.22f, 2.48f };

        /// <summary>该高度处环的外缘半径（环间过渡取小值，避免弧起点陷进环体）。</summary>
        public static float RingRadiusAt(float y)
        {
            return y < 2.04f ? 0.66f : y < 2.36f ? 0.58f : 0.50f;
        }

        private static Vector3 RingPoint(float a, float y, float k)
        {
            var r = RingRadiusAt(y) * k;
            return new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
        }

        /// <summary>环组电弧：自环上一点出发、绕塔轴跨 span 弧度、再落回环上的开口折线。
        /// 中间只有 mids 个顶点、段直角尖——RA2 像素电弧就是 3~5 段的尖角折线，不是密集小锯齿。
        /// 中间点半径=环外缘×[rMin,rMax]：浮在环外缘之外，深度测试下不被环体吃掉；yLow/yHigh 限定爬升范围。</summary>
        public static List<Vector3> StackArc(System.Random rng, float a0, float span, float yA, float yB,
            int mids, float rMin, float rMax, float yLow, float yHigh)
        {
            var pts = new List<Vector3>(mids + 2);
            pts.Add(RingPoint(a0, yA, 1.0f));
            for (var i = 1; i <= mids; i++)
            {
                var a = a0 + span * ((float)i / (mids + 1) + TeslaFxGeo.Range(rng, -0.12f, 0.12f));
                pts.Add(RingPoint(a, TeslaFxGeo.Range(rng, yLow, yHigh), TeslaFxGeo.Range(rng, rMin, rMax)));
            }
            pts.Add(RingPoint(a0 + span, yB, 1.0f));
            return pts;
        }

        /// <summary>球下短弧：自环组顶前缘向上蹿到球底附近（RA2 待机帧里偶发的一条）。</summary>
        public static List<Vector3> StemArc(System.Random rng, float a, int mids)
        {
            var pts = new List<Vector3>(mids + 2);
            pts.Add(RingPoint(a, StackTop - 0.04f, 1.0f));
            for (var i = 1; i <= mids; i++)
            {
                var t = (float)i / (mids + 1);
                var y = Mathf.Lerp(StackTop, TeslaFxGeo.BallY - 0.5f, t);
                var r = Mathf.Lerp(0.52f, 0.2f, t) + TeslaFxGeo.Range(rng, 0.05f, 0.3f);
                var aa = a + TeslaFxGeo.Range(rng, -0.7f, 0.7f);
                pts.Add(new Vector3(Mathf.Cos(aa) * r, y, Mathf.Sin(aa) * r));
            }
            var ae = a + TeslaFxGeo.Range(rng, -0.5f, 0.5f);
            pts.Add(new Vector3(Mathf.Cos(ae) * 0.2f, TeslaFxGeo.BallY - 0.5f, Mathf.Sin(ae) * 0.2f));
            return pts;
        }

        /// <summary>闭合锯齿环（绕某点的水平环，半径/高度强抖动，棱上按概率补内凹/外凸小折点），末点=首点。
        /// 用于目标躯干外的缠电。</summary>
        public static List<Vector3> Loop(System.Random rng, int n, float radius, float yCenter,
            float yJitter, float rMin, float rMax, float zig)
        {
            var pts = new List<Vector3>(n * 2 + 1);
            var phase = TeslaFxGeo.Range(rng, 0f, Mathf.PI * 2f);
            for (var i = 0; i < n; i++)
            {
                var a = phase + i * Mathf.PI * 2f / n + TeslaFxGeo.Range(rng, -0.38f, 0.38f);
                var r = radius * TeslaFxGeo.Range(rng, rMin, rMax);
                var cur = new Vector3(Mathf.Cos(a) * r, yCenter + TeslaFxGeo.Range(rng, -yJitter, yJitter), Mathf.Sin(a) * r);
                if (i > 0 && rng.NextDouble() < zig)
                {
                    var m = (pts[pts.Count - 1] + cur) * 0.5f;
                    var mr = Mathf.Sqrt(m.x * m.x + m.z * m.z);
                    var push = radius * TeslaFxGeo.Range(rng, 0.10f, 0.22f) * (rng.NextDouble() < 0.5 ? -1f : 1f);
                    if (mr > 1e-4f)
                    {
                        m.x += m.x / mr * push;
                        m.z += m.z / mr * push;
                    }
                    m.y += TeslaFxGeo.Range(rng, -yJitter, yJitter) * 0.6f;
                    pts.Add(m);
                }
                pts.Add(cur);
            }
            pts.Add(pts[0]);
            return pts;
        }
    }
}
