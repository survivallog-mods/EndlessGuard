using System;
using SurvivalLog.ModShared;

namespace SurvivalLog.EndlessGuard;

/// <summary>磁暴线圈「99006」：四表内存注入 + 防线 Lv4 解锁登记（结构照抄箭塔 99005，数值按高压塔定位）。
/// 模型走 bundle prefab（TeslaCoilModel 供应），不走程序化拼装。设备级通用处理（图标落盘、被咬规则、
/// 模型契约）由 ModDevices / DeviceModel 统一承担，本类只管自己的四行。</summary>
internal static class TeslaCoil
{
    internal const int Id = 99006;
    internal const string ResName = "P_TeslaCoil";
    internal static readonly int[] Materials = { 20004, 20004, 20004, 20005, 20005, 20002 };   // 铁皮×3+木片×2+碎玻璃

    private const int DefenseLvKey = 4;
    private const string Lv4AppendText = "\\n新增：解锁 磁暴线圈";
    private const int UnpackAction = 100026004;
    // 图标：专属图标（内嵌 Resources\eg_tesla_coil.png）走 WebUI/Res 磁盘通道，落盘与路径判定在 ModDevices.EnsureIcon；
    // 落盘不成立时退回官方「木板」图标。
    private const string IconTex = "Assets/RuntimeAssets/Texture/Material/UI_Item_Icon_Mat_M_muban";
    private const string IconWebMat = "../../Res/Material/UI_Item_Icon_Mat_M_muban.png";

    /// <summary>专属图标的 WebUI 相对路径；null=本会话退回木板占位（ModDevices.EnsureIcon 定版）。</summary>
    private static string _iconWeb;

    private static string IconNow => _iconWeb ?? IconTex;
    private static string IconWebNow => _iconWeb ?? IconWebMat;

    private const string PackageModel = "Assets/RuntimeAssets/Prefabs/Furniture/P_Paperbox_009_S1_M02";

    internal static bool TryInjectFast() => EnsureTables();
    internal static void OnStep() => EnsureTables();

    private static bool EnsureTables()
    {
        if (EndlessGuardPlugin.TeslaCoilEnabled != null && !EndlessGuardPlugin.TeslaCoilEnabled.Value) return false;
        var cm = BagTable.Cm();
        if (cm == null) return false;

        var td = cm._Config_TowerDevice_Dict;
        var fu = cm._Config_Furniture_Dict;
        var it = cm._Config_Item_Dict;
        var pl = cm._Config_ProductionList_Dict;
        var dl = cm._Config_DefenseLv_Dict;
        if (td == null || fu == null || it == null || pl == null || dl == null) return false;

        _iconWeb = ModDevices.EnsureIcon(ModDevices.Coil);   // 专属图标落盘先行（结果定图标走专属还是木板退路）

        if (SentinelsGreen(td, fu, it, pl, dl))
        {
            TeslaCoilModel.Arm();
            return true;
        }
        Inject(td, fu, it, pl, dl);
        return SentinelsGreen(td, fu, it, pl, dl);
    }

    private static bool SentinelsGreen(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        try
        {
            var lv4 = dl.TryGetValue(DefenseLvKey, out var row) ? row : null;
            return td.ContainsKey(Id) && fu.ContainsKey(Id) && it.ContainsKey(Id) && pl.ContainsKey(Id)
                   && lv4 != null && Contains(lv4.unlock_recipes, Id);
        }
        catch { return false; }
    }

    private static void Inject(
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl,
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        try
        {
            InjectTower(td);
            InjectFurniture(fu);
            InjectItem(it);
            InjectRecipe(pl);
            UnlockDefenseLv4(dl);
            EndlessGuardPlugin.Trace("[Coil] tables injected 99006 + defenseLv4 unlocked");
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Coil] Inject: " + e.Message); }
    }

    /// <summary>塔数值：高压放电定位——远射程、高单发、慢间隔、可同时打 2 个目标。</summary>
    private static void InjectTower(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_TowerDevice> td)
    {
        if (!td.TryGetValue(Id, out var t) || t == null)
        {
            t = new GameCore.HotUpdate.Config_TowerDevice();
            td[Id] = t;
        }
        t.ID = Id;
        t.AttackPriority = 0;
        t.MaxAttackers = 2;
        t.IsWalkThrough = true;
        t.ExclusiveCharacter = 0;
        t.Attack = 26;
        t.AttackInterval = 2.2f;
        t.AttackRange = 4.5f;
        t.BurstCount = 1;
        t.HitSlowRatio = 0f;
        t.HitSlowSeconds = 0f;
        t.CounterDamage = 0;
        t.SelfDamagePerBite = 0;
        t.PowerCostPerHit = 5f;
        t.MaxHitTargets = 5;
    }

    private static void InjectFurniture(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Furniture> fu)
    {
        if (!fu.TryGetValue(Id, out var f) || f == null)
        {
            f = new GameCore.HotUpdate.Config_Furniture();
            fu[Id] = f;
        }
        f.ID = Id;
        f.Name = "磁暴线圈"; f.Name_Local = "磁暴线圈";
        f.FsmName = "Agent/Furniture_TowerDevice";
        f.Story = 0;
        f.ResName = ResName;
        f.BagResName = "P_WoodBox_02";
        f.ICON = IconWebNow;
        f.FurniturePrice = 0;
        f.PawnPrice = 0;
        f.PurchaseRewardSetId = 0;
        f.InstallTime = 2400;
        f.Des = "高压线圈塔，向范围内目标释放电弧。射程远、单发高，充能较慢。";
        f.Des_Local = f.Des;
        f.FurnitureType = 0;
        f.ShopPage = 0;
        f.DemoTwoMode = 0;
        f.SlotType = 10;
        f.isAutoSilence = false;
        f.isShowName = true;
        f.ShowStorage = 0;
        f.isShowHP = true;
        f.FurnitureHP = 180; f.FurnitureHPMax = 180;
        f.FurnitureLowHP = ""; f.FurnitureLowHP_Local = "";
        f.isFurnitureDead = false;
        f.FurnitureDeadText = ""; f.FurnitureDeadText_Local = "";
        f.FurnitureFunc = L(9121, 9122);
        f.RemoveFunc = L(299);
        f.MoveFunc = L(1608);
        f.RemoveGet = L();
        f.ActionId = 2;
        f.ActionPos = "";
        f.PlantFurnitureID = 0;
        f.CookFurnitureID = 0;
        f.VaseCapacity = 0;
        f.BagId = 0;
        f.IsElectrical = true;
        f.ElectricalType = 1;
        f.ColdRate = 0f;
        f.PowerCost = 12;
        f.PowerMode = 0;
        f.HeatOutput = 0;
        f.ElectricalFurnitureID = 0;
        f.RestoreCoeff = 0f;
        f.DefReduceCoeff = 0f;
        f.InCodex = true;
        f.LootRandomGroupId = 0;
        f.LootRandomGroupId_P1 = 0; f.LootRandomGroupId_P2 = 0; f.LootRandomGroupId_P3 = 0; f.LootRandomGroupId_P4 = 0;
        f.LootRandomCount = 0;
        f.RotProductOverride = L();
        f.RotSourceSubCategory = L();
        f.BagAcceptCategory = 0;
        f.RobotPlayerID = 0;
    }

    private static void InjectItem(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Item> it)
    {
        if (!it.TryGetValue(Id, out var i) || i == null)
        {
            i = new GameCore.HotUpdate.Config_Item();
            it[Id] = i;
        }
        i.ID = Id;
        i.ItemName = "磁暴线圈包裹"; i.ItemName_Local = "磁暴线圈包裹";
        i.ItemDes1 = ""; i.ItemDes1_Local = "";
        i.ItemDes2 = "拆封后可安装到防御位的高压放电塔"; i.ItemDes2_Local = i.ItemDes2;
        i.ValueDisplay1 = 0f; i.ValueDisplay2 = 0f; i.ValueDisplay3 = 0f; i.ValueDisplay4 = 0f; i.ValueDisplay5 = 0f;
        i.StackLimit = 1;
        i.Category = 14; i.SubCategory = 0;
        i.CanCook = false;
        i.FoodTag1 = 0; i.FoodTag2 = 0; i.FoodTag3 = 0;
        i.Size = L(1, 1);
        i.price = 0;
        i.weight = 3000;
        i.Life = -1;
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
        i.TargetFurnitureID = Id;
        i.Prey_Rarity = 0;
        i.IT_Discovery_Exp = 0;
        i.CaptureExp = 0;
        i.ItemFmodPath = "";
        i.Icon = IconNow; i.WebIcon = IconWebNow;
        i.Model = PackageModel;
        i.ConditionSetId = 0;
        i.IsHighDemand = false;
        i.TradeValue = 0;
        i.RecommendType = 0;
        i.RecommendWeight = 0;
        i.InCodex = false;
        i.DemoTwoMode = 0;
        i.VaseLife = 0;
        i.VaseMoraleRate = 0f;
        i.VaseModel = "";
        i.CanBrew = false;
        i.CutProductId = 0;
        i.TradeSellRate = 0f;
        i.KeepWeightOnUse = false;
        i.UseAction = UnpackAction;
    }

    private static void InjectRecipe(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> pl)
    {
        if (!pl.TryGetValue(Id, out var r) || r == null)
        {
            r = new GameCore.HotUpdate.Config_ProductionList();
            pl[Id] = r;
        }
        r.ID = Id;
        r.ShopName = "磁暴线圈包裹"; r.ShopName_Local = "磁暴线圈包裹";
        r.DemoTwoMode = 0;
        r.IsUseable = true;
        r.MaterialList = L(Materials);
        r.ProductID = L(Id);
        r.FailedID = L();
        r.Level = 1;
        r.DiscountRate = 1f;
        r.LifeMin = 90;
        r.show_level = 0;
        r.craft_level = 0;
        r.perfect_item_id = L();
        r.perfect_rate = 0f;
        r.discovery_exp = 120;
        r.InCodex = true;
        r.NoteCategory = 6;
        r.defense_exp = 30;
    }

    private static bool UnlockDefenseLv4(Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dl)
    {
        if (!dl.TryGetValue(DefenseLvKey, out var lv4) || lv4 == null) return false;
        if (lv4.unlock_recipes == null) lv4.unlock_recipes = L();
        if (!Contains(lv4.unlock_recipes, Id)) lv4.unlock_recipes.Add(Id);
        var info = lv4.Info_Local;
        if (string.IsNullOrEmpty(info)) info = "工事技师";
        if (!info.Contains("新增：解锁 磁暴线圈")) lv4.Info_Local = info + Lv4AppendText;
        return true;
    }

    private static Il2CppSystem.Collections.Generic.List<int> L(params int[] v)
    {
        var l = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var x in v) l.Add(x);
        return l;
    }

    private static bool Contains(Il2CppSystem.Collections.Generic.List<int> list, int v)
    {
        if (list == null) return false;
        for (var i = 0; i < list.Count; i++) if (list[i] == v) return true;
        return false;
    }
}
