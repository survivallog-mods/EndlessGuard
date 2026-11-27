using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.ModShared
{
    /// <summary>
    /// 特效台架：场景根、材质、ribbon 元素/面片工厂、相机取用与像素尺寸；每帧画什么由 TeslaFxPainter 决定。
    /// ribbon 顶点是世界坐标，所以元素全部挂在与模型无父子关系的场景根下（挂模型下会被二次变换）；
    /// 只有包住模型表面的闪白壳挂在模型下。
    /// </summary>
    public sealed class TeslaFxRig
    {
        public const float CoreRatio = 0.38f;     // 亮芯宽=蓝带宽×此比例
        public const float GlowRatio = 2.4f;      // 辉光宽=蓝带宽×此比例
        private const float MinBandPx = 4f;       // 蓝带在屏幕上的最小宽度（像素）

        private static Camera _cam;
        private static float _camTimer;
        private static Camera _camOverride;

        /// <summary>一条三层闪电：蓝带 + 亮芯（+可选辉光），各一个 MeshFilter，共用同一折线。</summary>
        public sealed class Elem
        {
            public readonly MeshFilter Band, Core, Glow;   // Glow=null：这条不带辉光
            public readonly Material GlowMat;

            internal Elem(TeslaFxRig rig, string name, Material band, Material core, Material glowMat)
            {
                Band = rig.Ribbon(name + "Band", band);
                Core = rig.Ribbon(name + "Core", core);
                GlowMat = glowMat;
                Glow = glowMat != null ? rig.Ribbon(name + "Glow", glowMat) : null;
            }

            public void Clear()
            {
                Band.sharedMesh = null;
                Core.sharedMesh = null;
                if (Glow != null) Glow.sharedMesh = null;
            }
        }

        public readonly Transform Model;
        public readonly Color BandColor, GlowColor;
        public readonly Material Band, ArcBand, Core, ArcCore, Hit, Halo, BallFlash, BodyFlash;

        private readonly GameObject _root;
        private readonly Material _glowSrc;
        private readonly List<Material> _owned = new List<Material>();
        private readonly List<GameObject> _shells = new List<GameObject>();

        /// <param name="model">线圈模型根（决定锚点/缩放）。</param>
        /// <param name="band">bundle 的 Fx_BoltBlue（蓝带，普通混合）。</param>
        /// <param name="core">bundle 的 Fx_BoltCore（加色亮芯）。</param>
        /// <param name="glow">bundle 的 Fx_BoltGlow（加色辉光，每条弧各克隆一份以便单独调透明度）。</param>
        /// <param name="disc">bundle 的 Fx_Disc（加色面片，顶点色衰减）。</param>
        public TeslaFxRig(Transform model, Material band, Material core, Material glow, Material disc)
        {
            Model = model;
            _root = new GameObject("TeslaFx");
            _root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _root.transform.localScale = Vector3.one;

            // 闪电：蓝带 + 白芯
            Band = Own(band, 3000);
            BandColor = Band.GetColor("_Color");
            BandColor.a = 0.92f;
            Band.SetColor("_Color", BandColor);
            Core = Own(core, 3001);
            Core.SetColor("_Color", Color.white);
            // 待机弧：RA2 原版电弧实测色——主色 #6666FF、高光 #9999FF（蓝紫明暗，不是白芯）
            ArcBand = Own(band, 3000);
            ArcBand.SetColor("_Color", new Color(0.40f, 0.40f, 1f, 0.92f));
            ArcCore = Own(core, 3001);
            ArcCore.SetColor("_Color", new Color(0.62f, 0.62f, 1f, 1f));
            GlowColor = new Color(0.45f, 0.45f, 1f, 1f);
            _glowSrc = glow;
            Hit = Own(disc, 3002);
            Hit.SetInt("_ZTest", 8);              // Always：命中闪光盖在躯干上（闪白壳保持默认 LEqual，被前方红板正常遮挡）
            Halo = Own(disc, 2999);
            BallFlash = Own(disc, 3001);
            BodyFlash = Own(disc, 3001);
        }

        /// <summary>新建一条闪电元素。glow=true 时自带一份辉光材质克隆（队列 2998：先画，蓝带/亮芯叠在上面）。</summary>
        public Elem NewElem(string name, Material band, Material core, bool glow)
        {
            if (!glow) return new Elem(this, name, band, core, null);
            var g = Own(_glowSrc, 2998);
            g.SetColor("_Color", GlowColor);
            return new Elem(this, name, band, core, g);
        }

        /// <summary>挂在场景根下的面片（命中闪光/球体辉光），默认隐藏，位置朝向由绘制器每帧设。</summary>
        public MeshRenderer NewDisc(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            NoShadow(mr);
            go.SetActive(false);
            return mr;
        }

        /// <summary>包住模型某部件的壳（闪白），挂在模型下随其变换。</summary>
        public MeshRenderer NewShell(string name, Mesh mesh, Vector3 localPos, Vector3 localScale, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Model, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            NoShadow(mr);
            _shells.Add(go);
            return mr;
        }

        /// <summary>局部线宽 → 世界线宽：随模型缩放，但屏幕上不低于 MinBandPx 像素（拉远视角时不至于细成噪点）。</summary>
        public float Width(float local)
        {
            var at = Model.TransformPoint(new Vector3(0f, TeslaFxGeo.RingY, 0f));
            return Mathf.Max(local * Model.lossyScale.y, MinBandPx * PixelSize(at));
        }

        /// <summary>相机在模型局部坐标的方位角（x-z 平面）：待机弧的锚点相对它取，任何机位都落在环组轮廓两侧。</summary>
        public float CameraAzimuth()
        {
            var l = Model.InverseTransformPoint(CamPos());
            return Mathf.Atan2(l.z, l.x);
        }

        public List<Vector3> ToWorld(List<Vector3> local)
        {
            for (var i = 0; i < local.Count; i++) local[i] = Model.TransformPoint(local[i]);
            return local;
        }

        /// <summary>离线自测指定渲染相机（运行时不设=自动取游戏主相机）。</summary>
        public static void SetCameraOverride(Camera cam)
        {
            _camOverride = cam;
            _cam = cam;
        }

        /// <summary>ribbon 的侧向、面片朝向靠它；拿不到相机时侧向会错，所以不依赖 Camera.main（游戏主相机未打 MainCamera 标签）。</summary>
        public static Vector3 CamPos()
        {
            var cam = ActiveCamera();
            return cam != null ? cam.transform.position : Vector3.up * 20f + Vector3.forward * 20f;
        }

        /// <summary>取相机：自测覆盖 → 游戏内启用的 Game 相机（depth 最大）→ 任意相机，1s 缓存。</summary>
        public static Camera ActiveCamera()
        {
            if (_camOverride != null) return _camOverride;
            _camTimer -= Time.unscaledDeltaTime;
            if (_cam != null && _camTimer > 0f) return _cam;
            _camTimer = 1f;
            _cam = null;
            var cams = Camera.allCameras;
            Camera any = null;
            for (var i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null || !c.isActiveAndEnabled) continue;
                if (any == null) any = c;
                if (c.cameraType != CameraType.Game) continue;
                if (_cam == null || c.depth > _cam.depth) _cam = c;
            }
            if (_cam == null) _cam = any;
            return _cam;
        }

        public void Dispose()
        {
            Destroy(_root);
            foreach (var go in _shells) if (go != null) Destroy(go);
            _shells.Clear();
            foreach (var m in _owned) if (m != null) Destroy(m);
            _owned.Clear();
        }

        /// <summary>at 处一个屏幕像素对应的世界尺寸（透视按距离，正交按 orthographicSize）。</summary>
        private static float PixelSize(Vector3 at)
        {
            var cam = ActiveCamera();
            if (cam == null) return 1f / 236f;             // 无相机：按实机标定的 4K 像素密度兜底
            var h = cam.pixelHeight > 0 ? cam.pixelHeight : 2160;
            if (cam.orthographic) return cam.orthographicSize * 2f / h;
            var d = Vector3.Distance(cam.transform.position, at);
            return 2f * d * Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) / h;
        }

        private MeshFilter Ribbon(string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            NoShadow(mr);
            return mf;
        }

        private Material Own(Material src, int queue)
        {
            if (src == null) throw new ArgumentNullException("material");
            var m = new Material(src) { renderQueue = queue };
            _owned.Add(m);
            return m;
        }

        private static void NoShadow(MeshRenderer mr)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private static void Destroy(Object o)
        {
            if (o == null) return;
#if UNITY_EDITOR
            Object.DestroyImmediate(o);
#else
            Object.Destroy(o);
#endif
        }
    }
}
