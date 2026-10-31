using System;
using System.IO;
using SurvivalLog.ModShared;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔「99005」（功能B，spec §4.1/§4.2）：四表内存注入 + 防线 Lv4 解锁登记。
/// fixwave2 追加：派生等级行注入（99112..99155，DeviceXpMath.RowIdFor 方案）——升级=换配置行架构的
/// "行"从这来：每基础设备 lv2..5 各一行 Config_Furniture + Config_TowerDevice，全字段克隆源行
///（官方 36001-36004 从活字典读=gamekb 内容；99005 从我方注入行克隆），仅覆盖 ID/名称+“ Lv.N”/
/// 目标耐久/InCodex=false（家具行）与 Attack/Interval/Range/SelfDamagePerBite（塔数值行）。
/// 行：Config_TowerDevice/Config_Furniture/Config_Item(包裹)/Config_ProductionList(配方) 统一 ID=99005
///（99xxx 预留段，作者裁决 2026-10-30：紧贴官方空位必撞号）；Config_DefenseLv Lv4 unlock_recipes
/// [403]→[403,99005] 且 Info_Local 追加「新增：解锁 箭塔」（与电锯同档）。
/// 时机/幂等：照抄 ScavengePlans.Inject/OnConfigStep 定案模式——GuardTicker 2s 巡检（async 状态机
/// MoveNext 禁挂教训）、BagTable.Cm() 单例就绪检查、dict 级 ContainsKey 判重、哨兵复查（表被官方
/// Initialize 整体重建后自动重放；稳态静默零写入零 trace）。表是进程内存态，不写任何存档。
/// 本地化：Name/Name_Local、Des/Des_Local、ItemName(_Local)、ShopName(_Local) 全部直写字面中文
///（官方消费方把键走 GetLocalText→GetLocalTxt，键 miss 原样透传输入串，ScavengePlans 2026-10-25 实机截图实锤）。
/// 全列表字段非 null（官方消费方直接 .Count，null 即 NRE——ScavengePlans.NewCondSet 教训）。
/// ═══ fixwave5（2026-10-31，两场 live trace 定案 race 根因）═══
/// F1 快注入：存档家具实例化可早于表注入落地（旧门=ConfigManager 就绪 + 2s 步进），输掉竞态的会话里
/// 设备以「无模型」态出生（官方 LoadGameObject(P_Tripo05) miss 返 null、我方 supply 尚未 armed）。
/// FastInjectTicker（0.25s，EndlessGuardPlugin.cs）只跑表注入快路 TryInjectFast，首次全绿即退役。
/// F2 失联模型自愈（CreateFurnitureShow 重建）——官方创作者参数逐位考证（dump.cs 2026-10-23_26157732）：
/// - 方法=AgentModelManager.CreateFurnitureShow(long instanceId, int configId, long showInstanceId,
///   Vector3 position, Quaternion rotation, InstanceIdType type, int floorId)（dump.cs:353453，私有实例
///   方法=EnumNotify_Furniture.CreateFurnitureShow=1005（dump.cs:69469）的官方事件处理器——放家具
///   show 模型的正路。同族辨析：CreateModelShow=EnumNotify_Show 2008（通用代理模型，dump.cs:70133）、
///   CreateBagFurnitureShow=EnumNotify_Furniture 1004（包裹家具，dump.cs:69468）——均非本例。
///   私有可直调：interop 代理全公开（本仓先例：OnSelectFurnitureShow/FireDevice 等私有成员已挂/已调）。
/// - instanceId←Furniture.InstanceId（BaseEntity 公开，dump.cs:106044；cmp 实证=AgentModel.logicInstanceId）；
///   configId←Furniture.AgentConfigId（BaseAgent 0x30 公开，dump.cs:374680）；
///   showInstanceId←Furniture.ShowInstanceId（BaseAgent 0x38 公开，dump.cs:374683——TowerSelDiag cmp
///   实证的 GetAgentModel 有效键）；
///   position/rotation←TransformComponent.Position/.Rotation（public，dump.cs:373618/373619；
///   逻辑位姿权威值，DeviceXpRuntime.CellKeyOf 同源）；type←InstanceIdType.Furniture=1
///   （dump.cs:106129-106135：Default=0/Furniture=1/Item=2/Unit=3…）；
///   floorId←ConfigManager.GetFloorGroup(Furniture.MapConfigId)（dump.cs:54502/375671，公开）——
///   证据链：AgentModel.OnInit(…, int floorId=0, …)（dump.cs:352682）→AgentModel.FloorId（0xF8，
///   dump.cs:352424，cmp 实机实证防御设备 floor=1）；楼层值域={1(1F),101(B1),102(2F)}
///   （HotKeyManager 常量，dump.cs:362192-362194）；GetFloorGroup 取值即此组号，不可达/非正退 1（1F）。
/// - 防双建门：三键（GetAgentModel(showId)/GetAgentModel(instId)/GetAgentModelByLogicInstanceId(instId)）
///   任一解析到活模型（Obj 非 null 可读）即不碰——「存在但隐藏」态 Obj 活=跳过，故不会对隐藏模型重复
///   CreateFurnitureShow、无 RefreshCollider 双注册 collider（brief CAUTION）；上限 3 次/台/会话
///   （Dictionary&lt;long,int&gt;，读档/换档清零），give-up 每台一次。
/// interop 属性名经 mdump 实测（E:\SteamLibrary\...\BepInEx\interop\HotUpdate.dll，2026-10-30，
/// 实测命令：mdump.exe HotUpdate.dll "Config_TowerDevice,Config_Furniture,Config_Item,Config_ProductionList,Config_DefenseLv,ConfigManager"）：
/// - Config_TowerDevice：ID/AttackPriority/MaxAttackers/IsWalkThrough/ExclusiveCharacter/Attack/
///   AttackInterval(f)/AttackRange(f)/BurstCount/HitSlowRatio(f)/HitSlowSeconds(f)/CounterDamage/
///   SelfDamagePerBite/PowerCostPerHit(f)/MaxHitTargets（f=Single；MainKey 只读不写）；
/// - Config_Furniture：ID/Name(_Local)/FsmName/ResName/BagResName/ICON/FurniturePrice/InstallTime/Des(_Local)/
///   FurnitureType/ShopPage/SlotType/isShowName/isShowHP/FurnitureHP/FurnitureHPMax/FurnitureFunc|RemoveFunc|
///   MoveFunc|RemoveGet|RotProductOverride|RotSourceSubCategory(List&lt;int&gt;)/ActionId/IsElectrical/ElectricalType/
///   PowerCost/PowerMode/InCodex 等全字段可写；
/// - Config_Item：ID/ItemName(_Local)/ItemDes1(_Local)/ItemDes2(_Local)/StackLimit/Category/SubCategory/
///   Size|It_Rot_Product_Id|OverdueDebuff|DebuffPow(List&lt;int&gt;)/price/weight/Life/UseTimes/TargetFurnitureID/
///   Icon/WebIcon/Model/InCodex/UseAction；
/// - Config_ProductionList：ID/ShopName(_Local)/IsUseable/MaterialList|ProductID|FailedID|perfect_item_id(List&lt;int&gt;)/
///   Level/DiscountRate(f)/LifeMin/discovery_exp/InCodex/NoteCategory/defense_exp；
/// - Config_DefenseLv：Lv/EXP/Info(_Local)/required_condition|unlock_recipes(_P1..P4)(List&lt;int&gt;)；
/// - ConfigManager 字典：_Config_TowerDevice_Dict/_Config_Furniture_Dict/_Config_Item_Dict/
///   _Config_ProductionList_Dict/_Config_DefenseLv_Dict（均 Dictionary&lt;int,行&gt;）。
/// 用例裁定：包裹 UseAction 复用官方 100026004（拆封动作行与物品解耦——安装目标取物品行
/// TargetFurnitureID，动作行只承载文案/时长/音效，全表同构实证）；专用动作行=第五张表越出 Task 6
/// 范围，代价仅拆封进度文案显示「拆封 机械电锯包裹中…」，实机验收时若碍眼再立新任务注入 Config_Action。
/// </summary>
internal static class ArrowTower
{
    internal const int Id = 99005;
    internal const string ResName = "P_Tripo05";
    internal static readonly int[] Materials = { 20005, 20005, 20004, 20002 };   // 木片×2+铁皮+碎玻璃（spec §4.2）

    private const int DefenseLvKey = 4;                       // 工事技师档（与电锯 403 同档解锁）
    private const string Lv4AppendText = "\\n新增：解锁 箭塔";  // 现场格式为字面 \n 两字符（gamekb dump 实证），同款追加
    private const int ChainsawUnpackAction = 100026004;       // 官方拆封动作（复用，见类头裁定）
    // F6 图标（2026-10-31 playtest：包裹图标=电锯图标，作者不满）：改借官方材料「木板」20106 的图标
    //（gamekb Config_Item 20106 逐字实测），与四件套设备图标全部区分。
    // wave8 起木板只作退路：专属图标（自绘塔+弩 png）走 WebUI/Res 磁盘通道—— crafting UI（ToolTable.html）
    // 把 <img src=product.icon> 直绑相对路径，"../../Res/..." 按普通文件从 <游戏根>/SurvivalLog_Data/
    // StreamingAssets/WebUI/Res/ 解析（控制器实证，该目录 ~480MB 散装 png）；落盘成功 Icon/WebIcon 都指
    // 专属文件，失败/目录契约不成立则维持 F6 木板同款（判路见 EnsureIconFile/IconNow）。
    private const string IconTex = "Assets/RuntimeAssets/Texture/Material/UI_Item_Icon_Mat_M_muban";              // 木板图标（F6，退路）
    private const string IconWebMat = "../../Res/Material/UI_Item_Icon_Mat_M_muban.png";                          // 木板 web 图标（20106 同款，退路）
    private const string IconCustom = "../../Res/Icon/eg_arrow_tower.png";                                        // wave8 专属图标（WebUI/Res 磁盘通道）
    private const string IconResourceName = "SurvivalLog.EndlessGuard.Resources.eg_arrow_tower.png";              // 内嵌资源名（csproj EmbeddedResource）
    private const string IconWeb = "../../Res/Furniture/UI_Item_Icon_Furnit_M_831300316.png";                     // 家具行 ICON 维持电锯（F6 只改物品行）
    private const string PackageModel = "Assets/RuntimeAssets/Prefabs/Furniture/P_Paperbox_009_S1_M02";           // 与 26004 同款纸盒

    /// <summary>GuardTicker 2s 巡检入口（照抄 ScavengePlans.OnConfigStep：总开关下再挂本功能开关；
    /// Cm() 单例未建→下轮再试；五张 dict 任一未装载→下轮再试；哨兵齐→稳态静默返回）。
    /// fixwave2：派生等级行（99112..99155）随四表一起注入/哨兵重放；另有成长 cfg 新鲜度哨兵——
    /// 步长/系数类 cfg 改值后重建派生行内容（组件面板在下次官方重初始化时吸收新值）。
    /// fixwave5：表工作抽为 EnsureTables 共享核（F1 快注入同一条路）；稳态轮追加 F2 失联模型自愈。</summary>
    internal static void OnStep()
    {
        if (!EnsureTables()) return;          // 表未就绪/本轮注入后仍未全绿（如 Lv4 行漂移）→ 下轮再试
        ArrowTowerFx.EnsureStarted();         // 表稳态懒建动画/弹道 ticker（门=ArrowTowerEnabled，裁决见 ArrowTowerFx 类头；T8）
        HealModellessDevices();               // fixwave5 F2：无模型设备官方创作者重建（防双建门内）
    }

    /// <summary>fixwave5 F1 快注入快路（FastInjectTicker 0.25s 消费）：只做表注入/哨兵这一件事，
    /// 不含 FX/heal 全量巡检。返回 true=五哨兵全绿（表已稳态，快 ticker 收到即退役）。
    /// 与 OnStep 共用 EnsureTables 同一条幂等路径（先到者赢，后到者空转一轮即出）。</summary>
    internal static bool TryInjectFast() => EnsureTables();

    /// <summary>表注入共享核：门（功能开关/Cm 就绪/五 dict 装载）→ 哨兵复查 → 全绿直返（顺带 Arm，
    /// 模板仍等首次 supply 才懒建）→ 不全绿则 Inject（行级幂等）后复核。返回 false=本轮尚未稳态
    ///（装表中/Lv4 行漂移等），调用方下轮再试。fast ticker 退役与 OnStep 稳态判定同源于此。</summary>
    private static bool EnsureTables()
    {
        if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value) return false;   // 关=不注入（已注入行为进程内存态，本局保留，重启干净）
        var cm = BagTable.Cm();
        if (cm == null) return false;

        var td = cm._Config_TowerDevice_Dict;
        var fu = cm._Config_Furniture_Dict;
        var it = cm._Config_Item_Dict;
        var pl = cm._Config_ProductionList_Dict;
        var dl = cm._Config_DefenseLv_Dict;
        if (td == null || fu == null || it == null || pl == null || dl == null) return false;   // 装表中（2026-10-24 实锤：Cm() 就绪≠dict 就绪），下轮再试

        EnsureIconFile();                     // wave8：专属图标落盘先行（结果定 99005 行 Icon/WebIcon 走专属还是木板退路；快注入/OnStep 同此路）

        if (SentinelsGreen(td, fu, it, pl, dl))
        {
            ArrowTowerModel.Arm();            // F2：只武装不建——模板推迟到首次 supply（场景内、设备在场）再拼
            return true;
        }
        Inject(td, fu, it, pl, dl);
        return SentinelsGreen(td, fu, it, pl, dl);   // Inject 逐字段拍平=当轮应绿；false=表漂移（Lv4 行缺失等），下轮重试
    }

    /// <summary>五哨兵复查（Plans[0] 模式，求值顺序与短路保持 fixwave2 原语义）：官方重试/换语言重跑
    /// Initialize 会整体重建 _Config_*_Dict——四表任一丢 99005、派生行缺任一、或 Lv4 丢解锁项即判不全绿
    ///（Inject 内部逐行 ContainsKey/Contains 幂等，重放无副作用）。</summary>
    private static bool SentinelsGreen(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        bool rowsMissing = !td.ContainsKey(Id) || !fu.ContainsKey(Id) || !it.ContainsKey(Id) || !pl.ContainsKey(Id);
        bool derivedMissing = !rowsMissing && DerivedRowsMissing(td, fu);
        bool legacyNeedsSync = !rowsMissing && !derivedMissing && LegacyRowNeedsSync(fu, it);
        bool lv4Missing = !dl.TryGetValue(DefenseLvKey, out var lv4) || lv4 == null || !Contains(lv4.unlock_recipes, Id);
        bool xpStale = !rowsMissing && !derivedMissing && !legacyNeedsSync && DerivedRowsStale();
        return !rowsMissing && !derivedMissing && !legacyNeedsSync && !lv4Missing && !xpStale;
    }

    // =====================================================================
    // fixwave5 F2：失联模型自愈（race 幸存者链——官方 CreateFurnitureShow 重建，参数考证见类头）
    // =====================================================================

    /// <summary>每台家具实例的已用自愈尝试数（键=Furniture.InstanceId；读档/换档=slot 变更即清零——
    /// 新场景的实例是新的 show，旧计数作废）。</summary>
    private static readonly System.Collections.Generic.Dictionary<long, int> HealAttempts = new System.Collections.Generic.Dictionary<long, int>();

    /// <summary>已打过 give-up 行的实例（每台一次，防刷屏；同随 slot 变更清零）。</summary>
    private static readonly System.Collections.Generic.HashSet<long> HealGaveUp = new System.Collections.Generic.HashSet<long>();

    private static string _healSlot;

    /// <summary>无模型设备自愈（2s 稳态轮尾执行）：逐台受追踪设备（官方 36001-36004+我方 99005/991xx
    /// ——race 同样可能搁浅派生行设备）三键试解 AgentModel，全 miss 且 Obj 无活体才调官方
    /// CreateFurnitureShow 重建（参数映射逐位考证见类头）。稳态成本=每台 2~3 次字典查。全程 try/catch
    /// 单台隔离；零存档写入（show 层纯进程内存态）。</summary>
    private static void HealModellessDevices()
    {
        try
        {
            var slot = DeviceXpRuntime.SlotKey;
            if (_healSlot == null) _healSlot = slot;
            else if (!string.Equals(_healSlot, slot, StringComparison.Ordinal))
            {
                _healSlot = slot;                       // 读档/换档：尝试计数与 give-up 名单作废（brief 口径）
                HealAttempts.Clear();
                HealGaveUp.Clear();
            }

            var amm = LiveModelManager();
            if (amm == null) return;                    // 主菜单/场景切换空窗：静默下轮
            foreach (var f in TrackedDevicesInPlay())
            {
                try
                {
                    if (HasLiveModel(amm, f)) continue;   // 模型在（含存在但隐藏）→ 不碰（防双建/双 collider 注册）
                    var id = f.InstanceId;
                    if (!HealAttempts.TryGetValue(id, out var n)) n = 0;
                    if (n >= MaxHealAttemptsPerInstance)
                    {
                        if (HealGaveUp.Add(id))
                            EndlessGuardPlugin.Trace("[Tower] model heal gave up cfg=" + f.AgentConfigId
                                                     + " showId=" + f.ShowInstanceId);
                        continue;
                    }
                    n++;
                    HealAttempts[id] = n;
                    CreateFurnitureShowVia(amm, f);
                    EndlessGuardPlugin.Trace("[Tower] model healed cfg=" + f.AgentConfigId + " attempt=" + n);
                }
                catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] heal probe: " + e.Message); }
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] heal: " + e.Message); }
    }

    private const int MaxHealAttemptsPerInstance = 3;

    /// <summary>show 模型管理器可达性（TowerSelDiag.LiveAmm 同款访问路径：BattleShowWorld 单例 →
    /// _AgentModelManager；不可达=null 不抛）。</summary>
    private static GameCore.HotUpdate.Battle.Show.AgentModelManager LiveModelManager()
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.Battle.Show.BattleShowWorld>.IsInstanceCreated) return null;
            return GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.Battle.Show.BattleShowWorld>.Instance?._AgentModelManager;
        }
        catch { return null; }
    }

    /// <summary>在场受追踪设备枚举（DeviceXpRuntime.DevicesInPlay 同款口径：GetAllAgent → 滤 IsDead →
    /// TryCast&lt;Furniture&gt; → IsTrackedDevice 含派生行；单代理病态跳过不拖垮整轮）。</summary>
    private static System.Collections.Generic.IEnumerable<GameCore.HotUpdate.Battle.Logic.Furniture> TrackedDevicesInPlay()
    {
        if (!GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.Battle.Logic.BattleLogicWorld>.IsInstanceCreated) yield break;
        Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.Battle.Logic.BaseAgent> all = null;
        try { all = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.Battle.Logic.BattleLogicWorld>.Instance?._AgentManager?.GetAllAgent(); }
        catch { yield break; }
        if (all == null) yield break;
        for (int i = 0; i < all.Count; i++)
        {
            GameCore.HotUpdate.Battle.Logic.Furniture f = null;
            try
            {
                var a = all[i];
                if (a == null || a.IsDead) continue;
                f = a.TryCast<GameCore.HotUpdate.Battle.Logic.Furniture>();
                if (f == null || !DeviceXpMath.IsTrackedDevice(f.AgentConfigId)) continue;
            }
            catch { continue; }
            yield return f;
        }
    }

    /// <summary>模型在册判定（防双建门，brief CAUTION 落点）：三键只读试解——showId（TowerSelDiag cmp
    /// 实证的官方注册键）→ instId → logicId；任一键解析到「Obj 活体」=健康（存在但隐藏也是活体）→
    /// 不重建。身份不可读/模型壳不可读=保守视为在册不碰（宁漏勿炸、宁漏勿双建）。</summary>
    private static bool HasLiveModel(GameCore.HotUpdate.Battle.Show.AgentModelManager amm, GameCore.HotUpdate.Battle.Logic.Furniture f)
    {
        long showId, instId;
        try { showId = f.ShowInstanceId; instId = f.InstanceId; }
        catch { return true; }
        GameCore.HotUpdate.Battle.Show.AgentModel m = null;
        try { m = amm.GetAgentModel(showId); } catch { }
        if (m == null) try { m = amm.GetAgentModel(instId); } catch { }
        if (m == null) try { m = amm.GetAgentModelByLogicInstanceId(instId); } catch { }
        if (m == null) return false;
        UnityEngine.GameObject obj = null;
        try { obj = m.Obj; } catch { return true; }
        return ObjAlive(obj);
    }

    /// <summary>Unity 对象活体判定：interop 引用非 null 且可读名（已毁对象读属性会抛/返空——双保险）。</summary>
    private static bool ObjAlive(UnityEngine.GameObject o)
    {
        if (o == null) return false;
        try { var _ = o.name; return true; }
        catch { return false; }
    }

    /// <summary>官方创作者重建调用（类头「F2」参数逐位考证；TransformComponent 不可达=本轮放弃该台，
    /// 不猜位姿——错位重建比不重建更糟）。floorId=GetFloorGroup(MapConfigId)，非正退 1（1F）。</summary>
    private static void CreateFurnitureShowVia(GameCore.HotUpdate.Battle.Show.AgentModelManager amm, GameCore.HotUpdate.Battle.Logic.Furniture f)
    {
        var tc = TransformComponentOf(f);
        if (tc == null) throw new InvalidOperationException("TransformComponent unreachable (no position source; skip heal)");
        var pos = tc.Position;
        var rot = tc.Rotation;
        amm.CreateFurnitureShow(f.InstanceId, f.AgentConfigId, f.ShowInstanceId, pos, rot,
            GameCore.HotUpdate.InstanceIdType.Furniture, ResolveFloorId(f));
    }

    /// <summary>show 楼层：ConfigManager.GetFloorGroup(MapConfigId)（dump.cs:54502/375671；值域
    /// 1/101/102=HotKeyManager 常量 dump.cs:362192-362194），不可达/非正退 1（cmp 实证防御设备 floor=1）。</summary>
    private static int ResolveFloorId(GameCore.HotUpdate.Battle.Logic.Furniture f)
    {
        try
        {
            if (GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ConfigManager>.IsInstanceCreated)
            {
                var floor = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ConfigManager>.Instance?.GetFloorGroup(f.MapConfigId) ?? 0;
                if (floor > 0) return floor;
            }
        }
        catch { }
        return 1;
    }

    /// <summary>受追踪家具的 TransformComponent（组件按 Type 索引直达，ArrowTowerModel.TransformOf
    /// 已验证模式；不可达=null）。</summary>
    private static GameCore.HotUpdate.Battle.Logic.TransformComponent TransformComponentOf(GameCore.HotUpdate.Battle.Logic.Furniture f)
    {
        try
        {
            var dict = f != null ? f.agentComponentDictionary : null;
            if (dict == null) return null;
            return dict.TryGetValue(Il2CppInterop.Runtime.Il2CppType.From(typeof(GameCore.HotUpdate.Battle.Logic.TransformComponent)), out var raw)
                ? raw?.TryCast<GameCore.HotUpdate.Battle.Logic.TransformComponent>() : null;
        }
        catch { return null; }
    }

    // =====================================================================
    // wave8：专属图标落盘（WebUI/Res 磁盘通道，替换木板占位）
    // =====================================================================

    /// <summary>图标落盘判定缓存：null=未判定，true=eg_arrow_tower.png 已就绪（注入走专属路径），
    /// false=本会话定版退回木板（目录契约不成立/内嵌资源缺失/写入失败均一票定版，不反复重试刷盘）。</summary>
    private static bool? _iconReady;

    /// <summary>物品行 Icon 现行定版：落盘成功=专属 png 相对路径，否则退回 F6 木板 bundle 路径。</summary>
    private static string IconNow => _iconReady == true ? IconCustom : IconTex;

    /// <summary>物品行 WebIcon 现行定版：落盘成功=专属 png，否则退回 F6 木板 web 路径。</summary>
    private static string IconWebNow => _iconReady == true ? IconCustom : IconWebMat;

    /// <summary>wave8：把内嵌专属图标写到 <游戏根>/SurvivalLog_Data/StreamingAssets/WebUI/Res/Icon/
    /// eg_arrow_tower.png（ToolTable.html 直绑相对路径=普通磁盘文件，控制器实证通道）。游戏根取
    /// Paths.BepInExRootPath 上溯两级；先验 Res 目录存在（目录契约不成立绝不乱建目录→一次性 trace 后
    /// 本会话定版木板退路），Res 内补建 Icon 子目录（幂等，与 DeviceXpStore「首次保存自动建目标目录」
    /// 同款）。写策略：文件缺失或字节数与内嵌资源不符才写（temp+File.Move 原子替换同款），真写才 trace。
    /// 表注入共享核入口每轮调用（快注入/OnStep 同路），稳态成本=一次 Exists+Length 探测。全链 try/catch。</summary>
    /// <returns>true=专属文件就绪（99005 行 Icon/WebIcon 可指 IconCustom）。</returns>
    private static bool EnsureIconFile()
    {
        if (_iconReady != null) return _iconReady == true;   // 判定过（成败均定版）→ 直接复用，不再碰盘
        try
        {
            var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.BepInExRootPath, ".."));          // BepInEx 上溯一级=游戏根（原两级少一层，2026-11-01 实机 trace 实证修正）
            var resDir = Path.Combine(root, "SurvivalLog_Data", "StreamingAssets", "WebUI", "Res");
            if (!Directory.Exists(resDir))
            {
                _iconReady = false;                          // 目录契约不成立：不在错误位置建目录，本会话维持木板
                EndlessGuardPlugin.Trace("[Tower] icon: WebUI/Res 目录不存在（" + resDir + "），保留木板图标");
                return false;
            }
            Directory.CreateDirectory(Path.Combine(resDir, "Icon"));   // 已有则空转；只在已验证的 Res 下补子目录
            var target = Path.Combine(resDir, "Icon", "eg_arrow_tower.png");
            var bytes = ReadEmbeddedIcon();
            if (bytes == null)
            {
                _iconReady = false;                          // 内嵌资源缺失（构建配置病态）：木板退路
                EndlessGuardPlugin.Trace("[Tower] icon: 内嵌资源缺失（" + IconResourceName + "，查 csproj EmbeddedResource），保留木板图标");
                return false;
            }
            var fi = new FileInfo(target);
            if (!fi.Exists || fi.Length != bytes.Length)     // 缺失或过期（如旧版残留）才写；一致则零写入零 trace
            {
                var tmp = target + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, target, true);
                EndlessGuardPlugin.Trace("[Tower] icon file written: " + target + " (" + bytes.Length + " bytes)");
            }
            _iconReady = true;
            return true;
        }
        catch (Exception e)
        {
            _iconReady = false;                              // 写失败（权限等）：本会话木板退路，已 trace 不再刷屏
            EndlessGuardPlugin.Trace("[Tower] icon: 落盘失败（" + e.Message + "），保留木板图标");
            return false;
        }
    }

    /// <summary>读内嵌专属图标字节；资源缺失返 null（Trace 级兜底，不抛）。</summary>
    private static byte[] ReadEmbeddedIcon()
    {
        try
        {
            using var s = typeof(ArrowTower).Assembly.GetManifestResourceStream(IconResourceName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>F1/F6 旧版建行探测（行在我方 99005 上，只有本 mod 会建此 id）：①家具行
    /// RemoveFunc/MoveFunc 空（老 DLL 建的行，缺它们=塔无法卸载/搬动，官方 36004 同款 [299]/[1608]）；
    /// ②物品行 Icon/WebIcon 不是当前定版图标（F6：老行还挂电锯图标）→ 视同缺行触发 Inject 重放；
    /// Inject 内逐字段无条件拍平即幂等补齐。异常=false（哨兵不因探测病态空转）。</summary>
    private static bool LegacyRowNeedsSync(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it)
    {
        try
        {
            if (fu.TryGetValue(Id, out var f) && f != null && f.ResName == ResName
                && (f.RemoveFunc == null || f.RemoveFunc.Count == 0
                    || f.MoveFunc == null || f.MoveFunc.Count == 0)) return true;
            if (it.TryGetValue(Id, out var i) && i != null
                && (i.Icon != IconNow || i.WebIcon != IconWebNow)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>注入/修复四行 + 派生等级行 + Lv4 登记。行级幂等：行已存在则跳过建行仅无条件拍平显示/数值字段
    ///（半途异常重试时已入行不被判重挡死）；Lv4 追加前 Contains 判重、文案 Contains 判重。</summary>
    private static void Inject(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        try
        {
            InjectTowerDevice(td);
            InjectFurniture(fu);
            InjectPackageItem(it);
            InjectRecipe(pl);
            InjectDerivedRows(td, fu);
            if (!UnlockDefenseLv4(dl))
            {
                EndlessGuardPlugin.Trace("[Tower] Config_DefenseLv 缺 Lv" + DefenseLvKey + " 行（表漂移？四表已入，下轮重试登记）");
                return;
            }
            EndlessGuardPlugin.Trace("[Tower] tables injected 4/4 + derived " + DerivedInjectedCount + " rows + defenseLv4 unlocked");
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] Inject: " + e.Message); }
    }

    // =====================================================================
    // 派生等级行（fixwave2 核心配套；升级=换配置行架构的"行"从这来）
    // =====================================================================

    /// <summary>最近一次 InjectDerivedRows 实际写入的行数（trace 取证用；稳态重放=0）。</summary>
    internal static int DerivedInjectedCount { get; private set; }

    /// <summary>成长 cfg 签名（上次建派生行时的步长/系数组合；变了=派生行内容过期需重建）。</summary>
    private static string _derivedSig;

    /// <summary>派生行哨兵：任一派生行（lv2..5 × 5 基础）缺家具行或塔数值行=true（官方重建表后必然全缺）。</summary>
    private static bool DerivedRowsMissing(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu)
    {
        try
        {
            foreach (var baseCfg in DeviceXpMath.TrackedBaseIds)
                for (int lv = 2; lv <= 5; lv++)
                {
                    var rid = DeviceXpMath.RowIdFor(baseCfg, lv);
                    if (!fu.ContainsKey(rid) || !td.ContainsKey(rid)) return true;
                }
            return false;
        }
        catch { return true; }   // 探测病态按缺行处理（下轮重放幂等）
    }

    /// <summary>成长 cfg 新鲜度哨兵：WeaponAtkStep 等影响派生行数值的 cfg 自上次建行后有变化=true。
    /// （spec §3.5"改值即刻生效"语义在换行架构下落在行内容上：行重建后组件面板于下次官方重初始化吸收。）</summary>
    private static bool DerivedRowsStale()
    {
        var sig = DerivedSig();
        if (_derivedSig == null) { _derivedSig = sig; return false; }   // 冷启动：当前行就是按当前 cfg 建的
        return !string.Equals(sig, _derivedSig, StringComparison.Ordinal);
    }

    private static string DerivedSig() =>
        DeviceXpRuntime.WeaponAtkStep + "|" + DeviceXpRuntime.WeaponIntervalStep + "|" +
        DeviceXpRuntime.WeaponHpStepRatio + "|" + DeviceXpRuntime.SandbagHpStepRatio + "|" +
        DeviceXpRuntime.TowerRangeStep + "|" + DeviceXpRuntime.WeaponDefFactor + "|" +
        DeviceXpRuntime.SandbagDefFactor;

    /// <summary>建/刷新派生等级行（lv 2..5 × 5 基础设备；fixwave2）：家具行=源行全字段克隆，仅覆盖
    /// ID/Name(_Local)=基础名+" Lv.N"/FurnitureHP(_Max)=该级目标耐久/InCodex=false（箭塔家族 ResName
    /// 原样=P_Tripo05，模型供给不受影响）；塔数值行=源行克隆，仅 Attack/AttackInterval（武器+箭塔）、
    /// AttackRange（箭塔）、SelfDamagePerBite（按减伤系数缩放，基 0 恒 0=官方四件套/箭塔原值即无咬伤）。
    /// 源行从 ConfigManager 活字典读（官方 36001-36004 即 gamekb 表内容；99005 是我方注入行）——不硬编码
    /// 官方数值，表漂移时跟随。行在则原对象全字段重拍（幂等+cfg 新鲜度重建共用此路径）；列表字段全部
    /// 全新实例逐元素拷贝（官方消费方直读 .Count，null/共享实例有 NRE/Clear 连坐风险）。</summary>
    private static void InjectDerivedRows(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu)
    {
        var created = 0;
        try
        {
            foreach (var baseCfg in DeviceXpMath.TrackedBaseIds)
            {
                if (!fu.TryGetValue(baseCfg, out var srcF) || srcF == null ||
                    !td.TryGetValue(baseCfg, out var srcT) || srcT == null)
                {
                    EndlessGuardPlugin.Trace("[Tower] derived skip: base row missing cfg=" + baseCfg);
                    continue;
                }
                for (int lv = 2; lv <= 5; lv++)
                {
                    var rid = DeviceXpMath.RowIdFor(baseCfg, lv);
                    if (!fu.TryGetValue(rid, out var dstF) || dstF == null)
                    {
                        dstF = new GameCore.HotUpdate.Config_Furniture();
                        fu[rid] = dstF;
                        created++;
                    }
                    FillDerivedFurniture(dstF, srcF, rid, lv, baseCfg);
                    if (!td.TryGetValue(rid, out var dstT) || dstT == null)
                    {
                        dstT = new GameCore.HotUpdate.Config_TowerDevice();
                        td[rid] = dstT;
                        created++;
                    }
                    FillDerivedTowerDevice(dstT, srcT, rid, lv, baseCfg);
                }
            }
            DerivedInjectedCount = created;
            _derivedSig = DerivedSig();   // 行内容已按当前 cfg 拍平，新鲜度基线同步
            if (created > 0)
                EndlessGuardPlugin.Trace("[Tower] derived rows injected: " + created + " (2..5 × 5 bases)");
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] derived: " + e.Message); }
    }

    /// <summary>派生家具行全字段拍平（源行克隆 + 等级覆盖；字段清单=interop mdump 实测全可写集，
    /// 与 InjectFurniture 同一口径）。</summary>
    private static void FillDerivedFurniture(
        GameCore.HotUpdate.Config_Furniture f, GameCore.HotUpdate.Config_Furniture src, int rid, int lv, int baseCfg)
    {
        var name = DeviceXpMath.WithLevelSuffix(DisplayBaseName(src), lv);
        f.ID = rid;
        f.Name = name; f.Name_Local = name;                      // 字面中文（GetLocalText 键 miss 原样透传=直接显示）
        f.FsmName = src.FsmName;
        f.Story = src.Story;
        f.ResName = src.ResName;                                 // 箭塔家族原样 P_Tripo05 → 模型供给照常命中
        f.BagResName = src.BagResName;
        f.ICON = src.ICON;
        f.FurniturePrice = src.FurniturePrice;
        f.PawnPrice = src.PawnPrice;
        f.PurchaseRewardSetId = src.PurchaseRewardSetId;
        f.InstallTime = src.InstallTime;
        f.Des = src.Des; f.Des_Local = src.Des_Local;
        f.FurnitureType = src.FurnitureType;
        f.ShopPage = src.ShopPage;
        f.DemoTwoMode = src.DemoTwoMode;
        f.SlotType = src.SlotType;
        f.isAutoSilence = src.isAutoSilence;
        f.isShowName = src.isShowName;
        f.ShowStorage = src.ShowStorage;
        f.isShowHP = src.isShowHP;
        var baseHp = src.FurnitureHPMax > 0 ? src.FurnitureHPMax : src.FurnitureHP;   // 官方行 HP==HPMax；Max 优先
        var stepAbs = DeviceXpMath.StepAbs(baseHp, DeviceXpRuntime.HpStepRatioFor(baseCfg));
        var hp = DeviceXpMath.TargetMaxHp(baseHp, lv, stepAbs);
        f.FurnitureHP = hp; f.FurnitureHPMax = hp;
        f.FurnitureLowHP = src.FurnitureLowHP; f.FurnitureLowHP_Local = src.FurnitureLowHP_Local;
        f.isFurnitureDead = src.isFurnitureDead;
        f.FurnitureDeadText = src.FurnitureDeadText; f.FurnitureDeadText_Local = src.FurnitureDeadText_Local;
        f.FurnitureFunc = CopyList(src.FurnitureFunc);
        f.RemoveFunc = CopyList(src.RemoveFunc);                 // 官方 299 卸载/1608 搬动逐元素拷贝（可卸载性随行保留）
        f.MoveFunc = CopyList(src.MoveFunc);
        f.RemoveGet = CopyList(src.RemoveGet);
        f.ActionId = src.ActionId;
        f.ActionPos = src.ActionPos;
        f.PlantFurnitureID = src.PlantFurnitureID;
        f.CookFurnitureID = src.CookFurnitureID;
        f.VaseCapacity = src.VaseCapacity;
        f.BagId = src.BagId;
        f.IsElectrical = src.IsElectrical;
        f.ElectricalType = src.ElectricalType;
        f.ColdRate = src.ColdRate;
        f.PowerCost = src.PowerCost;
        f.PowerMode = src.PowerMode;
        f.HeatOutput = src.HeatOutput;
        f.ElectricalFurnitureID = src.ElectricalFurnitureID;
        f.RestoreCoeff = src.RestoreCoeff;
        f.DefReduceCoeff = src.DefReduceCoeff;
        f.InCodex = false;                                       // 派生行不进图鉴（brief 明确）
        f.LootRandomGroupId = src.LootRandomGroupId;
        f.LootRandomGroupId_P1 = src.LootRandomGroupId_P1;
        f.LootRandomGroupId_P2 = src.LootRandomGroupId_P2;
        f.LootRandomGroupId_P3 = src.LootRandomGroupId_P3;
        f.LootRandomGroupId_P4 = src.LootRandomGroupId_P4;
        f.LootRandomCount = src.LootRandomCount;
        f.RotProductOverride = CopyList(src.RotProductOverride);
        f.RotSourceSubCategory = CopyList(src.RotSourceSubCategory);
        f.BagAcceptCategory = src.BagAcceptCategory;
        f.RobotPlayerID = src.RobotPlayerID;
    }

    /// <summary>派生塔数值行全字段拍平（源行克隆 + 等级覆盖）：Attack/AttackInterval=Target*（武器+箭塔；
    /// 沙包/尖刺攻击面板本为 0，直拷语义等价，仍按类判定执行）；AttackRange=TargetRange（仅箭塔）；
    /// SelfDamagePerBite=源值×IncomingDamageFactor 四舍五入（受击减伤的新承载点；基 0 恒 0）。</summary>
    private static void FillDerivedTowerDevice(
        GameCore.HotUpdate.Config_TowerDevice t, GameCore.HotUpdate.Config_TowerDevice src, int rid, int lv, int baseCfg)
    {
        t.ID = rid;
        t.AttackPriority = src.AttackPriority;
        t.MaxAttackers = src.MaxAttackers;
        t.IsWalkThrough = src.IsWalkThrough;
        t.ExclusiveCharacter = src.ExclusiveCharacter;
        t.Attack = DeviceXpRuntime.IsWeaponClass(baseCfg)
            ? DeviceXpMath.TargetAttack(src.Attack, lv, DeviceXpRuntime.WeaponAtkStep)
            : src.Attack;
        t.AttackInterval = DeviceXpRuntime.IsWeaponClass(baseCfg)
            ? DeviceXpMath.TargetInterval(src.AttackInterval, lv, DeviceXpRuntime.WeaponIntervalStep)
            : src.AttackInterval;
        t.AttackRange = baseCfg == Id
            ? DeviceXpMath.TargetRange(src.AttackRange, lv, DeviceXpRuntime.TowerRangeStep)
            : src.AttackRange;
        t.BurstCount = src.BurstCount;
        t.HitSlowRatio = src.HitSlowRatio;
        t.HitSlowSeconds = src.HitSlowSeconds;
        t.CounterDamage = src.CounterDamage;                     // 尖刺反伤不随级成长（spec §3.4 反伤=经验来源）
        t.SelfDamagePerBite = src.SelfDamagePerBite != 0
            ? (int)Math.Round(src.SelfDamagePerBite * DeviceXpMath.IncomingDamageFactor(lv, DeviceXpRuntime.IncomingFactorFor(baseCfg)), MidpointRounding.AwayFromZero)
            : 0;
        t.PowerCostPerHit = src.PowerCostPerHit;
        t.MaxHitTargets = src.MaxHitTargets;
    }

    /// <summary>源行显示名（派生行 Name 基底）：Name_Local（字面中文）优先，空退 Name 键。</summary>
    private static string DisplayBaseName(GameCore.HotUpdate.Config_Furniture src) =>
        !string.IsNullOrEmpty(src.Name_Local) ? src.Name_Local : src.Name;

    /// <summary>Il2Cpp int 列表全新实例逐元素拷贝（源 null/病态=空列表——派生行列表字段恒非 null）。</summary>
    private static Il2CppSystem.Collections.Generic.List<int> CopyList(Il2CppSystem.Collections.Generic.List<int> src)
    {
        var l = new Il2CppSystem.Collections.Generic.List<int>();
        try { if (src != null) for (int i = 0; i < src.Count; i++) l.Add(src[i]); }
        catch { /* 源病态：给空列表保消费方 .Count 不炸 */ }
        return l;
    }

    /// <summary>塔体数值（spec §4.1 玻璃炮定位：高攻 14/速射 1.5s/远射 3.0/单体点名/低血 100/受击无减免）。</summary>
    private static void InjectTowerDevice(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td)
    {
        GameCore.HotUpdate.Config_TowerDevice t;
        if (!td.TryGetValue(Id, out t) || t == null)
        {
            t = new GameCore.HotUpdate.Config_TowerDevice();
            td[Id] = t;
        }
        t.ID = Id;
        t.AttackPriority = 0;
        t.MaxAttackers = 3;             // 可被 3 尸同时咬（同电锯——能被咬是经验来源之一）
        t.IsWalkThrough = true;         // 不阻挡，不改变现有布防迷宫
        t.ExclusiveCharacter = 0;
        t.Attack = 14;
        t.AttackInterval = 1.5f;
        t.AttackRange = 3.0f;
        t.BurstCount = 1;
        t.HitSlowRatio = 0f;            // 箭矢不减速
        t.HitSlowSeconds = 0f;
        t.CounterDamage = 0;
        t.SelfDamagePerBite = 0;
        t.PowerCostPerHit = 2.5f;
        t.MaxHitTargets = 1;            // 单体点名（对群短板=平衡项）
    }

    /// <summary>家具行：结构字段全量照抄 36004 电锯行（gamekb dump 2026-10-23 逐字段），仅覆盖
    /// ID/名称/描述/ResName/HP 100/PowerCost 6。</summary>
    private static void InjectFurniture(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu)
    {
        GameCore.HotUpdate.Config_Furniture f;
        if (!fu.TryGetValue(Id, out f) || f == null)
        {
            f = new GameCore.HotUpdate.Config_Furniture();
            fu[Id] = f;
        }
        f.ID = Id;
        f.Name = "箭塔"; f.Name_Local = "箭塔";
        f.FsmName = "Agent/Furniture_TowerDevice";
        f.Story = 0;
        f.ResName = ResName;
        f.BagResName = "P_WoodBox_02";
        f.ICON = IconWeb;                                   // 电锯图标（加密 bundle，一期复用）
        f.FurniturePrice = 0;
        f.PawnPrice = 0;
        f.PurchaseRewardSetId = 0;
        f.InstallTime = 1800;
        f.Des = "固定在木架上的连发弩，自动射击范围内的丧尸。高攻速射，但脆弱易损。";
        f.Des_Local = f.Des;
        f.FurnitureType = 0;
        f.ShopPage = 0;
        f.DemoTwoMode = 0;
        f.SlotType = 10;                                    // 防御位
        f.isAutoSilence = false;
        f.isShowName = true;
        f.ShowStorage = 0;
        f.isShowHP = true;                                  // 有血条（能被咬可感知）
        f.FurnitureHP = 100; f.FurnitureHPMax = 100;        // 电锯一半：低血量玻璃炮
        f.FurnitureLowHP = ""; f.FurnitureLowHP_Local = "";
        f.isFurnitureDead = false;
        f.FurnitureDeadText = ""; f.FurnitureDeadText_Local = "";
        f.FurnitureFunc = L(9121, 9122);                    // 修理/机器人修理（官方四件套同款）
        f.RemoveFunc = L(299);
        f.MoveFunc = L(1608);
        f.RemoveGet = L();
        f.ActionId = 2;
        f.ActionPos = "";
        f.PlantFurnitureID = 0;
        f.CookFurnitureID = 0;
        f.VaseCapacity = 0;
        f.BagId = 0;
        f.IsElectrical = true;                              // 接电网：不给电停摆（PowerMode 行为同官方）
        f.ElectricalType = 1;
        f.ColdRate = 0f;
        f.PowerCost = 6;                                    // 持续输出税（电锯 5）
        f.PowerMode = 0;
        f.HeatOutput = 0;
        f.ElectricalFurnitureID = 0;
        f.RestoreCoeff = 0f;
        f.DefReduceCoeff = 0f;                              // 无受击减免成长（低防御定位）
        f.InCodex = true;
        f.LootRandomGroupId = 0;
        f.LootRandomGroupId_P1 = 0; f.LootRandomGroupId_P2 = 0; f.LootRandomGroupId_P3 = 0; f.LootRandomGroupId_P4 = 0;
        f.LootRandomCount = 0;
        f.RotProductOverride = L();
        f.RotSourceSubCategory = L();
        f.BagAcceptCategory = 0;
        f.RobotPlayerID = 0;
        // F1 运行时自证：playtest（2026-10-31）报不可卸载/搬动时建行代码其实已含 [299]/[1608]
        //（实测部署 DLL 与本仓 Release 逐字节一致），故留一行行内容实证——下轮实机若仍不可卸载，
        // 可据此排除"行缺字段"，转向查官方消费方（func 查找表建在我们注入之前？）。
        EndlessGuardPlugin.Trace("[Tower] furniture row funcs remove=" + SafeCount(f.RemoveFunc)
                                 + " move=" + SafeCount(f.MoveFunc) + " func=" + SafeCount(f.FurnitureFunc));
    }

    /// <summary>il2cpp List 计数（null/病态=-1，取证行不因探测炸）。</summary>
    private static int SafeCount(Il2CppSystem.Collections.Generic.List<int> l)
    {
        try { return l != null ? l.Count : -1; }
        catch { return -1; }
    }

    /// <summary>包裹物品行：结构照抄 26004 电锯包裹，仅覆盖 ID/名称/描述/TargetFurnitureID；
    /// Icon/WebIcon=专属图标（wave8，WebUI/Res 磁盘通道；落盘失败退 F6 木板同款——2026-10-31 playtest
    /// 作者不满电锯图标后先借官方材料 20106 过渡）、Model 复用电锯包裹纸盒（spec §4.2 一期裁定）。</summary>
    private static void InjectPackageItem(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it)
    {
        GameCore.HotUpdate.Config_Item i;
        if (!it.TryGetValue(Id, out i) || i == null)
        {
            i = new GameCore.HotUpdate.Config_Item();
            it[Id] = i;
        }
        i.ID = Id;
        i.ItemName = "箭塔包裹"; i.ItemName_Local = "箭塔包裹";
        i.ItemDes1 = ""; i.ItemDes1_Local = "";
        i.ItemDes2 = "拆封后可安装到防御位的固定式连发弩"; i.ItemDes2_Local = i.ItemDes2;
        i.ValueDisplay1 = 0f; i.ValueDisplay2 = 0f; i.ValueDisplay3 = 0f; i.ValueDisplay4 = 0f; i.ValueDisplay5 = 0f;
        i.StackLimit = 1;
        i.Category = 14; i.SubCategory = 0;                 // 14=包裹类（工作台配方产出品类）
        i.CanCook = false;
        i.FoodTag1 = 0; i.FoodTag2 = 0; i.FoodTag3 = 0;
        i.Size = L(1, 1);
        i.price = 0;
        i.weight = 3000;
        i.Life = -1;                                        // 不腐坏（官方包裹同款）
        i.IT_Rot_Threshold = 0f;
        i.It_Rot_Product_Id = L();
        i.UseTimes = 1;
        i.CantUse = false;
        i.CanUsePeace = false;
        i.OverdueDebuff = L();
        i.DebuffPow = L();
        i.Taste = 0;
        i.WishPower = 0;
        i.Story = 0;
        i.DailyMax = 0;
        i.Plant = 0;
        i.Plant_Fast = 0f;
        i.Burnable = false;
        i.BurnValue = 0;
        i.Trap = 0;
        i.TargetFurnitureID = Id;                           // 拆封→安装箭塔（安装目标走物品行，与动作行解耦）
        i.Prey_Rarity = 0;
        i.IT_Discovery_Exp = 0;
        i.CaptureExp = 0;
        i.ItemFmodPath = "";
        i.Icon = IconNow; i.WebIcon = IconWebNow;           // wave8：落盘成功=专属图标，失败=F6 木板退路（EnsureIconFile 判路；旧行靠哨兵差异自动补拍）
        i.Model = PackageModel;
        i.ConditionSetId = 0;
        i.IsHighDemand = false;
        i.TradeValue = 0;
        i.RecommendType = 0;
        i.RecommendWeight = 0;
        i.InCodex = false;                                  // 官方包裹行原值 false，照抄
        i.DemoTwoMode = 0;
        i.VaseLife = 0;
        i.VaseMoraleRate = 0f;
        i.VaseModel = "";
        i.CanBrew = false;
        i.CutProductId = 0;
        i.TradeSellRate = 0f;
        i.KeepWeightOnUse = false;
        i.UseAction = ChainsawUnpackAction;                 // 复用官方拆封动作（类头裁定）
    }

    /// <summary>工作台配方行：结构照抄 403 电锯配方，仅覆盖 ID/名称/材料/产物/defense_exp。</summary>
    private static void InjectRecipe(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl)
    {
        GameCore.HotUpdate.Config_ProductionList r;
        if (!pl.TryGetValue(Id, out r) || r == null)
        {
            r = new GameCore.HotUpdate.Config_ProductionList();
            pl[Id] = r;
        }
        r.ID = Id;
        r.ShopName = "箭塔包裹"; r.ShopName_Local = "箭塔包裹";
        r.DemoTwoMode = 0;
        r.IsUseable = true;
        r.MaterialList = L(Materials);
        r.ProductID = L(Id);
        r.FailedID = L();
        r.Level = 1;
        r.DiscountRate = 1f;
        r.LifeMin = 80;
        r.show_level = 0;
        r.craft_level = 0;
        r.perfect_item_id = L();
        r.perfect_rate = 0f;
        r.discovery_exp = 100;
        r.InCodex = true;
        r.NoteCategory = 6;
        r.defense_exp = 25;                                 // 制作给防线经验（电网 20/电锯 30 之间）
    }

    /// <summary>防线 Lv4 登记：unlock_recipes 追加 99005（Contains 幂等）+ Info_Local 追加
    /// 「新增：解锁 箭塔」（Contains 幂等）。返回 false=Lv4 行缺失（表漂移），哨兵下轮重试（四行已入不重建）。</summary>
    private static bool UnlockDefenseLv4(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        if (!dl.TryGetValue(DefenseLvKey, out var lv4) || lv4 == null) return false;
        if (lv4.unlock_recipes == null) lv4.unlock_recipes = L();
        if (!Contains(lv4.unlock_recipes, Id)) lv4.unlock_recipes.Add(Id);
        var info = lv4.Info_Local;
        if (string.IsNullOrEmpty(info)) info = "工事技师";
        if (!info.Contains("新增：解锁 箭塔")) lv4.Info_Local = info + Lv4AppendText;
        return true;
    }

    /// <summary>Il2Cpp List 构造（每字段全新实例——官方消费方直读 .Count，复用/共享实例有被 Clear 连坐风险）。</summary>
    private static Il2CppSystem.Collections.Generic.List<int> L(params int[] v)
    {
        var l = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var x in v) l.Add(x);
        return l;
    }

    private static bool Contains(Il2CppSystem.Collections.Generic.List<int> list, int v)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++) if (list[i] == v) return true;
        return false;
    }
}
