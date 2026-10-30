# 无尽守卫 EndlessGuard（Survival Log MOD）

> 作者 Night · 抖音 166660371 · 免费分享 · 存档只通过游戏自己的接口写入（不用任何私有格式）/ 不改游戏文件（详见第三节）
> 版本随套件统一管理；适配游戏版本以仓库兼容说明为准。

## 一、这是干什么的

无尽模式玩到后期，交易点和野外探险图会按阶段一个个沦陷，全交易点沦陷后连无人机交易入口都会收起，长期档等于慢性死亡。本 mod 默认四件事一起做：新沦陷全部拦下（三种模式通用）、已沦陷的点读档复活、地图上的沦陷倒计时清掉，再补 11 条后期拾遗规划，让产出和生存点开销接得上。另带两件防御向功能：防御设备打怪攒经验逐级成长，以及一台全新设备「箭塔」（见功能清单后两条）。

## 二、功能清单

- **永不沦陷**（默认开）：拦截全部新沦陷，纯无尽 / 剧情无尽 / 休闲三模式通用。
- **旧档复活**（默认开）：读档后自动恢复已沦陷的交易点 / 探险图，无人机交易入口随之回来；已进入撤离剧情链的营地带剧情状态，保持不动（保剧情一致性）。
- **倒计时清场**（默认开）：地图上挂着的沦陷倒计时全部清掉，重开地图即消失。
- **11 条拾遗系列新规划**（默认开）：超市拾遗·六~十、医院踏勘·三~五、学校摸排·三~五，第 100 天起每 20 天解锁一条（D100~D300），全买共 494 生存点；每张卡解锁一批新物品，并把官方埋在池子里的高门槛好货降到可解锁。
- **越买越富**：探索物资数量 = 存档"探索物资"难度档位 + 每张已购新卡 +0.3 期望份数（步长可调，0=纯官方难度）；标准难度下超市五张买满约每件 2.5 份。
- **购买通道说明**：新规划不在每日三选一的随机池里，通过「自选规划」列表上架购买（与本套件 PlanPick 同装即出现在其目录中）；不装 PlanPick 时，前三项功能照常，新规划无处可买。
- **设备经验升级**（默认开）：防御设备（沙包 / 尖刺障碍 / 电网 / 机械电锯 / 箭塔）打怪、承伤、击杀都攒经验，逐级变强——武器类攻击 +15%/级、攻速 +8%/级、耐久 +30%/级、受伤 -10%/级；沙包专精血牛（耐久 +25%/级、受伤 -12%/级）；箭塔另加射程 +0.55 格/级。升级瞬间回满血，悬停设备名显示「Lv.N · 升级 x/y」（满级 MAX）。读档 / 修理后 2 秒内自动校准属性（升级不丢）。数据存 mod 本地 JSON，不写游戏存档。
- **新防御设备：箭塔**（默认开）：工作台新配方「箭塔包裹」（木片×2 + 铁皮 + 碎玻璃），防线 Lv4（工事技师）解锁。定位高攻速射远射程的单体玻璃炮：攻 14 / 1.5 秒一发 / 3 格射程起步（满级 5.2 格），血量只有电锯一半、无减伤成长——输出换生存，被摸到就疼。外观为程序化拼装的木质哨塔（零外部资产），弩机自动索敌转向，箭矢追踪弹道必命中。

## 三、安全与还原

- 不改游戏文件 / 删 dll 即还原。
- 存档只通过游戏自己的接口写入：恢复已沦陷点 = 把点从游戏官方的沦陷名单里移除（这正是功能本身）；新规划的购买入档也走游戏官方存档。不使用任何私有格式——全新无尽档天然就是这套状态。
- 设备等级数据存 mod 本地 JSON（`BepInEx\plugins\EndlessGuard_data\device_levels.json`），不写游戏存档；箭塔与数值注入全是进程内存表。删 dll 后设备数值读档即还原官方原厂值，等级记录留在 mod 文件夹里（无害残留，重装即续）。
- 卸载后：已恢复的交易点 / 探险图、已购买的新规划都保留（它们已是游戏官方数据）；已购的 11 条新规划会留下无害的孤儿记录。新沦陷恢复官方判定（会继续按日程沦陷）。
- 不回滚已经触发过的一次性事件（沦陷广播、遗产事件是历史新闻，只拦以后）。
- 已进入撤离剧情链的营地不恢复（剧情一致性）。

## 四、安装与卸载

**前置**：Steam 正版《生存日志》（AppID 4164790）+ BepInEx 6（IL2CPP）注入链。
- **Full 完整包（推荐新玩家）**：解压到游戏根目录（Steam 库 `steamapps\common\Survival Log`），自带 BepInEx 6（IL2CPP）基座，装完即用；
- **PluginOnly 纯插件包**：已装 BepInEx 6 的玩家，把 `SurvivalLog.EndlessGuard.dll` 放进 `<游戏目录>\BepInEx\plugins\`。

**卸载** = 删掉该 dll（Full 包玩家要彻底清基座：再删 `BepInEx\` 目录、`winhttp.dll`、`dotnet\` 目录；存档不受影响），新沦陷恢复官方判定，已恢复的点与已购规划保留（官方存档数据）。游戏更新后失效属正常，等套件适配新版即可；本 mod 不改任何游戏文件。

## 五、配置说明

配置文件：`BepInEx\config\com.local.survivallog.endlessguard.cfg`（各开关按现值即时工作，无需重启；新规划条目关闭后自下次启动起完全消失）。用记事本可改。

| 节.键 | 默认 | 说明（人话） |
|---|---|---|
| General.Enabled | true | 总开关：false=全部功能还原官方（本局已生效的改动保留，重启后完全干净） |
| Fall.PreventNewFalls | true | 拦截全部新沦陷（三模式通吃） |
| Fall.RestoreFallenPoints | true | 读档后恢复已沦陷的交易点/探险图（营地除外，重复读档安全） |
| Plans.ExtendScavengePlans | true | 注入 11 条拾遗扩展规划（超市·六~十/医院·三~五/学校·三~五，D100-D300） |
| Plans.GrantPlanId | 0 | 补授单张丢失的新规划卡 id，进档一次自动补入（不扣点），补完自动清零；0=关闭 |
| Plans.GrantPlanIds | （空） | 补授多卡版：逗号分隔的卡 id 列表，逐卡补入，全部确认后自动清空 |
| Defense.XPEnabled | true | 设备经验升级总开关：false=经验系统整体停用（入账/等级属性/巡检校准/悬停等级全停；已生效属性本局保留，重启读档后还原官方原厂值） |
| Defense.XPMultiplier | 1.0 | 经验获取倍率：开火/承伤/击杀/反伤入账全乘这个数（2=升级速度翻倍） |
| Defense.XPThresholdScale | 1.0 | 升级阈值整体缩放（>1=更难升级，0.5=阈值减半；只影响今后的经验判定，已到手等级不回退） |
| Defense.MaxLevel | 5 | 设备等级上限（1~5；1=关闭成长；只封顶今后升级，已超上限的属性保留，调回自动续上） |
| Defense.KillBonusRatio | 0.5 | 击杀追加比例：打死每只丧尸额外给「该次伤害×比例」的经验（0=关闭） |
| Defense.SandbagXPRate | 2.0 | 沙包承伤经验系数：别的设备掉 1 血得 1 经验，沙包掉 1 血得这么多倍（练功袋定位） |
| Defense.WeaponAtkStep | 0.15 | 武器类（尖刺/电网/电锯/箭塔）每级攻击 +15% 的比例（复利；改后 2 秒内对已升级设备自动重算） |
| Defense.WeaponIntervalStep | 0.08 | 武器类每级攻击间隔缩短 8% 的比例（下限=基础间隔的 60%；改后 2 秒内自动重算） |
| Defense.WeaponHpStepRatio | 0.30 | 武器类每级耐久上限 +基础值×此比例（改后 2 秒内自动重算） |
| Defense.WeaponDefFactor | 0.90 | 武器类每级受伤×此系数（0.9=每级减伤 10%，1=不减伤；改后 2 秒内自动重算） |
| Defense.SandbagHpStepRatio | 0.25 | 沙包每级耐久上限 +基础值×此比例（比武器类更能堆血；改后 2 秒内自动重算） |
| Defense.SandbagDefFactor | 0.88 | 沙包每级受伤×此系数（每级减伤 12%；改后 2 秒内自动重算） |
| Defense.TowerRangeStep | 0.55 | 箭塔射程每级 +此格数（基础 3 格，Lv5=5.2 格；改后 2 秒内自动重算） |
| Defense.ShowLevel | true | 悬停防御设备名附等级与升级进度（名称 Lv.N · 升级 x/y，满级 Lv.N MAX）；false=还原官方悬停名 |
| Defense.ArrowTowerEnabled | true | 注入新设备「箭塔」（工作台配方 + 防线 Lv4 解锁 + 数值表）；false=停止注入（本局已注入的保留，重启后完全干净） |

> 旧版本说明：`Fall.StripPendingFallTags`（剥倒计时 tag 改为无条件执行）与 `Plans.RichnessStep`（物资份数完全按官方"探索物资"难度档）两个键已于 2026-10-25 撤除，cfg 文件里残留的旧键 BepInEx 自动忽略，无需手清。

## 六、English

**Endless Guard** — Endless mode never falls: block all new falls, revive fallen trade points/expedition maps, clear the fall countdowns, plus 11 late-game scavenge plans.

**Features**
- No more falls (default on): every new fall is blocked in all three endless modes (pure endless / story endless / casual).
- Old-save revival (default on): fallen trade points and expedition maps come back on save load, including the drone trade entrance; camps already in the evacuation story chain stay fallen.
- Countdown cleanup (default on): pending fall countdowns on the map are stripped.
- 11 new scavenge plans (default on): Supermarket VI–X, Hospital III–V, School III–V, one every 20 days from day 100 (D100–D300), 494 survival points in total; each card unlocks new items and lowers officially gated loot to reachable levels.
- Richer loot as you buy: expected copies per item = your save's official explore-loot difficulty + 0.3 per purchased new card (step adjustable; 0 = pure vanilla). At Standard difficulty with all five supermarket cards, roughly 2.5 copies per item.
- Purchase channel: the new plans are not in the daily random pool; they are listed and bought through the Plan Pick catalog (install the suite's Plan Pick to see them). Without Plan Pick, the first three features still work but the new plans cannot be bought.
- Device XP leveling (default on): defense devices (sandbag / spike barrier / electric fence / chainsaw / arrow tower) earn XP from shooting, tanking hits and kills, and level up — weapon-class devices gain +15% attack, +8% fire rate, +30% durability and -10% damage taken per level; the sandbag is the HP specialist (+25% durability, -12% damage taken per level); the arrow tower additionally gains +0.55 range tiles per level. Devices heal to full on level-up, and hovering a device shows "Lv.N · x/y" (MAX at cap). Stats are re-calibrated within 2 seconds after loading a save. Level data lives in a mod-local JSON, never in the game save.
- New defense device — Arrow Tower (default on): a workshop recipe ("Arrow Tower Package": wood chip ×2 + iron sheet + broken glass), unlocked at defense line Lv4. A single-target glass cannon — high attack (14), fast fire (1.5s), long range (3 tiles, up to 5.2 at Lv5) with only half the chainsaw's HP and no damage-reduction growth. Rendered as a procedurally assembled wooden watchtower (zero external assets) with an auto-aiming ballista and hit-certain tracer arrows.

**Requirements / Install / Uninstall**
Survival Log (Steam AppID 4164790) + BepInEx 6 IL2CPP. Install the Full package (bundled BepInEx 6 base — extract into the game root, `steamapps\common\Survival Log`) or the PluginOnly package (drop `SurvivalLog.EndlessGuard.dll` into `BepInEx\plugins\` if BepInEx 6 is already installed). Uninstall = delete the DLL; new falls resume the vanilla schedule, while revived points and purchased plans are kept (official save data; the 11 purchased entries leave harmless orphan records). Device levels live in `BepInEx\plugins\EndlessGuard_data\device_levels.json` (mod-local JSON) — deleting the DLL reverts every device stat to vanilla on the next save load.

**Configuration**
Config file: `BepInEx\config\com.local.survivallog.endlessguard.cfg`. Key knobs: the three fall-protection switches (all default on), and the `Defense.*` group for the XP system and arrow tower — master switch `Defense.XPEnabled`, plus tunables for XP multiplier, threshold scale, level cap (1–5), kill bonus, sandbag rates and per-class growth steps (all default to spec values; step changes re-calibrate leveled devices within 2 seconds), `Defense.ShowLevel` (hover readout) and `Defense.ArrowTowerEnabled` (device injection). Changes take effect without restarting.

**Credits**
By Night (Douyin 166660371). Free for personal use. Do NOT re-upload or redistribute anywhere without permission.
