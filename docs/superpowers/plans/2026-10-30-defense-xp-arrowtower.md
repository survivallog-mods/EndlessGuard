# 防御工事经验升级 + 箭塔 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给《生存日志》EndlessGuard mod 加"防御设备经验升级系统"（四件套+箭塔）和新设备"箭塔"（远程玻璃炮，程序化木塔模型）。

**Architecture:** 沿用套件既有模式——Harmony 补丁类由 `EndlessGuardPlugin.Load` 的逐类隔离挂载循环自动装载；表注入与属性重放走 2s `GuardTicker` 幂等巡检；纯数值逻辑（等级曲线/经验结算/账本）独立成无 il2cpp 依赖的类并用 xunit 单测覆盖；箭塔模型为运行时图元拼装 prefab 注入 `BattleResource` 缓存。数值与机制以 spec 为唯一权威。

**Tech Stack:** C# / net6.0 / BepInEx 6 IL2CPP / Il2CppInterop / HarmonyX / System.Text.Json / xunit（纯逻辑测试）。

**Spec:** `docs/superpowers/specs/2026-10-30-defense-upgrade-arrowtower-design.md`（已定稿，本计划所有数值从 spec 抄录）

## Global Constraints

- **零游戏存档写入**（套件红线）：mod 私有数据只写 `BepInEx/plugins/EndlessGuard_data/device_levels.json`。
- **禁挂 async 状态机 MoveNext**（2026-10-26 铁律）；**禁 patch get_/set_ 属性访问器**（红线7）——只做值直写与前/后缀补丁。
- 补丁类带 `[HarmonyPatch]` 即被现有挂载循环收编，禁止自建 Harmony 实例。
- **部署前必须关游戏**（红线8：运行中 dll 被锁，Copy-Item 静默失败）。部署 = 关游戏后 `cp bin/Release/net6.0/SurvivalLog.EndlessGuard.dll "E:/SteamLibrary/steamapps/common/Survival Log/BepInEx/plugins/"`。
- 自证：所有关键动作写 `BepInEx/EndlessGuard_trace.txt`（`EndlessGuardPlugin.Trace`），实机验收以 trace 行为准。
- 新表行 ID **一律 99005**（四表同号；预留段 99xxx）；`ResName="P_Tripo05"`；防线解锁挂 DefenseLv **Lv4**。
- cfg 键名与默认值按 spec §3.5 逐字实现。
- il2cpp 侧集合用 `Il2CppSystem.Collections.Generic.*`，访问前置 null 防护，异常只 `Trace` 不抛。
- 提交信息中文、随任务粒度；**不自行 push**。

## Review Focus（spec 隐含、最易翻车的五类输入 → 归属任务）

1. **读档后升级属性被官方重快照冲掉**（TowerDeviceComponent.OnInit 从配置重读）→ Task 4 校准重放 + 实机"升级→存读档→数值仍在"验证。
2. **防御减免误伤修理**（TakeDamage isRepair=true 是回复路径）→ Task 1 纯函数 `ShouldScaleIncomingDamage(isRepair)` 单测 + Task 3 前缀只走 false 分支。
3. **经验重复入账/记到非设备家具**（门/窗也走 TakeDamage；FireDevice 与伤害事件可能重叠）→ Task 1 `IsTrackedDevice` 白名单单测 + Task 3 每锚点独立入账、trace 可核对。
4. **官方换语言/重试重建表冲掉注入行**（Initialize 重建 `_Config_*_Dict`）→ Task 6 哨兵复查重放（ScavengePlans 同款）。
5. **弹道箭引用已死亡/已回收僵尸**（目标中途死亡、场景切换）→ Task 8 每帧校验目标存活 + 0.25s 上限强制消散。

---

### Task 1: 纯数值内核 `DeviceXpMath`（TDD）

**Files:**
- Create: `SurvivalLog.EndlessGuard/DeviceXpMath.cs`
- Create: `SurvivalLog.EndlessGuard.Tests/SurvivalLog.EndlessGuard.Tests.csproj` + `DeviceXpMathTests.cs`

**Interfaces (Produces，后续任务全部消费):**
```csharp
internal static class DeviceXpMath {
    int[] ThresholdsDefault { get; }            // {1200,3600,8400,16800}
    int LevelFromXp(int xp, int maxLevel, int[] thresholds);          // xp<1200→1；≥16800→5
    int NextThreshold(int level, int[] thresholds);                   // level=5→-1(MAX)
    int TargetAttack(int baseAtk, int level);                         // ×1.15^(lv-1) 四舍五入
    float TargetInterval(float baseInt, int level);                   // ×0.92^(lv-1)，下限 0.6×base
    int TargetMaxHp(int baseHp, int level, int stepAbs);              // base + (lv-1)×stepAbs
    float TargetRange(float baseRange, int level);                    // base + (lv-1)×0.55
    float IncomingDamageFactor(int level, float perLevelFactor);      // perLevelFactor^(lv-1)；箭塔传 1f
    int StepAbs(int baseHp, float ratio);                             // (int)(baseHp*ratio)：武器0.30/沙包0.25/箭塔0.15
    int AwardForFire(int attack, int hitCount, float mult);           // attack*hitCount*mult
    int AwardForTaken(int damage, float mult);                        // damage*mult（mult=沙包2.0）
    int AwardForKill(int killingDamage, float killRatio);             // killingDamage*killRatio
    bool IsTrackedDevice(int furnitureConfigId);                      // {36001,36002,36003,36004,99005}
    bool ShouldScaleIncomingDamage(bool isRepair);                    // !isRepair
}
```

- [ ] **Step 1: 写失败测试**（用 spec 数值做断言）：`LevelFromXp(1199,..)==1`、`(1200)==2`、`(16800)==5`；电锯 `TargetAttack(12,5)==21`（12×1.749）、`TargetInterval(2f,5)==1.43f±0.01`；沙包 `TargetMaxHp(600,5,150)==1200`；箭塔 `TargetRange(3.0f,5)==5.2f±0.01`、`IncomingDamageFactor(5,0.9f)==0.656f±0.001`、`IncomingDamageFactor(5,1f)==1f`；`AwardForTaken(3,2f)==6`；`ShouldScaleIncomingDamage(true)==false`。
- [ ] **Step 2: `dotnet test SurvivalLog.EndlessGuard.Tests` 预期编译失败**（类不存在）。
- [ ] **Step 3: 实现 `DeviceXpMath`**（纯托管代码，不引 interop；Tests csproj 只链接该单文件 `<Compile Include="../SurvivalLog.EndlessGuard/DeviceXpMath.cs" />`）。复利一律 `Math.Pow`，round 用 `MidpointRounding.AwayFromZero`。
- [ ] **Step 4: `dotnet test` 全绿。**
- [ ] **Step 5: Commit** `feat(xp): 设备经验纯数值内核（等级曲线/经验结算/目标属性）+ 单测`

### Task 2: 经验账本 `DeviceXpStore`（TDD）

**Files:**
- Create: `SurvivalLog.EndlessGuard/DeviceXpStore.cs`
- Test: `SurvivalLog.EndlessGuard.Tests/DeviceXpStoreTests.cs`

**Interfaces (Produces):**
```csharp
internal sealed class DeviceXpEntry { public int Xp; public int Lv; }
internal sealed class DeviceXpStore {
    DeviceXpStore(string jsonPath);                 // BepInEx/plugins/EndlessGuard_data/device_levels.json
    void Load();                                    // 文件缺失/损坏→空账本（不抛）；损坏时 Trace 一行
    void Save();                                    // 临时文件+File.Move 原子替换
    DeviceXpEntry GetOrNew(string slot, int configId, string cellKey);
    bool TryGet(string slot, int configId, string cellKey, out DeviceXpEntry e);
    void Set(string slot, int configId, string cellKey, int xp, int lv);
}
```
JSON 形状：`{"slots":{"<slot>":{"<configId>#<cellKey>":{"xp":n,"lv":n}}}}`。cellKey 形如 `12,8`（格坐标 x,z 取整）。

- [ ] **Step 1: 失败测试**：临时目录 round-trip（Set→Save→new Store Load→值一致）；损坏 JSON（写乱码）`Load()` 得空账本不抛；原子性（Save 后无 .tmp 残留）。
- [ ] **Step 2: 跑测确认失败。**
- [ ] **Step 3: 实现**（System.Text.Json；类自己不调 Trace，构造函数可注入 `Action<string>` logger，默认 null）。
- [ ] **Step 4: 测试全绿。**
- [ ] **Step 5: Commit** `feat(xp): 设备经验 JSON 账本（原子写/损坏容错）+ 单测`

### Task 3: 运行时追踪与 Harmony 补丁（经验入账 + 升级生效）

**Files:**
- Create: `SurvivalLog.EndlessGuard/DeviceXpRuntime.cs`（含全部 `[HarmonyPatch]` 嵌套类与静态账本引用）

**Interfaces:**
- Consumes: Task1 `DeviceXpMath.*`、Task2 `DeviceXpStore`。
- Produces: `DeviceXpRuntime.OnStep()`（Task 4 巡检入口）；`DeviceXpRuntime.SlotKey`（当前存档标识，string）；`static event Action<long, Vector3, long> DeviceFired`（Task 8 弹道事件：家具id、 muzzle 位置、目标id）；升级写属性静态方法 `ApplyLevel(Furniture f, int lv)`。

**锚点与行为（spec §3.4/§3.6）：**
- `FurnitureDurabilityComponent.TakeDamage(int damage, bool isRepair, string key, bool bypassShield)` 前缀：组件属主家具 `IsTrackedDevice` 且 `ShouldScaleIncomingDamage(isRepair)` 时 `damage=(int)(damage*IncomingDamageFactor(lv, perLevelFactor))` 写回入参（`__args` 或 ref 参数）；后缀：按实际掉血 `AwardForTaken`（沙包 mult=2.0，其余 1.0）。
- `TowerCombatManager.FireDevice(Furniture, TowerDeviceComponent, IReadOnlyList<long>)` 前缀：快照 targets 当前 HP；后缀：`AwardForFire(device.Attack, targets.Count, mult)`，HP≤0 者按 `AwardForKill(Attack, 0.5f)` 追加；触发 `DeviceFired`。
- `TowerCombatManager.ApplyZombieBite(Zombie)` 后缀：受咬者是 36002 时 `XP += CounterDamage(15)`。
- `TowerCombatManager.HitZombieByWreck(...)` 后缀：`XP += WreckDamage`。
- 升级：入账后 `LevelFromXp` 变化即 `ApplyLevel`——直写 `TowerDeviceComponent.Attack/AttackIntervalSeconds/AttackRangeSqr(=TargetRange²)`（箭塔专属射程，36005→99005 同）；`AddMaxDurability(delta)`+`AddCurrentDurability(delta)` 回满；材质 emissive 脉冲 0.6s。trace：`[XP] cfg=36004 cell=3,7 xp=3650/8400` 与 `[XP] lvup cfg=36004 lv=3`。

- [ ] **Step 1: 反汇编定位两件事**（disasm_tool + gamekb dump.cs RVA）：① BaseComponent→属主 Furniture 的引用字段；② FireDevice 内取目标 HP 的调用（快照口径）。产出写进类头注释。
- [ ] **Step 2: 实现补丁类 + 账本接线 + ApplyLevel**（slot 键先用 `GameSaveManager.SaveFileName`，拿不到则 `"default"`，Task 4 复核）。
- [ ] **Step 3: `dotnet build -c Release` 0 error 0 warning（新增代码）。**
- [ ] **Step 4: 实机验证**：关游戏部署→开测试档→放沙包+电锯→等一波尸潮→关游戏读 `EndlessGuard_trace.txt`：出现 `[XP] cfg=36001`（沙包承伤）与 `[XP] cfg=36004`（电锯开火）且 lvup 行数值与公式一致；沙包受击掉血明显低于面板伤害（防御系数生效）。
- [ ] **Step 5: Commit** `feat(xp): 四锚点经验入账+升级生效（TakeDamage减免/FireDevice/咬击反伤/残骸）`

### Task 4: 读档重放与幂等校准（巡检接线）

**Files:**
- Modify: `SurvivalLog.EndlessGuard/EndlessGuardPlugin.cs`（GuardTicker.Update 内加 `DeviceXpRuntime.OnStep()` try/catch，模式照抄 ScavengePlans 行）
- Modify: `SurvivalLog.EndlessGuard/DeviceXpRuntime.cs`

**Interfaces:**
- Produces: `OnStep()` 每 2s：① slot 键失效即切换账本上下文；② 遍历在场受追踪家具，组件属性≠`DeviceXpMath` 目标值→按等级校准重放（幂等，改动才 trace 一行 `[XP] recal cfg=36004 lv=3`）。

- [ ] **Step 1: 实现 OnStep + GuardTicker 接线。**在场家具枚举：AgentManager/场景家具列表（Step 3 反汇编已定位属主关系，反向遍历同源）。
- [ ] **Step 2: build + 部署。**
- [ ] **Step 3: 实机验证（Review Focus #1）**：升到 Lv2→存档→读档→trace 出现 `recal` 行且悬停面板攻击值仍为升级值；换另一个存档进游戏→账本按 slot 隔离（trace 无串档 recal）。
- [ ] **Step 4: Commit** `feat(xp): 读档重放+2s幂等校准（升级属性不丢）`

### Task 5: 悬停等级显示（样式A）

**Files:**
- Create: `SurvivalLog.EndlessGuard/DeviceXpHover.cs`

**Interfaces:**
- Consumes: `DeviceXpStore.TryGet`。
- Produces: `[HarmonyPatch]` 后缀挂 `Furniture.PushTowerDeviceHoverName(bool)`；显示格式：`名称 Lv.N · 升级 x/y`（满级 `Lv.5 MAX`），随样式A面板附带经验条与耐久条数据（若通道支持富文本/多行；仅纯文本则降级单行）。

- [ ] **Step 1: 反汇编 `PushTowerDeviceHoverName`（dump.cs RVA 0x306AAB0）**：确认其把名称推到哪（本地化通道/直接 UI 消息）。通道不可改写→置 `hoverFallback=true` 走样式B TextMesh 浮标（spec §3.7 工程兜底），并 trace 决策。
- [ ] **Step 2: 实现后缀/兜底 + cfg `Defense.ShowLevel` 门。**
- [ ] **Step 3: build + 实机**：悬停 Lv2 电锯显示「机械电锯 Lv.2 · 升级 x/3600」；`ShowLevel=false` 重启后不显示。
- [ ] **Step 4: Commit** `feat(xp): 悬停等级详情（样式A；通道不可写时样式B兜底）`

### Task 6: 箭塔四表注入 + 防线解锁

**Files:**
- Create: `SurvivalLog.EndlessGuard/ArrowTower.cs`（表注入；`internal static void OnStep()` 供巡检）
- Modify: `EndlessGuardPlugin.cs`（cfg 绑定 + ticker 接线）

**Interfaces:**
- Produces: 常量 `ArrowTower.Id=99005`、`ResName="P_Tripo05"`、配方材料 `{20005,20005,20004,20002}`；注入行字段按 spec §4.1/§4.2 抄录（Attack=14、AttackInterval=1.5、AttackRange=3.0、MaxHitTargets=1、PowerCost=6、PowerCostPerHit=2.5、HP=100；家具行含 SlotType=10/FsmName/FurnitureFunc=[9121,9122]/InstallTime=1800/isShowHP；包裹 TargetFurnitureID=99005/Category=14；配方 defense_exp=25/LifeMin=80）；DefenseLv Lv4 `unlock_recipes [403]→[403,99005]` 且 `Info_Local` 追加「新增：解锁 箭塔」。

- [ ] **Step 1: 实现幂等 upsert + 哨兵复查**（照抄 ScavengePlans.Inject/OnConfigStep 模式：dict.ContainsKey 判重、表被重建自动重放）。名称描述走 `Name_Local` 直写中文（透传显示先例）。
- [ ] **Step 2: build + 实机**：trace `[Tower] tables injected 4/4 + defenseLv4 unlocked`；工作台（防线等级≥4 测试档）出现「箭塔包裹」，材料正确、可制造。
- [ ] **Step 3: 实机延续**：放置箭塔→能被咬、有血条、修理按钮（9121/9122）可用；不给电时停摆（PowerMode 行为同电网）。
- [ ] **Step 4: Commit** `feat(tower): 箭塔99005四表注入+防线Lv4解锁（幂等+哨兵重放）`

### Task 7: 程序化塔模型 + 资源管线注入（含 spike）

**Files:**
- Create: `SurvivalLog.EndlessGuard/ArrowTowerModel.cs`

**Interfaces:**
- Produces: `static GameObject BuildPrefab()`（spec §4.3 结构件清单：石基/三段塔身/爬梯/挑台/垛口/四坡顶/旗杆红旗/重型弩机，≈60 部件；旋转弩机子物体名 `"Head"`）；`static void EnsureRegistered()`（建一次、注册 `BattleResource` 缓存键 `P_Tripo05`）。

- [ ] **Step 1: spike——反汇编 `LoadPrefabWithFallback`(0x2EE3910)/`TryGetCache`**：确认缓存键规则（全路径 vs 文件名）与 `StorePrefab(GameObject)` 入参语义；结论写类头。可行→Step 2 走注册；不可行→Harmony 前缀挂 `BattleResource.LoadGameObject` 命中 `P_Tripo05` 返回我方 prefab（普通实例方法，非红线）。
- [ ] **Step 2: 实现拼装**（Unity 图元 GameObject.CreatePrimitive+去 Collider；材质：运行时找一块已放置设备 renderer 克隆 Material 改色，找不到→`Shader.Find` URP SimpleLit 兜底；色值常量按预览页 7 档）。
- [ ] **Step 3: build + 实机**：放置箭塔显示完整木塔（非粉盒/隐形）、比例对齐四件套、被摧毁走官方下沉态。
- [ ] **Step 4: Commit** `feat(tower): 程序化精致木塔prefab+BattleResource注入（spike结论在内）`

### Task 8: 弩机动画 + 追踪式弹道

**Files:**
- Create: `SurvivalLog.EndlessGuard/ArrowTowerFx.cs`（per-frame MonoBehaviour，注册方式照抄 GuardTicker：ClassInjector.RegisterTypeInIl2Cpp + DontDestroyOnLoad）

**Interfaces:**
- Consumes: Task3 `DeviceFired(家具id, muzzle, 目标id)`；Task7 prefab 的 `"Head"` 子物体。
- Produces: 每帧 `Update()`：待机扫描（±15°/2s）与红旗摆动（±16°/0.9s）；开火转向（lerp ≤120°/s）+ 后坐 0.07/0.2s；箭矢模板=杆+锥头+双羽，**追踪式**飞向目标实时位置，命中/0.25s 上限/目标死亡三条件消散（Review Focus #5）。

- [ ] **Step 1: 实现动画与弹道**（目标位置经 AgentManager 按 id 查询，查无即消散；箭对象池≤8）。
- [ ] **Step 2: build + 实机**：开一波尸潮，箭全部命中受击僵尸（无空射），弩机转头追踪、后坐可见；目标死亡瞬间飞行箭正确消散。
- [ ] **Step 3: Commit** `feat(tower): 弩机索敌动画+追踪式弹道（必命中表现层）`

### Task 9: cfg / 文档 / 版本收尾

**Files:**
- Modify: `EndlessGuardPlugin.cs`（cfg：`Defense.XPEnabled/XPEnabled→Defense.*` 全部键按 spec §3.5 绑定，默认值逐字对齐）
- Modify: `README.md`、`DESIGN.md`、`CHANGELOG.md`（功能清单、cfg 表、机制记录；版本号**留作者定号**，csproj Version 不动）

- [ ] **Step 1: cfg 键补全 + 读写路径核对（cfg 描述含人话说明）。**
- [ ] **Step 2: 文档更新**：README 中文+English 各加一节；DESIGN.md 记录四锚点/账本/注入/spike 结论；CHANGELOG 记两功能。
- [ ] **Step 3: 全量回归**：`dotnet build` + `dotnet test` 全绿；实机跑一轮（升级/读档/悬停/制造/弹道五项各验一次 trace 或目视）。
- [ ] **Step 4: Commit** `docs+cfg: 防御升级与箭塔功能文档、配置项、回归`

---

## Self-Review 记录

- **Spec 覆盖**：§3.1/3.1.1 经验入账→Task3；§3.2 曲线→Task1；§3.3 持久化→Task2；§3.4 锚点→Task3；§3.5 cfg→Task9（运行键）+Task3（消费）；§3.6 生命周期→Task3（账目与格子绑定，摧毁保留=自然结果）；§3.7 等级显示→Task5；§4.1 数值→Task1/6；§4.2 注入→Task6；§4.3 模型→Task7；§4.4 动作/弹道→Task8；§4.5 特效留空=默认无特效（Task8 仅箭矢）；§7 交付物→Task9。无缺口。
- **类型一致**：`DeviceFired(long,Vector3,long)`/`ApplyLevel`/`OnStep()`/`Id=99005`/`"Head"` 各任务引用一致。
- **比例**：计划以签名+spec 值为主，无函数体转抄。
