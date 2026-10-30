using System;
using SurvivalLog.ModShared;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 箭塔「99005」（功能B，spec §4.1/§4.2）：四表内存注入 + 防线 Lv4 解锁登记。
/// 行：Config_TowerDevice/Config_Furniture/Config_Item(包裹)/Config_ProductionList(配方) 统一 ID=99005
///（99xxx 预留段，作者裁决 2026-10-30：紧贴官方空位必撞号）；Config_DefenseLv Lv4 unlock_recipes
/// [403]→[403,99005] 且 Info_Local 追加「新增：解锁 箭塔」（与电锯同档）。
/// 时机/幂等：照抄 ScavengePlans.Inject/OnConfigStep 定案模式——GuardTicker 2s 巡检（async 状态机
/// MoveNext 禁挂教训）、BagTable.Cm() 单例就绪检查、dict 级 ContainsKey 判重、哨兵复查（表被官方
/// Initialize 整体重建后自动重放；稳态静默零写入零 trace）。表是进程内存态，不写任何存档。
/// 本地化：Name/Name_Local、Des/Des_Local、ItemName(_Local)、ShopName(_Local) 全部直写字面中文
///（官方消费方把键走 GetLocalText→GetLocalTxt，键 miss 原样透传输入串，ScavengePlans 2026-10-25 实机截图实锤）。
/// 全列表字段非 null（官方消费方直接 .Count，null 即 NRE——ScavengePlans.NewCondSet 教训）。
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
    private const string IconTex = "Assets/RuntimeAssets/Texture/Furniture/UI_Item_Icon_Furnit_M_831300316";      // 电锯图标（一期复用）
    private const string IconWeb = "../../Res/Furniture/UI_Item_Icon_Furnit_M_831300316.png";
    private const string PackageModel = "Assets/RuntimeAssets/Prefabs/Furniture/P_Paperbox_009_S1_M02";           // 与 26004 同款纸盒

    /// <summary>GuardTicker 2s 巡检入口（照抄 ScavengePlans.OnConfigStep：总开关下再挂本功能开关；
    /// Cm() 单例未建→下轮再试；五张 dict 任一未装载→下轮再试；哨兵齐→稳态静默返回）。</summary>
    internal static void OnStep()
    {
        if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value) return;   // 关=不注入（已注入行为进程内存态，本局保留，重启干净）
        var cm = BagTable.Cm();
        if (cm == null) return;

        var td = cm._Config_TowerDevice_Dict;
        var fu = cm._Config_Furniture_Dict;
        var it = cm._Config_Item_Dict;
        var pl = cm._Config_ProductionList_Dict;
        var dl = cm._Config_DefenseLv_Dict;
        if (td == null || fu == null || it == null || pl == null || dl == null) return;   // 装表中（2026-10-24 实锤：Cm() 就绪≠dict 就绪），下轮再试

        // 哨兵复查（Plans[0] 模式）：官方重试/换语言重跑 Initialize 会整体重建 _Config_*_Dict——
        // 四表任一丢 99005 或 Lv4 丢解锁项即全量重放（Inject 内部逐行 ContainsKey/Contains 幂等，重放无副作用）
        bool rowsMissing = !td.ContainsKey(Id) || !fu.ContainsKey(Id) || !it.ContainsKey(Id) || !pl.ContainsKey(Id);
        bool lv4Missing = !dl.TryGetValue(DefenseLvKey, out var lv4) || lv4 == null || !Contains(lv4.unlock_recipes, Id);
        if (!rowsMissing && !lv4Missing)
        {
            ArrowTowerModel.EnsureRegistered();   // 表齐后的稳态轮挂模型注册（幂等，建一次+核验补丁；T7）
            return;
        }
        Inject(td, fu, it, pl, dl);
        ArrowTowerModel.EnsureRegistered();       // 注入完成轮同样尝试（幂等；模板尚未建成功时提前一轮）
    }

    /// <summary>注入/修复四行 + Lv4 登记。行级幂等：行已存在则跳过建行仅无条件拍平显示/数值字段
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
            if (!UnlockDefenseLv4(dl))
            {
                EndlessGuardPlugin.Trace("[Tower] Config_DefenseLv 缺 Lv" + DefenseLvKey + " 行（表漂移？四表已入，下轮重试登记）");
                return;
            }
            EndlessGuardPlugin.Trace("[Tower] tables injected 4/4 + defenseLv4 unlocked");
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] Inject: " + e.Message); }
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
    }

    /// <summary>包裹物品行：结构照抄 26004 电锯包裹，仅覆盖 ID/名称/描述/TargetFurnitureID；
    /// Icon/Model 复用电锯包裹（同款纸盒+电锯图标，spec §4.2 一期复用裁定）。</summary>
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
        i.Icon = IconTex; i.WebIcon = IconWeb;              // 电锯图标复用（spec §4.2）
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
