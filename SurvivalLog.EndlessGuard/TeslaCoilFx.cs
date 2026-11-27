using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.Battle.Show;
using SurvivalLog.ModShared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 磁暴线圈特效组件（挂模型根）：扫目标 / 取丧尸躯干中心 / 状态机 / 与设备真实开火对拍。
/// 形态绘制全部委托 TeslaFxPainter（与离线自测共用源码，改外观只动 ModShared）。
/// 节奏：待机=贴环电弧持续跳，射程内有目标则加强（警戒）；只在收到本塔的 DeviceFired（与伤害同帧）时放电。
/// 链电路径=TeslaFxGeo.Chain（起点为游戏实际开火目标，逐只串联最多 5 只，单跳 ≤3m）。
/// </summary>
internal sealed class TeslaCoilFx : MonoBehaviour
{
    private const float SearchRange = 5.0f;     // 塔座起算的水平半径（设备射程 4.5 格）
    private const float HopRange = 3.0f;        // 链电单跳半径
    private const float FireTime = 0.35f;
    private const float CooldownTime = 0.25f;

    private enum FxState { Idle, Fire, Cooldown }

    private Transform _model;
    private TeslaFxPainter _painter;
    private FxState _state = FxState.Idle;
    private float _stateT, _scanTimer, _idleTimer, _t, _ft, _seed, _alert;
    private readonly List<long> _candIds = new();
    private readonly List<Vector3> _candPos = new();
    private readonly List<Vector3> _chain = new();
    private long _syncTargetId;
    private bool _syncFire, _ready;
    private int _errCount, _missLog;

    /// <summary>丧尸 instanceId → 躯干中心相对逻辑位置的高度（同类丧尸身高一致，量一次缓存）。</summary>
    private static readonly Dictionary<long, float> BodyOffset = new();

    public TeslaCoilFx(IntPtr p) : base(p) { }
    public TeslaCoilFx() { }

    public void Init(AssetBundle bundle, GameObject model)
    {
        try
        {
            _model = model.transform;
            var band = LoadMat(bundle, "Assets/TeslaCoilBundle/Fx_BoltBlue.mat");
            var glow = LoadMat(bundle, "Assets/TeslaCoilBundle/Fx_BoltGlow.mat");
            var core = LoadMat(bundle, "Assets/TeslaCoilBundle/Fx_BoltCore.mat");
            var disc = LoadMat(bundle, "Assets/TeslaCoilBundle/Fx_Disc.mat");
            if (band == null || glow == null || core == null || disc == null)
            {
                Trace("[TeslaFx] FAIL 特效材质缺失 band=" + (band != null) + " glow=" + (glow != null)
                      + " core=" + (core != null) + " disc=" + (disc != null));
                return;
            }
            _painter = new TeslaFxPainter(_model, band, core, glow, disc);
            DeviceXpRuntime.DeviceFired += OnDeviceFired;
            _painter.Hide();
            _ready = true;
            Trace("[TeslaFx] init ok pos=" + _model.position + " scale=" + _model.lossyScale.y);
        }
        catch (Exception e) { Trace("[TeslaFx] init: " + e.Message); }
    }

    private void Update()
    {
        if (!_ready) return;
        try
        {
            var dt = Time.deltaTime;              // 缩放时间：放电节奏随暂停冻结
            var udt = Time.unscaledDeltaTime;     // 待机电弧用真实时间：暂停时也持续跳（冻帧的闪电像根铁丝）
            _t += udt;
            _ft += dt;
            _stateT += dt;
            _scanTimer -= dt;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 0.3f;
                Scan();
            }
            _alert += ((_candPos.Count > 0 ? 1f : 0f) - _alert) * Mathf.Clamp01(udt * 6f);

            // 只跟设备真实开火（与伤害同帧）；冷却期的事件丢弃（设备间隔 2.2s 远大于整轮 0.6s）
            if (_syncFire && _state != FxState.Fire)
            {
                Scan();
                // 游戏的受击目标必须当链电起点：不在搜索半径内也要补进来，否则闪电会劈到别的丧尸身上
                if (_syncTargetId != 0 && !_candIds.Contains(_syncTargetId))
                    AddCandidate(_syncTargetId, float.MaxValue);
                if (_candPos.Count > 0) BeginFire();
            }
            _syncFire = false;

            switch (_state)
            {
                case FxState.Fire:
                    _painter.DrawFire(_chain, (int)(_ft / 0.04f), _t, Mathf.Clamp01(_stateT / 0.05f));
                    if (_stateT >= FireTime) Enter(FxState.Cooldown);
                    break;
                case FxState.Cooldown:
                    _painter.DrawFire(_chain, (int)(_ft / 0.04f), _t, Mathf.Clamp01(1f - _stateT / CooldownTime));
                    if (_stateT >= CooldownTime) Enter(FxState.Idle);
                    break;
                default:
                    _idleTimer -= udt;
                    if (_idleTimer <= 0f)
                    {
                        _idleTimer = 0.05f;      // 20Hz 重抖形
                        _seed++;
                        _painter.DrawIdle((int)_seed, _t, _alert);
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            if (_errCount++ < 5) Trace("[TeslaFx] update: " + e.Message);
        }
    }

    private void Enter(FxState s)
    {
        _state = s;
        _stateT = 0f;
        if (s == FxState.Idle) _idleTimer = 0f;    // 回待机立即画，不留空窗
    }

    private void BeginFire()
    {
        _chain.Clear();
        Vector3? first = null;
        if (_syncTargetId != 0)
            for (var i = 0; i < _candIds.Count; i++)
                if (_candIds[i] == _syncTargetId) first = _candPos[i];
        _chain.AddRange(TeslaFxGeo.Chain(_candPos, _model.position, first, HopRange, TeslaFxPainter.MaxChain));
        Enter(FxState.Fire);
        if (first == null && _syncTargetId != 0 && _missLog++ < 6)
            Trace("[TeslaFx] 游戏开火目标未能解析 id=" + _syncTargetId + "，链电改从最近的丧尸起（目标已死或非丧尸）");
    }

    /// <summary>只认本塔：配置行=99006 且炮口（家具位）落在塔座 1m 内。</summary>
    private void OnDeviceFired(long furnitureId, Vector3 muzzle, long targetId)
    {
        try
        {
            if (_model == null || !_ready) return;
            var cfg = 0;
            try { cfg = BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAgent(furnitureId)?.AgentConfigId ?? 0; }
            catch { }
            if (DeviceXpMath.BaseCfgOf(cfg) != TeslaCoil.Id) return;
            var dx = muzzle.x - _model.position.x;
            var dz = muzzle.z - _model.position.z;
            if (dx * dx + dz * dz > 1f) return;
            _syncTargetId = targetId;
            _syncFire = true;
        }
        catch { }
    }

    /// <summary>塔座 SearchRange 内的活丧尸 → 候选（id + 躯干中心）。</summary>
    private void Scan()
    {
        _candIds.Clear();
        _candPos.Clear();
        try
        {
            if (!BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return;
            var all = BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent();
            if (all == null) return;
            for (var i = 0; i < all.Count; i++)
            {
                try
                {
                    var a = all[i];
                    if (a == null || a.IsDead) continue;
                    var z = a.TryCast<Zombie>();
                    if (z == null) continue;
                    AddCandidate(z);
                }
                catch { }
            }
        }
        catch { }
    }

    private void AddCandidate(long instanceId, float range)
    {
        try
        {
            if (!BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return;
            var a = BaseSingleton<BattleLogicWorld>.Instance?._AgentManager?.GetAgent(instanceId);
            var z = a != null ? a.TryCast<Zombie>() : null;
            if (z == null || z.IsDead) return;
            AddCandidate(z, range);
        }
        catch { }
    }

    private void AddCandidate(Zombie z) => AddCandidate(z, SearchRange);

    private void AddCandidate(Zombie z, float range)
    {
        var tc = GetComp<TransformComponent>(z);
        if (tc == null) return;
        var pos = tc.Position;
        var p = pos + Vector3.up * BodyHeightOf(z, pos);
        var o = _model.position;
        var dx = p.x - o.x;
        var dz = p.z - o.z;
        if (dx * dx + dz * dz > range * range) return;
        _candIds.Add(z.InstanceId);
        _candPos.Add(p);
    }

    /// <summary>躯干中心高度=模型 Renderer 包围盒中心 − 逻辑位置；取不到模型时按 0.9m。
    /// 闪电落点用这个值，保证链电穿躯干而不是从头顶连过去。</summary>
    private float BodyHeightOf(Zombie z, Vector3 pos)
    {
        long id = 0;
        try { id = z.InstanceId; } catch { }
        if (id != 0 && BodyOffset.TryGetValue(id, out var cached)) return cached;
        var off = 0.9f;
        try
        {
            var amm = BaseSingleton<BattleShowWorld>.Instance?._AgentModelManager;
            AgentModel m = null;
            if (amm != null)
            {
                try { m = amm.GetAgentModelByLogicInstanceId(id); } catch { }
                if (m == null) try { m = amm.GetAgentModel(id); } catch { }
            }
            var obj = m != null ? m.Obj : null;
            if (obj != null)
            {
                var rs = obj.GetComponentsInChildren<Renderer>();
                var has = false;
                var b = new Bounds();
                for (var i = 0; i < rs.Length; i++)
                {
                    var r = rs[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (has) b.Encapsulate(r.bounds);
                    else { b = r.bounds; has = true; }
                }
                if (has && b.size.y > 0.2f)
                    off = Mathf.Clamp(b.center.y - pos.y, 0.25f, 1.5f);
            }
        }
        catch { }
        if (id != 0)
        {
            if (BodyOffset.Count > 512) BodyOffset.Clear();
            BodyOffset[id] = off;
        }
        return off;
    }

    private static T GetComp<T>(BaseAgent a) where T : BaseEntity
    {
        try
        {
            var dict = a != null ? a.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppType.From(typeof(T)), out var raw) ? raw?.TryCast<T>() : null;
        }
        catch { return null; }
    }

    private static Material LoadMat(AssetBundle bundle, string name)
    {
        try
        {
            var o = bundle.LoadAsset(name);
            return o == null ? null : o.TryCast<Material>();
        }
        catch (Exception e)
        {
            Trace("[TeslaFx] mat " + name + ": " + e.Message);
            return null;
        }
    }

    private void OnDestroy()
    {
        try { DeviceXpRuntime.DeviceFired -= OnDeviceFired; } catch { }
        try { if (_painter != null) _painter.Dispose(); }
        catch (Exception e) { Trace("[TeslaFx] dispose: " + e.Message); }
        _painter = null;
        _ready = false;
    }

    private static void Trace(string s) => EndlessGuardPlugin.Trace(s);
}
