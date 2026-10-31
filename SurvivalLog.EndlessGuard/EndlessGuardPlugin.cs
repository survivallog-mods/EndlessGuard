using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.EndlessGuard;

/// <summary>无尽守卫：拦沦陷/旧档恢复/剥倒计时tag/生存规划扩展。机制与实证见 DESIGN.md。</summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class EndlessGuardPlugin : BasePlugin
{
    public const string Guid = "com.local.survivallog.endlessguard";
    public const string Name = "EndlessGuard";
    public const string Version = "1.7.0"; // 跟随套件 version.json（作者定号）

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> PreventNewFalls;
    internal static ConfigEntry<bool> RestoreFallenPoints;
    internal static ConfigEntry<bool> ExtendScavengePlans;
    internal static ConfigEntry<int> GrantPlanId;
    internal static ConfigEntry<string> GrantPlanIds;
    internal static ConfigEntry<bool> ShowLevel;
    internal static ConfigEntry<bool> ResetLevels;
    internal static ConfigEntry<bool> ArrowTowerEnabled;
    internal static ConfigEntry<bool> XPEnabled;
    internal static ConfigEntry<float> XPMultiplier;
    internal static ConfigEntry<float> XPThresholdScale;
    internal static ConfigEntry<int> MaxLevel;
    internal static ConfigEntry<float> KillBonusRatio;
    internal static ConfigEntry<float> SandbagXPRate;
    internal static ConfigEntry<float> WeaponAtkStep;
    internal static ConfigEntry<float> WeaponIntervalStep;
    internal static ConfigEntry<float> WeaponHpStepRatio;
    internal static ConfigEntry<float> WeaponDefFactor;
    internal static ConfigEntry<float> SandbagHpStepRatio;
    internal static ConfigEntry<float> SandbagDefFactor;
    internal static ConfigEntry<float> TowerRangeStep;

    public override void Load()
    {
        Log = base.Log;
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=全部功能还原官方（本局已生效的改动保留，重启后完全干净）。");
        PreventNewFalls = Config.Bind("Fall", "PreventNewFalls", true, "拦截全部新沦陷（三模式通吃）。");
        RestoreFallenPoints = Config.Bind("Fall", "RestoreFallenPoints", true, "读档后恢复已沦陷的交易点/探险图（营地按剧情链状态区分；恢复例程幂等）。");
        ExtendScavengePlans = Config.Bind("Plans", "ExtendScavengePlans", true, "注入 11 条拾遗扩展规划（超市·六~十/医院·三~五/学校·三~五，D100-D300）+ 洞察注册表 + 篮子/降档。");
        GrantPlanId = Config.Bind("Plans", "GrantPlanId", 0, "补授：因注入窗口事故丢失持有的新规划卡 id（如 31606=超市拾遗·六）。设值后进档一次即写入持有名单（不扣点），补完自动清零。0=关闭。");
        GrantPlanIds = Config.Bind("Plans", "GrantPlanIds", "", "补授（多卡版）：逗号分隔的新规划卡 id 列表（如 31607,31633）。进档一次逐卡补入持有名单（不扣点），全部确认后自动清空。");
        ShowLevel = Config.Bind("Defense", "ShowLevel", true, "设备等级随名称显示（升级后名字自动带 Lv.N，如「机械电锯 Lv.3」，全 UI 原生）；此项另控制悬停设备时的 trace 进度行（EndlessGuard_trace.txt 的 [XP] hoverinfo，同设备 5 秒一条）。false=关进度行。");
        ResetLevels = Config.Bind("Defense", "ResetLevels", false, "卸载前还原模式：开启后进档一次，即把全部已升级防御设备还原官方原厂状态（配置行换回基础行+耐久按基础上限夹取+清空经验账本），然后可安全删除本 dll。不会自动复位——反复进出档确认干净后请手动关回 false（保持开启期间经验不累积）。");
        ArrowTowerEnabled = Config.Bind("Defense", "ArrowTowerEnabled", true, "注入新设备「箭塔」：工作台配方（木片×2+铁皮+碎玻璃）+ 防线 Lv4 解锁 + 四表数值行（ID 99005）。false=停止注入；已注入行为进程内存态，本局保留，重启后完全干净。");
        XPEnabled = Config.Bind("Defense", "XPEnabled", true, "设备经验升级总开关：false=经验系统整体停用（经验入账/升级换行/巡检补写/悬停进度行全部停；已升级设备保留当前等级行，重启读档原样回来）。");
        XPMultiplier = Config.Bind("Defense", "XPMultiplier", 1.0f, "经验获取倍率：开火/承伤/击杀/反伤入账全部乘这个数（2=升级速度翻倍，0.5=减半）。");
        XPThresholdScale = Config.Bind("Defense", "XPThresholdScale", 1.0f, "升级阈值整体缩放（>1=更难升级，0.5=阈值减半；基线 1200/3600/8400/16800）。只影响今后的经验判定，已到手的等级不回退。");
        MaxLevel = Config.Bind("Defense", "MaxLevel", 5, "设备等级上限（1~5；1=关闭成长）。只封顶今后的升级，已超上限的设备属性保留不回退（调回上限自动续上）。");
        KillBonusRatio = Config.Bind("Defense", "KillBonusRatio", 0.5f, "击杀追加比例：每次开火打死的每只丧尸额外给「该次伤害×比例」的经验（0=关掉击杀加成）。");
        SandbagXPRate = Config.Bind("Defense", "SandbagXPRate", 2.0f, "沙包承伤经验系数：其他设备掉 1 血得 1 经验，沙包掉 1 血得这么多倍（练功沙袋定位，越高沙包升级越快）。");
        WeaponAtkStep = Config.Bind("Defense", "WeaponAtkStep", 0.15f, "武器类（尖刺/电网/电锯/箭塔）每级攻击 +15% 的比例（复利累乘；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        WeaponIntervalStep = Config.Bind("Defense", "WeaponIntervalStep", 0.08f, "武器类每级攻击间隔缩短 8% 的比例（下限=基础间隔的 60%，攻速不会无限制缩；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        WeaponHpStepRatio = Config.Bind("Defense", "WeaponHpStepRatio", 0.30f, "武器类每级耐久上限 +基础值×此比例（改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        WeaponDefFactor = Config.Bind("Defense", "WeaponDefFactor", 0.90f, "武器类每级受伤×此系数（0.9=每级减伤 10%；1=不减伤；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        SandbagHpStepRatio = Config.Bind("Defense", "SandbagHpStepRatio", 0.25f, "沙包每级耐久上限 +基础值×此比例（血牛定位，比武器类厚；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        SandbagDefFactor = Config.Bind("Defense", "SandbagDefFactor", 0.88f, "沙包每级受伤×此系数（0.88=每级减伤 12%，比武器类更能扛；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");
        TowerRangeStep = Config.Bind("Defense", "TowerRangeStep", 0.55f, "箭塔射程每级 +此格数（基础射程 3 格，Lv5=3+4×0.55=5.2 格全场大半纵深；改后立即重写等级行，设备面板于下次读档/重放时吸收新值）。");

        // 逐类挂载+异常隔离（2026-10-25 事故教训：PatchAll 整包挂载时单个补丁 IL 编译错会炸掉整个
        // Load()——Ticker/loaded 行全没执行，"补丁在跑轮询全无"的残废态。逐类隔离后坏一个不连坐）。
        var harmony = new HarmonyLib.Harmony(Guid);
        foreach (var t in typeof(EndlessGuardPlugin).Assembly.GetTypes())
        {
            if (!t.IsClass || !t.IsDefined(typeof(HarmonyLib.HarmonyPatch), false)) continue;
            try { harmony.CreateClassProcessor(t).Patch(); }
            catch (Exception e) { Trace("patch 挂载失败 " + t.Name + ": " + e.Message); }
        }

        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<GuardTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<GuardTicker>();
        var go = new GameObject("EndlessGuard.Ticker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<GuardTicker>();

        // fixwave5 F1 快注入 ticker（0.25s，表注入竞态根治——独立 GameObject：退役 SetActive(false)
        // 不连坐 2s 主 ticker；形态照抄 GuardTicker 的已实证注册模式）。
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FastInjectTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<FastInjectTicker>();
        var fastGo = new GameObject("EndlessGuard.FastTicker");
        Object.DontDestroyOnLoad(fastGo);
        fastGo.AddComponent<FastInjectTicker>();

        Trace("loaded：cfg=" + Enabled.Value + "," + PreventNewFalls.Value + "," + RestoreFallenPoints.Value + "," + ExtendScavengePlans.Value
            + "；xp=" + (XPEnabled != null && XPEnabled.Value) + ",tower=" + (ArrowTowerEnabled != null && ArrowTowerEnabled.Value));
        Log.LogInfo("[EndlessGuard] 囤鼠桃园 loaded（拦沦陷/恢复/剥tag/规划扩展）");
        SurvivalLog.ModShared.WelcomeTip.Install(Log);
    }

    /// <summary>自证文件（硬规则9：不依赖 LogOutput.log）。每行带时间戳。</summary>
    internal static void Trace(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "EndlessGuard_trace.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { }
    }
}

/// <summary>2s 轮询：表注入与注册表追加在 Cm() 就绪且装表完成后执行（静默失败期天然避开装表窗口）。
/// Update 必须 public + 双构造器（RoleUnlock RoleTicker 实证模式：interop 注入类的引擎回调
/// 只桥接 public 生命周期方法——2026-10-25 探针实锤 private 版在场景切换后停止回调）。
/// 2026-10-26 兓律（两连实弹事故定案）：**禁用 HarmonyX detour 挂编译器生成的 async 状态机
/// MoveNext**——d__256 锚点=Initialize ~10Hz 重试循环（2026-10-25 晚），d__0/EnterMain 锚点=
/// 装表后启动序列卡死黑屏（2026-10-26 凌晨，进程活/webview 活/窗口黑、内存 1.09GB 卡死）。
/// 两次均在"锚点选对"的前提下翻车：此类方法靠 XMM 非易失寄存器+栈保活状态机数据，蹦床不完整
/// 保存即静默损坏。免巡检若再迁：候补锚点=装表后续体里的**常规方法**（如 GameSaveManager.Initialize，
/// 与套件在役补丁同类），必须先离线论证其调用时机与全部调用方，论证材料呈作者批后才上机。</summary>
internal sealed class GuardTicker : MonoBehaviour
{
    private float _acc;
    public GuardTicker(IntPtr p) : base(p) { }
    public GuardTicker() { }
    public void Update()
    {
        _acc += UnityEngine.Time.unscaledDeltaTime;
        if (_acc < 2f) return;
        _acc = 0;
        if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;   // 总开关关=轮询停（已生效改动保留，重启干净）
        try { ScavengePlans.OnConfigStep(); } catch (Exception e) { EndlessGuardPlugin.Trace("ticker ScavengePlans: " + e.Message); }
        try { DeviceXpRuntime.OnStep(); } catch (Exception e) { EndlessGuardPlugin.Trace("ticker DeviceXp: " + e.Message); }
        try { ArrowTower.OnStep(); } catch (Exception e) { EndlessGuardPlugin.Trace("ticker Tower: " + e.Message); }
    }
}

/// <summary>fixwave5 F1 快注入 ticker（0.25s）：race 实证（两场 live trace，2026-10-31）——存档家具
/// 实例化可先于表注入落地（旧注入门=ConfigManager 就绪 + GuardTicker 2s 步进），输掉竞态的会话里
/// 设备以「无模型」态出生（官方 LoadGameObject(P_Tripo05) miss 返 null 时我方 supply 尚未 armed）。
/// 本 ticker 只跑表注入快路 ArrowTower.TryInjectFast()（不含 FX/heal 全量巡检），表首次全绿即退役
///（SetActive(false) + 一条 `[Tower] fast-inject armed→retired`），此后零开销常驻；2s 主 ticker 的
/// 哨兵重放职责不受影响。形态照抄 GuardTicker 已实证模式（RegisterTypeInIl2Cpp + 独立 GameObject
/// ——退役 SetActive(false) 不连坐主 ticker——+ DontDestroyOnLoad + AddComponent；public Update +
/// IntPtr 双构造器，interop 注入类引擎回调只桥接 public 生命周期方法）。门=Enabled 主开关 +
/// Defense.ArrowTowerEnabled（功能关=静默退役，不空转）。</summary>
internal sealed class FastInjectTicker : MonoBehaviour
{
    private float _acc;
    private bool _retired;
    public FastInjectTicker(IntPtr p) : base(p) { }
    public FastInjectTicker() { }
    public void Update()
    {
        if (_retired) return;
        _acc += UnityEngine.Time.unscaledDeltaTime;
        if (_acc < 0.25f) return;
        _acc = 0;
        if (EndlessGuardPlugin.Enabled != null && !EndlessGuardPlugin.Enabled.Value) return;
        try
        {
            if (EndlessGuardPlugin.ArrowTowerEnabled != null && !EndlessGuardPlugin.ArrowTowerEnabled.Value)
            {
                Retire(trace: false);   // 功能关=快注入永不成功，静默退役（不占 0.25s 空转；主 ticker 照管其余功能）
                return;
            }
            if (ArrowTower.TryInjectFast()) Retire(trace: true);
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Tower] fast-inject: " + e.Message); }
    }

    private void Retire(bool trace)
    {
        if (_retired) return;
        _retired = true;
        try { gameObject.SetActive(false); } catch { }
        if (trace) EndlessGuardPlugin.Trace("[Tower] fast-inject armed→retired");
    }
}
