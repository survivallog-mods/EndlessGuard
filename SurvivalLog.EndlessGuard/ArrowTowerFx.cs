using System;
using System.Collections.Generic;
using GameCore.HotUpdate.Battle.Logic;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔动画 + 追踪式弹道（Task 8，spec §4.4）：per-frame MonoBehaviour。注册方式照抄 GuardTicker
/// （ClassInjector.RegisterTypeInIl2Cpp + new GameObject + DontDestroyOnLoad + AddComponent）；
/// Update 必须 public + 双构造器（IntPtr/无参）——2026-10-25 教训：interop 注入类的引擎回调只桥接
/// public 生命周期方法，private 版场景切换后停止回调（OnDestroy 同理 public）。
///
/// 行为清单（spec §4.4 逐条；数值常量在 ArrowTowerFxMath，纯函数已单测）：
/// ① 待机扫描：Head 偏航 ±15° 正弦 2s 周期——仅无【活】目标追踪时（死亡目标的残箭段不算活）；
/// ② 红旗摆动：Flag 子物体绕局部 z ±16° 0.9s 周期——恒开（模板 pivot=旗杆轴、基准局部欧拉≈0，
///    绕 z=绕旗杆铰接的上下扑动，视觉即「摆动」）；
/// ③ 开火转向：订阅 DeviceXpRuntime.DeviceFired(家具id, muzzle, 目标id)，家具为箭塔(ArrowTower.Id=99005)
///    时锁定该目标：Head 朝目标实时位置水平转向（localEuler.y），角速度上限 120°/s
///    （ArrowTowerFxMath.StepYawDeg）；持续开火刷新追踪窗 2.5s（>1.5s 开火间隔自然续期），
///    超窗或目标死亡回①待机；
/// ④ 后坐：开火帧起 0.2s 三角波（RecoilTri×0.07），Head 沿自身局部 -x（模板 +x=托身朝前）位移。
///    裁决注释：预览版的托身/导轨/上膛箭分离后坐【不采用】，采整体 Head 单变换后坐（brief 认可的简化，
///    免子物体二次锚点登记）；
/// ⑤ 追踪式箭矢（必命中表现层——伤害已在 FireDevice 内即时结算，本物纯视觉）：开火帧从 Head 枪口位
///    （Head 局部 (0.20,0.055,0)=导轨前端）派生模板箭（克隆 ArrowTowerModel 结构件：杆 0.26+锥头+双羽，
///    米杆/铁头/米羽同色板；沿 +z 朝前便于 LookRotation 直用），逐帧朝目标实时位置直线折转
///    （步长=min(22·dt, 距离) 永不过冲→必收敛入命中半径 0.2）；消散三条件=命中半径 / 飞行 0.25s 上限 /
///    目标死亡改飞最后已知点后照常消散；并发 ≤8 超限杀最老（List 插入序=派生序）；
/// ⑥ 升级脉冲：消费 DeviceXpRuntime.RecordPulse 队列（其 RenderPulse() 逐帧排水转投 NotePulse）。
///    裁决降级注释：Battle.Logic 层拿不到 Renderer 直连是已知让步；即使经 Unity 根可达 renderer，
///    emissive 闪也需逐塔克隆材质且塔底材质可能是 URP SimpleLit（无 emission 通道），共享材质直改
///    会全塔串色——故采 Head 局部缩放脉冲 0.6s（PulseScale 首尾无缝回 1）。
///
/// 注册表（免逐帧 GetAllAgent——那是 DeviceXpRuntime.OnStep 2s 的活）：Dictionary&lt;家具id, TowerFx&gt;。
/// 喂养源=①DeviceXpRuntime.OnStep 巡检（其 DevicesInPlay 已每 2s 枚举在场设备，顺手 NoteTower）
/// +②DeviceFired 开火即注；清理=Update 每 2s 一轮 GetAgent 查活（查无/IsDead 即删；家具重建的新 id
/// 由喂养源重灌）。变换解析（fixwave4 状态组契约，模板=根直下 State01/02/03/Broken 互斥组）：先在根
/// 直下找【当前激活的状态组】，动画锚 Head/Flag 只在激活组=State01 时于组内按名解析（State02/03=全量
/// 静态克隆、StateBroken=残骸——控制方裁定 Fx 不得动画 State02 的头）；激活组漂移（断电/耐久掉档/修复）
/// 在 0.5s 重试轮检出并重锚，非 State01 期 Head=null → 逐塔动画静默空转。未解析/模型未生成期同 0.5s
/// 重试；官方兜底盒（无状态组）天然空转不报错。
///
/// cfg 门（裁决）：整组件创建只挂 Defense.ArrowTowerEnabled——FX 属塔本体；懒建点=ArrowTower.OnStep
/// （表注入稳态，2s 节奏，模板/材质三级降级链此时已就绪；比 EndlessGuardPlugin.Load 干净：Load 期
/// 模板未建、cfg 未稳，且不动基座插件文件）。总开关（Enabled）关时模型 Supply 拒供→无塔可动，天然静默。
/// 逐帧全程 try/catch + Trace 节流（同文只发一次，FireDevicePatch.WarnOnce 同款）；OnDestroy 退订
/// DeviceFired 并清箭。
/// </summary>
internal sealed class ArrowTowerFx : MonoBehaviour
{
    // ── 注册/订阅态 ──────────────────────────────────────────────────────────
    private static bool _subscribed;
    private static ArrowTowerFx _instance;
    private static GameObject _arrowTemplate;            // ⑤ 箭矢模板（DontDestroyOnLoad+失活，懒建一次）
    private static string _lastTrace;                    // trace 节流（同文一次）

    // ── 注册表与飞行箭（托管侧，主线程单写单读）────────────────────────────────
    private static readonly Dictionary<long, TowerFx> Towers = new();
    private static readonly List<ArrowFx> Arrows = new();   // 插入序=派生序（最老在 [0]）
    private static readonly List<long> _removals = new();   // 复用缓冲（免逐帧分配）
    private static float _cleanupAcc;                    // 2s 查活轮
    private static float _resolveAcc;                    // 0.5s 变换解析重试轮

    private const float MuzzleLocalX = 0.20f;            // ⑤ 枪口位（Head 局部）：导轨前端
    private const float MuzzleLocalY = 0.055f;

    // ── 双构造器 + public 生命周期（interop 桥接红线，见类头）────────────────────
    public ArrowTowerFx(IntPtr p) : base(p) { }
    public ArrowTowerFx() { }

    /// <summary>懒建入口（裁决见类头 cfg 门）：ArrowTower.OnStep 表稳态每 2s 调；已建则只补订阅。</summary>
    internal static void EnsureStarted()
    {
        try
        {
            if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value) return;
            if (_instance != null) { EnsureSubscribed(); return; }   // Unity == 重载：销毁后为 null → 走重建
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<ArrowTowerFx>())   // EndlessGuardPlugin 同款幂等注册
                ClassInjector.RegisterTypeInIl2Cpp<ArrowTowerFx>();
            var go = new GameObject("EndlessGuard.ArrowTowerFx");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ArrowTowerFx>();
            EnsureSubscribed();
            EndlessGuardPlugin.Trace("[TowerFx] ticker started");
        }
        catch (Exception e) { TraceOnce("[TowerFx] start: " + e.Message); }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        DeviceXpRuntime.DeviceFired += OnDeviceFired;
        _subscribed = true;
    }

    /// <summary>每帧入口：cfg 关=清箭停画（注册表保留，重开即续）；否则排水脉冲队列→逐塔动画→箭矢追踪。
    /// fixwave3 W3A：先喂一次性取证 dump（TowerModelDiag，完成态零开销）。</summary>
    public void Update()
    {
        try
        {
            TowerModelDiag.Update(Time.deltaTime);
            if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value)
            {
                DespawnAllArrows();
                return;
            }

            var dt = Time.deltaTime;
            var now = Time.time;
            DeviceXpRuntime.RenderPulse();                       // ⑥ 队列排水（转投 NotePulse）

            _resolveAcc += dt;
            var resolvePass = _resolveAcc >= 0.5f;               // 变换未就绪的半秒重试
            if (resolvePass) _resolveAcc = 0f;
            _cleanupAcc += dt;
            var cleanupPass = _cleanupAcc >= 2f;                 // 注册表查活轮
            if (cleanupPass) _cleanupAcc = 0f;

            _removals.Clear();
            foreach (var kv in Towers)                           // struct 枚举器，零分配
            {
                try
                {
                    if (TickTower(kv.Value, now, dt, resolvePass, cleanupPass)) _removals.Add(kv.Key);
                }
                catch (Exception e) { TraceOnce("[TowerFx] tick " + kv.Key + ": " + e.Message); }
            }
            for (int i = 0; i < _removals.Count; i++) Towers.Remove(_removals[i]);   // 枚举外删（C# 字典枚举期禁改）

            TickArrows(dt);
        }
        catch (Exception e) { TraceOnce("[TowerFx] update: " + e.Message); }
    }

    /// <summary>退订+清箭（public：生命周期桥接红线）。模板与塔注册表由 OnStep 侧重建，不在此毁模板。</summary>
    public void OnDestroy()
    {
        try
        {
            if (_subscribed)
            {
                DeviceXpRuntime.DeviceFired -= OnDeviceFired;
                _subscribed = false;
            }
            for (int i = Arrows.Count - 1; i >= 0; i--) KillArrow(i);
            Towers.Clear();
            if (ReferenceEquals(_instance, this)) _instance = null;
        }
        catch (Exception e) { TraceOnce("[TowerFx] onDestroy: " + e.Message); }
    }

    // ══════════════════════════ 逐塔动画（①②③④⑥）══════════════════════════
    // 返回 true=该条目已死（查无/IsDead），调用方在枚举外移除。

    private static bool TickTower(TowerFx t, float now, float dt, bool resolvePass, bool cleanupPass)
    {
        // 注册表陈旧清理：agent 查无/死亡 → 删（拆除/摧毁；重建的新 id 由喂养源重灌）
        if (cleanupPass && !AgentAlive(t.Id)) return true;

        // 变换解析：Root 没抓到（模型未生成/场景切换重建）→ 半秒一Retry；Root 在而（激活状态组漂移 ∨
        // 锚点缺）→ 同轮重锚。漂移=官方 FSM 切了激活组（State01↔02/03/Broken，fixwave4 契约）；重锚后
        // 非 State01 激活期 Head/Flag=null → 动画静默空转（State02 静头不动画，控制方裁定）。
        if (t.Root == null)
        {
            if (!resolvePass || !TryResolve(t)) return false;    // 未解析轮零 il2cpp 动画调用
        }
        else if (resolvePass && (StateGroupChanged(t) || t.Head == null || t.Flag == null))
        {
            TryResolveChildren(t);
        }

        // ② 红旗摆动（恒开；pivot=旗杆轴，基准局部欧拉≈0，绕 z=铰接扑动）
        if (t.Flag != null)
            t.Flag.localEulerAngles = new Vector3(t.FlagBaseEuler.x, t.FlagBaseEuler.y,
                t.FlagBaseEuler.z + ArrowTowerFxMath.FlagWaveDeg(now));

        if (t.Head == null) return false;                        // 兜底盒态：无弩机可动，静默空转

        // ⑥ 升级脉冲衰减（Head 局部缩放，降级裁决见类头⑥）
        var pulseScale = 1f;
        if (t.PulseAge >= 0f)
        {
            t.PulseAge += dt;
            pulseScale = ArrowTowerFxMath.PulseScale(t.PulseAge);
            if (t.PulseAge >= ArrowTowerFxMath.PulsePeriod) t.PulseAge = -1f;
        }

        // ④ 后坐三角波（-1=空闲哨兵；开火帧由 OnDeviceFired 置 0）
        var recoil = 0f;
        if (t.RecoilAge >= 0f)
        {
            t.RecoilAge += dt;
            recoil = ArrowTowerFxMath.RecoilTri(t.RecoilAge) * ArrowTowerFxMath.RecoilDist;
            if (t.RecoilAge >= ArrowTowerFxMath.RecoilPeriod) t.RecoilAge = -1f;
        }

        // ③ 目标方位（活→刷新最后已知位；死/查无→无活目标，回①）：
        var aimValid = false;
        var aim = Vector3.zero;
        if (t.TargetId != 0 && now < t.TrackUntil)
        {
            if (TryTargetPos(t.TargetId, ref t.LastKnown))
            {
                aim = t.LastKnown;
                aimValid = true;                                 // 活目标：③追踪
            }
            else if (t.HasLastKnown)
            {
                aim = t.LastKnown;                               // 已死：无【活】目标 → ①待机（箭矢自己飞最后已知点）
            }
        }

        // ①③ 转向统一走 120°/s 步进（待机正弦峰速≈47°/s 远低于上限，正弦不被削顶）。
        // 参照系统一（F1 修复）：两分支都先产出【世界系】yaw，再共用 ToLocalYawDeg 换算入局部——
        // 修复前待机分支在局部系（HeadBaseEuler 取自 localEulerAngles）上又减 parentYaw，塔根带
        // 摆放旋转时待机朝错心；现两分支同换算必同对。待机中心=塔根朝向+Head 基准局部朝向。
        var parentYaw = t.Head.parent != null ? t.Head.parent.eulerAngles.y : 0f;
        var targetYawWorld = aimValid
            ? Mathf.Atan2(aim.x - t.Head.position.x, aim.z - t.Head.position.z) * Mathf.Rad2Deg
            : ArrowTowerFxMath.IdleCenterWorldYawDeg(parentYaw, t.HeadBaseEuler.y)
              + ArrowTowerFxMath.IdleYawDeg(now);
        var yaw = ArrowTowerFxMath.StepYawDeg(t.Head.localEulerAngles.y,
            ArrowTowerFxMath.ToLocalYawDeg(targetYawWorld, parentYaw),
            ArrowTowerFxMath.TrackYawSpeedDeg * dt);

        t.Head.localEulerAngles = new Vector3(t.HeadBaseEuler.x, yaw, t.HeadBaseEuler.z);
        // ④ 后坐沿自身局部 -x（+x=托身朝前）：父空间回退向量按当前偏航合成
        t.Head.localPosition = recoil > 0f
            ? t.HeadBasePos + Quaternion.Euler(0f, yaw, 0f) * new Vector3(-recoil, 0f, 0f)
            : t.HeadBasePos;
        t.Head.localScale = t.HeadBaseScale * pulseScale;        // 恒等回写=无残留态（脉冲结束自动复位）
        return false;
    }

    // ══════════════════════════ ⑤ 追踪式箭矢 ═══════════════════════════

    private static void TickArrows(float dt)
    {
        for (int i = Arrows.Count - 1; i >= 0; i--)
        {
            var a = Arrows[i];
            try
            {
                a.Age += dt;
                var tr = a.Tr;
                if (tr == null || a.Age >= ArrowTowerFxMath.ArrowLifetime) { KillArrow(i); continue; }

                var aim = a.LastKnown;                           // 死/查无 → 飞往最后已知点（必命中语义）
                TryTargetPos(a.TargetId, ref aim);               // 活→aim 刷为实时位；死/查无→保持原值
                a.LastKnown = aim;
                var to = aim - tr.position;
                var dist = to.magnitude;
                var dir = dist > 1e-6f ? to / dist : tr.forward;
                tr.position += dir * ArrowTowerFxMath.ArrowStep(dist, dt);
                if (dist > 1e-6f) tr.rotation = Quaternion.LookRotation(dir);
                if (dist <= ArrowTowerFxMath.ArrowHitRadius) KillArrow(i);   // 命中消散（伤害已由 FireDevice 结算）
            }
            catch (Exception e)
            {
                TraceOnce("[TowerFx] arrow: " + e.Message);
                KillArrow(i);                                    // 病态箭不留活口
            }
        }
    }

    private static void KillArrow(int i)
    {
        var a = Arrows[i];
        try { if (a.Tr != null) Object.Destroy(a.Tr.gameObject); }
        catch (Exception e) { TraceOnce("[TowerFx] killArrow: " + e.Message); }
        Arrows.RemoveAt(i);
    }

    private static void DespawnAllArrows()
    {
        for (int i = Arrows.Count - 1; i >= 0; i--) KillArrow(i);
    }

    // ══════════════════════════ 事件与注册表 ═══════════════════════════

    /// <summary>DeviceFired 消费：仅箭塔（99005，含升级派生行 99152..99155——BaseCfgOf 归一）入口；
    /// 后坐+追踪窗+派箭。其他设备一条比较即出。</summary>
    private static void OnDeviceFired(long furnitureId, Vector3 muzzle, long targetId)
    {
        try
        {
            if (DeviceXpMath.BaseCfgOf(ResolveTowerCfg(furnitureId)) != ArrowTower.Id) return;
            if (!Towers.TryGetValue(furnitureId, out var t) || t == null)
                Towers[furnitureId] = t = new TowerFx { Id = furnitureId };

            t.RecoilAge = 0f;                                    // ④ 后坐起始
            if (targetId == 0) return;                           // 空目标：只后坐不派箭

            var aim = Vector3.zero;
            if (!TryTargetPos(targetId, ref aim)) return;            // 开火瞬间目标即不可解析：不派箭（转向/后坐照走）
            t.TargetId = targetId;
            t.LastKnown = aim;
            t.HasLastKnown = true;
            t.TrackUntil = Time.time + ArrowTowerFxMath.TrackWindow;   // ③ 追踪窗（持续开火自然续期）
            SpawnArrow(t, muzzle, targetId);
        }
        catch (Exception e) { TraceOnce("[TowerFx] fired: " + e.Message); }
    }

    /// <summary>注册表喂养（DeviceXpRuntime.OnStep 2s 巡检顺手调用；开火路径自带 Ensure 式建条目）。</summary>
    internal static void NoteTower(long furnitureId)
    {
        if (!Towers.ContainsKey(furnitureId)) Towers[furnitureId] = new TowerFx { Id = furnitureId };
    }

    /// <summary>F2 材质升级回调（ArrowTowerModel.EnsureGoodMaterial 调用）：①毁箭矢模板（下支箭经
    /// ArrowTowerModel.Mats 缓存用升级后材质重建——模板若早于材质升级建成，旧 Sprites/Default 材质
    /// 会一直沿用）；②重染已放置塔（其 renderer 挂的是旧逐色缓存材质，清缓存后须回挂新材质）。</summary>
    internal static void RetintRootsAndResetArrowTemplate()
    {
        if (_arrowTemplate != null)
        {
            Object.Destroy(_arrowTemplate);
            _arrowTemplate = null;
        }
        foreach (var kv in Towers)
        {
            var root = kv.Value != null ? kv.Value.Root : null;
            if (root == null) continue;                       // 未解析/兜底盒态：无我方 renderer 可染
            ArrowTowerModel.RetintDescendants(root);
        }
    }

    /// <summary>⑥ 脉冲转投（DeviceXpRuntime.RenderPulse 排水调用；elapsed 补偿排水延迟，过期丢）。</summary>
    internal static void NotePulse(long ownerId, float elapsed)
    {
        try
        {
            if (elapsed >= ArrowTowerFxMath.PulsePeriod) return;
            if (!Towers.TryGetValue(ownerId, out var t) || t == null) return;
            t.PulseAge = elapsed;
        }
        catch (Exception e) { TraceOnce("[TowerFx] pulse: " + e.Message); }
    }

    private static void SpawnArrow(TowerFx t, Vector3 muzzle, long targetId)
    {
        // 封顶即建即毁（非复用池——「对象池≤8」约束的语义是并发上限；派箭率 ~0.67 发/s/塔，
        // Instantiate 成本可忽略，T8 评审 M1 知悉项）：超限杀最老（List 插入序=派生序）
        if (Arrows.Count >= ArrowTowerFxMath.MaxArrows) KillArrow(0);
        if (_arrowTemplate == null && !BuildArrowTemplate()) return;

        var clone = Object.Instantiate(_arrowTemplate);
        var tr = clone.transform;
        var from = t.Head != null
            ? t.Head.TransformPoint(new Vector3(MuzzleLocalX, MuzzleLocalY, 0f))
            : muzzle;                                             // 事件 muzzle 兜底（Head 未就绪的极短窗口）
        tr.position = from;
        var to = t.LastKnown - from;
        tr.rotation = to.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(to)
            : t.Head != null ? t.Head.rotation : Quaternion.identity;
        clone.SetActive(true);
        Arrows.Add(new ArrowFx { Tr = tr, TargetId = targetId, LastKnown = t.LastKnown, Age = 0f });
    }

    /// <summary>箭矢模板懒建（首次派箭一次）：克隆 ArrowTowerModel 结构件/色板/材质（全走 ArrowTowerModel.
    /// Mat 缓存=与塔体同一条四级降级链，F2 裁定：不私建材质；模板早于材质升级建成的旧态由
    /// RetintRootsAndResetArrowTemplate 毁掉重建），沿 +z 朝前（tr.rotation=LookRotation 直用）；
    /// 比例=塔载上膛箭（spec §4.3：杆 0.26+锥头+双羽）。</summary>
    private static bool BuildArrowTemplate()
    {
        GameObject t = null;
        try
        {
            ArrowTowerModel.EnsureRegistered();                   // 材质三级降级链在塔模板内建成
            t = new GameObject("EG_ArrowTemplate");
            t.SetActive(false);
            Object.DontDestroyOnLoad(t);
            var tr = t.transform;
            ArrowTowerModel.Box(new Vector3(0.02f, 0.02f, 0.26f), Vector3.zero, ArrowTowerModel.ArrowShaft, tr,
                role: TowerTextures.Role.PlankSmall);             // 杆（fixwave3 W3B：木纹杆，与塔体箭杆同款）
            ArrowTowerModel.Pyramid(0.02f, 0.06f, new Vector3(0f, 0f, 0.13f), ArrowTowerModel.IronLight, tr,
                new Vector3(90f, 0f, 0f));                        // 锥头（+y→+z 转位），base 贴杆前端、apex z=0.19
            ArrowTowerModel.Box(new Vector3(0.004f, 0.035f, 0.07f), new Vector3(0f, 0f, -0.09f), ArrowTowerModel.FletchCream, tr);  // 双羽
            ArrowTowerModel.Box(new Vector3(0.035f, 0.004f, 0.07f), new Vector3(0f, 0f, -0.09f), ArrowTowerModel.FletchCream, tr);
            _arrowTemplate = t;
            EndlessGuardPlugin.Trace("[TowerFx] arrow template built");
            return true;
        }
        catch (Exception e)
        {
            if (t != null) Object.Destroy(t);                     // 半途异常不留半成品模板
            TraceOnce("[TowerFx] arrow template: " + e.Message);
            return false;
        }
    }

    // ══════════════════════════ il2cpp 解析助手（null/异常兜底，DeviceXpRuntime 同款）══════════════════════════

    /// <summary>组件按 Type 索引直达（DeviceXpRuntime.GetComponent 同款已验证模式）。</summary>
    private static T GetComponent<T>(BaseAgent a) where T : GameCore.HotUpdate.BaseEntity
    {
        try
        {
            var dict = a != null ? a.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppInterop.Runtime.Il2CppType.From(typeof(T)), out var raw)
                ? raw?.TryCast<T>() : null;
        }
        catch { return null; }
    }

    /// <summary>家具配置 id（开火过滤箭塔用）；任何不可达=0。</summary>
    private static int ResolveTowerCfg(long furnitureId)
    {
        try
        {
            var f = ResolveAgent(furnitureId)?.TryCast<Furniture>();
            return f != null ? f.AgentConfigId : 0;
        }
        catch { return 0; }
    }

    /// <summary>注册表查活：世界未建/查无/IsDead → false（条目删）。</summary>
    private static bool AgentAlive(long id)
    {
        try
        {
            var a = ResolveAgent(id);
            return a != null && !a.IsDead;
        }
        catch { return false; }
    }

    private static BaseAgent ResolveAgent(long id)
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return null;
            var am = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance?._AgentManager;
            return am?.GetAgent(id);
        }
        catch { return null; }
    }

    /// <summary>⑤③ 目标实时位置：活僵尸→TransformComponent.Position 刷新 lastKnown 返回 true；
    /// 死/查无/组件不可读 → false 且 lastKnown 保持原值（调用方回落：箭飞死亡点、Head 回待机，必命中语义）。</summary>
    private static bool TryTargetPos(long targetId, ref Vector3 lastKnown)
    {
        try
        {
            var z = ResolveAgent(targetId)?.TryCast<Zombie>();
            if (z == null || z.IsDead) return false;
            var tc = GetComponent<TransformComponent>(z);
            if (tc == null) return false;
            lastKnown = tc.Position;
            return true;
        }
        catch { return false; }
    }

    /// <summary>变换解析：Root=TransformComponent.transform（interop 实证公开属性，mdump 2026-10-30；
    /// get_transform 为其访问器，C# 侧只能属性访问——CS0571 实证）；
    /// 成功即锚 Head/Flag 并拍基准局部位姿（重建模型后以静止态重拍，同时复位后坐/脉冲计时）。</summary>
    private static bool TryResolve(TowerFx t)
    {
        try
        {
            var f = ResolveAgent(t.Id)?.TryCast<Furniture>();
            if (f == null) return false;
            var tc = GetComponent<TransformComponent>(f);
            var root = tc != null ? tc.transform : null;   // interop：get_transform 暴露为属性 transform（CS0571 实证）
            if (root == null) return false;
            t.Root = root;
            t.RecoilAge = -1f;
            t.PulseAge = -1f;
            TryResolveChildren(t);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Head/Flag 锚定（fixwave4 状态组契约）：先取根直下【当前激活的状态组】记入 t.ActiveGroup；
    /// 仅激活组=State01（完好态）时在组内按名解析动画锚（Head=弩机/Flag=旗，State01/Mesh/G_Tripo05A 直
    /// 子物体，模板结构保证）——State02/03（静态克隆）与 StateBroken（残骸）激活期锚点=null，逐塔动画
    /// 静默空转（State02 静头不动画，控制方裁定）。官方兜底盒（无状态组）同样空转；State01 在而锚点缺
    /// 才打一次性告警。</summary>
    private static void TryResolveChildren(TowerFx t)
    {
        try
        {
            var root = t.Root;
            if (root == null) return;
            var group = FindActiveStateGroup(root);
            t.ActiveGroup = group;
            var normal = group != null && string.Equals(group.name, StateNormal, StringComparison.Ordinal);
            Transform head = null, flag = null;
            if (normal) FindAnchors(group, ref head, ref flag);
            t.Head = head;
            t.Flag = flag;
            if (head != null)
            {
                t.HeadBasePos = head.localPosition;
                t.HeadBaseEuler = head.localEulerAngles;
                t.HeadBaseScale = head.localScale;
            }
            if (flag != null) t.FlagBaseEuler = flag.localEulerAngles;
            if (normal && (head == null || flag == null))
                TraceOnce("[TowerFx] anchor miss（State01 在而锚点缺，模板异常？）: "
                          + (head == null ? "Head " : "") + (flag == null ? "Flag" : ""));
        }
        catch (Exception e) { TraceOnce("[TowerFx] resolveChildren: " + e.Message); }
    }

    /// <summary>完好态组名（唯一挂动画锚的状态组；语义=durability&gt;70%，见 ArrowTowerModel.BuildTemplate）。</summary>
    private const string StateNormal = "State01";

    /// <summary>根直下当前激活的状态组（StateXX 互斥激活；无=官方兜底盒/全暗异常）。</summary>
    private static Transform FindActiveStateGroup(Transform root)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c == null) continue;
            try
            {
                if (c.gameObject.activeInHierarchy && IsStateGroupName(c.name)) return c;
            }
            catch { }
        }
        return null;
    }

    private static bool IsStateGroupName(string n) =>
        n == "State01" || n == "State02" || n == "State03" || n == "StateBroken";

    /// <summary>组内按名递归找 Head/Flag（模板=组/Mesh/G_Tripo05A 直下，深度 3 止；先到先得）。</summary>
    private static void FindAnchors(Transform group, ref Transform head, ref Transform flag)
    {
        for (int i = 0; i < group.childCount; i++)
        {
            var c = group.GetChild(i);
            if (c == null) continue;
            ScanAnchors(c, ref head, ref flag, 0);
            if (head != null && flag != null) return;
        }
    }

    private static void ScanAnchors(Transform t, ref Transform head, ref Transform flag, int depth)
    {
        try
        {
            var n = t.name;
            if (head == null && n == "Head") { head = t; return; }
            if (flag == null && n == "Flag") { flag = t; return; }
            if (depth >= 3) return;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;
                ScanAnchors(c, ref head, ref flag, depth + 1);
                if (head != null && flag != null) return;
            }
        }
        catch { }
    }

    /// <summary>激活状态组漂移检测（fixwave4）：当前激活组与缓存 t.ActiveGroup 不同（断电→State02、
    /// 掉档→State03、死亡→StateBroken、修复回 State01、或旧组被毁）=true 触发重锚。半秒一轮调用，
    /// 每塔 ≤5 次 GetChild+名字比较（零分配）。</summary>
    private static bool StateGroupChanged(TowerFx t)
    {
        try
        {
            var root = t.Root;
            if (root == null) return false;
            var cur = FindActiveStateGroup(root);
            if (cur == null) return t.ActiveGroup != null;   // 全暗：视为漂移（重锚清锚点）
            if (t.ActiveGroup == null) return true;          // 首次/组被毁：重锚
            return !string.Equals(cur.name, t.ActiveGroup.name, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>trace 节流（同文只发一次；换文重发，FireDevicePatch.WarnOnce 同款）。</summary>
    private static void TraceOnce(string line)
    {
        if (string.Equals(line, _lastTrace, StringComparison.Ordinal)) return;
        _lastTrace = line;
        EndlessGuardPlugin.Trace(line);
    }

    // ══════════════════════════ 状态载体 ═══════════════════════════

    /// <summary>单台箭塔的 FX 态（注册表条目；键=家具实例 id）。</summary>
    private sealed class TowerFx
    {
        public long Id;
        public Transform Root;           // 家具 Unity 根（TransformComponent.get_transform）
        public Transform ActiveGroup;    // 当前激活状态组 StateXX（fixwave4；漂移检测基线，State01 期才挂锚）
        public Transform Head;           // 动画锚点「Head」（State01/Mesh/G_Tripo05A 直下；非 State01 激活期=null）
        public Transform Flag;           // 动画锚点「Flag」（同上；pivot=旗杆轴）
        public Vector3 HeadBasePos;      // 解析时拍的基准局部位姿（动画只写增量、恒等回写）
        public Vector3 HeadBaseEuler;
        public Vector3 HeadBaseScale;
        public Vector3 FlagBaseEuler;
        public long TargetId;            // ③ 追踪目标实例 id（0=无）
        public float TrackUntil;         // ③ 追踪窗截止（Time.time 域）
        public float RecoilAge = -1f;    // ④ 后坐计时（-1=空闲）
        public float PulseAge = -1f;     // ⑥ 脉冲计时（-1=空闲）
        public Vector3 LastKnown;        // 目标最后已知位（死亡回落飞行终点/待机回退）
        public bool HasLastKnown;
    }

    /// <summary>单支飞行箭的 FX 态。</summary>
    private sealed class ArrowFx
    {
        public Transform Tr;
        public long TargetId;
        public Vector3 LastKnown;        // 最后已知目标位（追踪源；死亡后固化）
        public float Age;
    }
}
