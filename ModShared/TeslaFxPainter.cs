using System;
using System.Collections.Generic;
using UnityEngine;

namespace SurvivalLog.ModShared
{
    /// <summary>
    /// 磁暴线圈特效绘制器（mod 运行时与 Unity 离线自测共用同一份源码；改外观先过 EGFxSelfTest 出图自审）。
    /// 待机：贴着环组的尖角开口电弧——左右外缘各一道「括号弧」、正面一道横穿的 Z 字、偶发一条蹿向球底的短弧。
    /// 锚点相对相机方位角取（任何机位都落在环组轮廓两侧），慢漂移=弧绕着环组爬；每 0.05s 重抖形，弧恒在不闪。
    /// 警戒（射程内有目标）：弧更多、更外扩。放电：球体闪白 + 主闪电 + 逐只串联 + 目标缠电（路径由 TeslaFxGeo.Chain 给定）。
    /// 线型：待机弧=RA2 原版同款蓝紫粗线；闪电=辉光 + 蓝带 + 白亮芯。材质、元素工厂、相机取用见 TeslaFxRig。
    /// </summary>
    public sealed class TeslaFxPainter
    {
        public const int MaxChain = 5;

        private const int IdleSlots = 5;
        private const float HalfPi = Mathf.PI * 0.5f;

        private readonly TeslaFxRig _rig;
        private readonly TeslaFxRig.Elem[] _idle = new TeslaFxRig.Elem[IdleSlots];
        private readonly TeslaFxRig.Elem _bolt, _branch0, _branch1;
        private readonly TeslaFxRig.Elem[] _chain = new TeslaFxRig.Elem[MaxChain];
        private readonly TeslaFxRig.Elem[] _hitArc = new TeslaFxRig.Elem[MaxChain];
        private readonly MeshRenderer[] _hitDiscs = new MeshRenderer[MaxChain];
        private readonly MeshRenderer _ballFlash, _bodyFlash, _ballHalo;

        public TeslaFxPainter(Transform model, Material band, Material core, Material glow, Material disc)
        {
            var r = _rig = new TeslaFxRig(model, band, core, glow, disc);
            for (var i = 0; i < IdleSlots; i++)
                _idle[i] = r.NewElem("Idle" + i, r.ArcBand, r.ArcCore, true);
            _bolt = r.NewElem("Bolt", r.Band, r.Core, true);
            _branch0 = r.NewElem("Branch0", r.Band, r.Core, false);
            _branch1 = r.NewElem("Branch1", r.Band, r.Core, false);
            for (var i = 0; i < MaxChain; i++)
            {
                _chain[i] = r.NewElem("Chain" + i, r.Band, r.Core, true);
                _hitArc[i] = r.NewElem("HitArc" + i, r.Band, r.Core, false);
            }

            var discMesh = TeslaFxMeshes.Disc(8, 40);
            for (var i = 0; i < MaxChain; i++) _hitDiscs[i] = r.NewDisc("HitDisc" + i, discMesh, r.Hit);
            _ballHalo = r.NewDisc("BallHalo", discMesh, r.Halo);   // 面片在球心，被球体挡住中间，只露出外圈
            _ballFlash = r.NewShell("BallFlash", TeslaFxMeshes.Sphere(28, 16),
                new Vector3(0f, TeslaFxGeo.BallY, 0f), Vector3.one * TeslaFxGeo.BallR, r.BallFlash);
            _bodyFlash = r.NewShell("BodyFlash", TeslaFxMeshes.Cylinder(28),
                new Vector3(0f, TeslaFxGeo.BodyY, 0f),
                new Vector3(TeslaFxGeo.BodyR, TeslaFxGeo.BodyH, TeslaFxGeo.BodyR), r.BodyFlash);
            Hide();
        }

        // ───────────────────────── 帧绘制 ─────────────────────────

        /// <summary>待机/警戒：贴环电弧。t=持续流逝的时间（驱动锚点慢漂移），alert=0 待机 → 1 射程内有目标。</summary>
        public void DrawIdle(int seed, float t, float alert)
        {
            HideBolt();
            SetFlash(0f, 0f);
            DrawArcs(new System.Random(seed * 131 + 7), t, alert);
        }

        /// <summary>放电：球体闪白 + 主闪电 + 逐只串联 + 目标缠电（环组电弧保持跳动）。chain=躯干中心序列（首只为游戏实际目标）。</summary>
        public void DrawFire(IList<Vector3> chain, int seed, float t, float intensity)
        {
            if (chain == null || chain.Count == 0 || intensity <= 0.01f)
            {
                HideBolt();
                SetFlash(0f, 0f);
                DrawArcs(new System.Random(seed * 131 + 7), t, 0f);
                return;
            }
            var rng = new System.Random(seed * 7919 + 13);
            DrawArcs(new System.Random(seed * 131 + 7), t, 0.5f);   // 放电时弧比待机强、比警戒少，免得糊成一团
            var model = _rig.Model;
            var scale = model.lossyScale.y;
            var cam = TeslaFxRig.ActiveCamera();
            var rot = cam != null ? cam.transform.rotation : Quaternion.identity;
            var ball = model.TransformPoint(new Vector3(0f, TeslaFxGeo.BallY, 0f));
            var first = chain[0];
            var dir = (first - ball).normalized;
            var main = TeslaFxGeo.Bolt(rng, ball + dir * (TeslaFxGeo.BallR * scale), first, 6, 0.16f);
            Put(_bolt, main, 0.24f, 0.15f, 0.55f * intensity, 0.46f);

            var branchCount = 1 + rng.Next(2);
            for (var i = 0; i < 2; i++)
            {
                var e = i == 0 ? _branch0 : _branch1;
                if (i >= branchCount || main.Count < 20)
                {
                    e.Clear();
                    continue;
                }
                var idx = rng.Next(6, main.Count - 8);
                var anchor = main[idx];
                var d = (main[idx + 6] - anchor).normalized;
                d = (d + new Vector3(TeslaFxGeo.Range(rng, -1f, 1f), TeslaFxGeo.Range(rng, -1f, 1f),
                    TeslaFxGeo.Range(rng, -1f, 1f)) * 1.2f).normalized;
                var len = TeslaFxGeo.Range(rng, 0.5f, 1.1f) * scale;
                Put(e, TeslaFxGeo.Bolt(rng, anchor, anchor + d * len, 4, 0.22f), 0.08f, 0.03f, 0f, 0.3f);
            }

            for (var i = 1; i < chain.Count; i++)
                Put(_chain[i - 1], TeslaFxGeo.Bolt(rng, chain[i - 1], chain[i], 5, 0.22f), 0.18f, 0.13f, 0.4f * intensity, 0.42f);
            for (var i = chain.Count - 1; i < MaxChain; i++) _chain[i].Clear();

            var c = _rig.Hit.GetColor("_Color");
            c.a = 0.6f * intensity;
            _rig.Hit.SetColor("_Color", c);
            for (var i = 0; i < MaxChain; i++)
            {
                var mr = _hitDiscs[i];
                if (i >= chain.Count)
                {
                    mr.gameObject.SetActive(false);
                    _hitArc[i].Clear();
                    continue;
                }
                var loop = TeslaFxCoilArcs.Loop(rng, 6, 0.42f * scale, 0f, 0.34f * scale, 0.25f, 1.5f, 0.9f);
                for (var k = 0; k < loop.Count; k++) loop[k] += chain[i];
                Put(_hitArc[i], loop, 0.06f, 0.06f, 0.3f * intensity, 0.4f);
                mr.gameObject.SetActive(true);
                mr.transform.position = chain[i];
                mr.transform.rotation = rot;
                mr.transform.localScale = Vector3.one * (TeslaFxGeo.Range(rng, 1.3f, 1.8f) * scale);
            }

            SetFlash(0.95f * intensity, 0.50f * intensity);
            c = _rig.Halo.GetColor("_Color");
            c.a = 0.6f * intensity;
            _rig.Halo.SetColor("_Color", c);
            _ballHalo.gameObject.SetActive(true);
            _ballHalo.transform.SetPositionAndRotation(ball, rot);
            _ballHalo.transform.localScale = Vector3.one * (TeslaFxGeo.BallR * scale * 3.2f);
        }

        /// <summary>全部隐藏。</summary>
        public void Hide()
        {
            HideArcs();
            HideBolt();
            SetFlash(0f, 0f);
        }

        public void Dispose() => _rig.Dispose();

        // ───────────────────────── 内部 ─────────────────────────

        /// <summary>贴环电弧（线型=RA2 原版：线宽≈建筑高度的 3.6%、蓝紫明暗）：左右括号弧 + 正面横穿 Z 字 +
        /// 偶发球下短弧，全部开口不闭合；警戒时正面再追加一道。</summary>
        private void DrawArcs(System.Random rng, float t, float alert)
        {
            var az = _rig.CameraAzimuth();
            var spread = 1f + 0.5f * alert;                // 警戒：弧离环更远
            const float rough = 0.012f;                    // 直线段上的细小毛刺（像素闪电感）
            var i = 0;

            // 左右括号弧：锚在环组两侧轮廓，往正面绕；一上一下方向交错
            var leftUp = rng.Next(2) == 0;
            Put(_idle[i++], _rig.ToWorld(TeslaFxGeo.Roughen(rng, TeslaFxCoilArcs.StackArc(rng,
                az + HalfPi + Drift(t, 0f) + TeslaFxGeo.Range(rng, -0.25f, 0.25f), -TeslaFxGeo.Range(rng, 0.9f, 1.9f),
                TeslaFxCoilArcs.RingYs[leftUp ? 0 : 2], TeslaFxCoilArcs.RingYs[leftUp ? 2 : rng.Next(2)],
                rng.Next(2, 4), 1.1f, 1.32f * spread, TeslaFxCoilArcs.StackBottom + 0.04f, TeslaFxCoilArcs.StackTop), rough)),
                0.1365f, 0.105f, 0.55f);
            Put(_idle[i++], _rig.ToWorld(TeslaFxGeo.Roughen(rng, TeslaFxCoilArcs.StackArc(rng,
                az - HalfPi + Drift(t, 1f) + TeslaFxGeo.Range(rng, -0.25f, 0.25f), TeslaFxGeo.Range(rng, 0.9f, 1.9f),
                TeslaFxCoilArcs.RingYs[leftUp ? 2 : 0], TeslaFxCoilArcs.RingYs[leftUp ? rng.Next(1, 3) : 2],
                rng.Next(2, 4), 1.1f, 1.32f * spread, TeslaFxCoilArcs.StackBottom + 0.04f, TeslaFxCoilArcs.StackTop), rough)),
                0.1365f, 0.105f, 0.55f);

            // 正面横穿的 Z 字：自左轮廓横跨正面到右侧，高度贴着中层环附近
            Put(_idle[i++], _rig.ToWorld(TeslaFxGeo.Roughen(rng, TeslaFxCoilArcs.StackArc(rng,
                az + HalfPi + Drift(t, 2f) + TeslaFxGeo.Range(rng, -0.3f, 0.3f), -(Mathf.PI - TeslaFxGeo.Range(rng, 0.1f, 0.5f)),
                TeslaFxGeo.Range(rng, 2.0f, 2.36f), TeslaFxGeo.Range(rng, 1.9f, 2.32f),
                rng.Next(3, 5), 1.06f, 1.26f * spread, 1.96f, 2.42f), rough)), 0.1155f, 0.0945f, 0.45f);

            // 蹿向球底的短弧：偶发
            if (rng.NextDouble() < 0.6 + 0.3 * alert)
                Put(_idle[i++], _rig.ToWorld(TeslaFxGeo.Roughen(rng,
                    TeslaFxCoilArcs.StemArc(rng, az + TeslaFxGeo.Range(rng, -0.9f, 0.9f), 2), rough * 0.6f)),
                    0.105f, 0.0735f, 0.4f);
            else _idle[i++].Clear();

            // 警戒追加：正面再来一道
            while (i < IdleSlots)
            {
                var e = _idle[i++];
                if (alert < 0.6f) { e.Clear(); continue; }
                var side = i % 2 == 0 ? 1f : -1f;
                Put(e, _rig.ToWorld(TeslaFxGeo.Roughen(rng, TeslaFxCoilArcs.StackArc(rng,
                    az + side * TeslaFxGeo.Range(rng, 0.5f, 1.1f) + Drift(t, i), -side * TeslaFxGeo.Range(rng, 0.6f, 1.4f),
                    TeslaFxCoilArcs.RingYs[rng.Next(3)], TeslaFxCoilArcs.RingYs[rng.Next(3)],
                    rng.Next(1, 3), 1.12f, 1.4f * spread, TeslaFxCoilArcs.StackBottom + 0.04f, TeslaFxCoilArcs.StackTop + 0.12f),
                    rough)), 0.105f, 0.084f, 0.35f);
            }
        }

        private void HideArcs()
        {
            for (var i = 0; i < IdleSlots; i++) _idle[i].Clear();
        }

        private void HideBolt()
        {
            _bolt.Clear();
            _branch0.Clear();
            _branch1.Clear();
            for (var i = 0; i < MaxChain; i++)
            {
                _chain[i].Clear();
                _hitArc[i].Clear();
                _hitDiscs[i].gameObject.SetActive(false);
            }
            _ballHalo.gameObject.SetActive(false);
        }

        private void SetFlash(float ball, float body)
        {
            _rig.BallFlash.SetColor("_Color", new Color(1f, 1f, 1f, ball));
            _rig.BodyFlash.SetColor("_Color", new Color(0.88f, 0.94f, 1f, body));
        }

        /// <summary>锚点慢漂移：括号弧的根在环组轮廓两侧缓慢游走=「绕着爬」。</summary>
        private static float Drift(float t, float k)
        {
            return 0.42f * Mathf.Sin(t * 0.9f + k * 2.1f) + 0.2f * Mathf.Sin(t * 0.37f + k * 1.7f);
        }

        /// <summary>画一条三层闪电：蓝带宽 w（模型局部单位，随安装缩放；屏幕上不低于最小像素宽），
        /// 亮芯=蓝带×core，辉光透明度 glow。</summary>
        private void Put(TeslaFxRig.Elem e, List<Vector3> pts, float wStart, float wEnd, float glow,
            float core = TeslaFxRig.CoreRatio)
        {
            var cam = TeslaFxRig.CamPos();
            var ws = _rig.Width(wStart);
            var we = _rig.Width(wEnd);
            e.Band.sharedMesh = TeslaFxGeo.Ribbon(e.Band.sharedMesh, pts, cam, ws, we);
            e.Core.sharedMesh = TeslaFxGeo.Ribbon(e.Core.sharedMesh, pts, cam, ws * core, we * core);
            if (e.Glow == null) return;
            if (glow <= 0f)
            {
                e.Glow.sharedMesh = null;
                return;
            }
            var c = _rig.GlowColor;
            c.a = glow;
            e.GlowMat.SetColor("_Color", c);
            e.Glow.sharedMesh = TeslaFxGeo.Ribbon(e.Glow.sharedMesh, pts, cam,
                ws * TeslaFxRig.GlowRatio, we * TeslaFxRig.GlowRatio);
        }
    }
}
