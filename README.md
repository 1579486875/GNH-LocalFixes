# GNH 本地修复补丁（GNH.LocalFixes）

> **📦 下载**：[最新版本（Release）](https://github.com/1579486875/GNH-LocalFixes/releases/latest) —— 下载 zip，解压后放进 `Mods\` 目录即可（内含编译好的 dll，需要 Harmony）。

本机模组组合的修复合集。packageId `gnh.cn.cys.localfixes`。
部署目录名：`GNH-本地修复补丁`（放进 RimWorld 的 `Mods\` 目录）

- 模组版本：**1.3.14**　·　适用版本：RimWorld **1.6**　·　依赖：**Harmony**

## 构建与部署

    dotnet build GNH.LocalFixes.csproj -c Release
    # bin\Release\ 直接就是可部署的模组结构（About + Assemblies + Patches），整体复制到 Mods 目录

`refs\` 下的引用 DLL 不会进入产物（所有 `<Reference>` 都有 `<Private>false</Private>`）；
`EnableDefaultNoneItems=false` 用于防止把 refs 误打包进模组。

## 十四项修复

十四项互相独立，均为最小侵入，可整体或逐项停用。

| # | 内容 | 承载 |
| --- | --- | --- |
| 1 | ElToro「观看」任务类缺失（`JobDriver_BestialityInvite_Watching`） | `src/JobDriver_BestialityInvite_Watching.cs` |
| 2 | CeleTech 智能切换武器在容器武器上取射程导致 NRE | `src/Patch_VerbProperties_AdjustedRange.cs` |
| 3 | VFE Empire 爵位层级缺 `RoyalTitleDefExtension` | `Patches/VFEE_RoyalTitleExtFix.xml` |
| 4 | Vehicle Framework 开发者面板的「查不到就报错」兜底 | `src/VehicleFrameworkDebugFix.cs` |
| 5 | VFE Empire「乐器空间」静态构造崩溃 | `src/VFEEInstrumentSpaceFix.cs`（由 `src/LocalFixesMod.cs` 安装） |
| 6 | **打开游戏「模组」页面即崩溃**（brrainz CrossPromotion 调用已废弃的 Steam API） | `src/CrossPromotionUnpatch.cs` + `src/SteamWorkshopHookFix.cs` |
| 7 | RJW 动物动画对 Rotti 的悬空引用 | `Patches/ElToroAnimsRottiRefFix.xml` |
| 8 | 被「禁用房间要求」连累跳过的 VFE 功能 | `Patches/VFEPianoFirepitRestore.xml` |
| 9 | CCOE 月经结算的 `TargetException`（`?.` 保护错了对象） | `src/CcoeReflectionFix.cs` |
| 10 | 原版植入体生成的「0 权重」空引用崩溃（**两层**：前缀预判 + 收尾器兜底） | `src/TechHediffsZeroBudgetFix.cs` |
| 11 | **每帧 `InvalidCastException`**：非乐器混进「乐器」分组（游戏被拖到约 2fps）；同时拦下「挂着配方却不是工作台」的脏对象 | `src/MusicManagerFadeoutFix.cs` |
| 12 | **中文环境下 `<li>Royalty</li>` 永远匹配不上（原版路径）** —— 官方 DLC 的显示名会被语言包翻译，导致多个模组的补丁整块静默失效 | `src/FindModLanguageFix.cs` |
| 13 | 天鹰溪谷联邦往炮塔 Def 塞入 VFE Security 1.6 已删除的类型，导致整个 Def 加载失败、一批炮塔消失 | `Patches/FixTY2ValleyLongRangeArtillery.xml` |
| 14 | **XmlExtensions 的 `FindMod` 同样败给翻译**（另一条独立路径）：它自己遍历 `RunningMods` 比名字，中文下判定 DLC 恒为 false，而且**连一句红字都不留** | `src/XmlExtensionsFindModFix.cs` |

### 第 12 项与第 14 项：同一个病，两条独立的路径

「找不到模组」这件事在游戏里有**两套互不相干的判定代码**，必须分别修：

| | 路径 | 判定依据 | 修在哪 |
| --- | --- | --- | --- |
| ① | `Verse.PatchOperationFindMod.ApplyWorker` → `ModLister.HasActiveModWithName` | `ModMetaData.Name`（DLC 取自 `ExpansionDef.label`，**会被翻译**） | 第 12 项 |
| ② | `XmlExtensions.FindMod.Patch` / `Boolean.FindMod.Evaluation` → 自己遍历 `LoadedModManager.RunningMods` | `ModContentPack.Name`（同样会被翻译） | 第 14 项 |

两者都比 `m.Name.ToLower() == mod.ToLower()`，而简体中文下 `Royalty` 的实际名字是「皇权」。

区别在于**失败时的表现**：路径 ① 会留下一行
`Patch operation Verse.PatchOperationFindMod(Royalty) failed`；
路径 ② 的布尔版本（`Boolean.FindMod.Evaluation`）只是把结果写进 `ref bool`，
**从不报错** —— 补丁整块不生效，日志里却一条痕迹都没有。

第 14 项的修法刻意「不绕过原逻辑」：只在**原本注定失败**时，把作者写的 DLC 短名
**临时替换**成它的实际显示名，让原代码自己跑完（`caseTrue` / `caseFalse` / `logic` /
`foundMod` 全部原样执行），判定结束后原地还原。
安全边界收得很紧：**只认 `Ludeon.RimWorld` 前缀**的官方模组 —— 否则「以 `.Core` 结尾的模组」
会让所有 `<li>Core</li>` 突然命中，那比原 bug 更糟。

> ⚠️ **副作用要说清楚**：第 14 项生效后，那些按「名字」判定 DLC 的第三方补丁会**真正开始执行**。
> 它们本来就该执行，但若年久失修，可能会冒出新的红字。权衡下来仍然选择修：
> 静默不生效比报错更难排查，玩家只会觉得「这个模组的内容怎么缺了一块」。
> （本机实测：112 处 `XmlExtensions.FindMod` 调用全部写了 `<packageId>true</packageId>`，
> 走的是不会被翻译的 packageId，所以目前**零处受影响**。）

### 第 8 项与第 12 项的「红字」是怎么回事（2026-10-04 查清，容易被误判）

装上第 12 项之后，日志里会**多出**四行：

```
[Blue Archive Furniture]                      Patch operation Verse.PatchOperationFindMod(Royalty) failed
[Vanilla Furniture Expanded]                  Patch operation Verse.PatchOperationFindMod(Royalty) failed
[华夏扩展 Chinese Comprehensive Expansion]     Patch operation Verse.PatchOperationFindMod(Royalty) failed
[Vanilla Furniture Expanded - Spacer Module]  Patch operation Verse.PatchOperationFindMod(Royalty) failed
```

**这不是新增的损坏，而是老问题第一次被如实报出来。** 完整因果链：

1. 反编译 `Verse.PatchOperationFindMod.ApplyWorker` 原文：
   `if (flag) { if (match != null) return match.Apply(xml); } … return true;`
   —— **判定失败时返回 `true`（安静跳过，不报错）**；只有 `<match>` 里的子操作失败，
   才会把 `false` 抛上来。
2. 而红字的打印条件是 `neverSucceeded`，它只在 `Apply` 返回 `false` 时被留下：

   ```csharp
   // Verse.PatchOperation.Apply
   bool flag = ApplyWorker(xml);
   if (success == Success.Always) flag = true; else if (…) …
   if (flag) neverSucceeded = false;      // ← 成功过一次就清掉标记
   return flag;

   // Verse.PatchOperation.Complete（红字就是这里打的）
   if (neverSucceeded)
       Log.Error($"[{modIdentifier}] Patch operation {this} failed\nfile: {sourceFile}");
   ```

   —— 所以：**红字出现，恰恰证明 `Royalty` 被找到了**（否则 `ApplyWorker` 会返回 `true`，
   标记被清掉，一行字都不会有）。出错的是 `<match>` 里那条 `PatchOperationAdd` 的 xpath。
3. 真正的元凶是 **`disroom.mashiro`**（工坊 `3297881350`，中文名 `disabledroomRequirements`，
   本机加载位次 **470**）：它用
   ```xml
   <Operation Class="PatchOperationReplace">
     <xpath>Defs/RoyalTitleDef[@ParentName = "BaseEmpireTitle"]/bedroomRequirements</xpath>
     <value><bedroomRequirements Inherit="False" /></value>
   </Operation>
   ```
   把所有帝国爵位的「卧室要求」整个换成空表。而报错的那批家具模组位次在 **687~722**，
   排在它后面，想往
   `…/bedroomRequirements/li[@Class="RoomRequirement_ThingAnyOf"]/things`
   里加床时，节点已经不存在了。
4. 在 v1.3.11 及更早，第 12 项还不存在 → `FindMod(Royalty)` 判定为 false → 整块 `<match>`
   被静默跳过 → **什么都没做，也什么都不报**。v1.3.12 修好判定后，`<match>` 终于执行，
   这才轮到它因为节点被清空而报错。

**这几行红字无害**：它们只想给「贵族卧室」多登记几张床，而卧室要求已被整个删掉，
登记与否都不影响游戏。真正有价值的连带损失（VFE 的钢琴 / 营火 / 石棺那 8 条）
已由第 8 项补做回来。

**想彻底消掉这四行红字**，最干净的办法是**调整加载顺序**：把 `disroom.mashiro`
挪到那批家具模组（工坊 `3491176484` / `1718190143` / `3221850511` / `2028381079`）之后。
它们先正常追加、`disroom.mashiro` 最后再统一清空，**最终游戏效果完全一样，红字不再出现**。


### 第 10 项：为什么最终必须再加一个收尾器（2026-10-04 19:12 实机日志）

起初这一项只有一个前缀，判断「预算上限 ≤ 0、没有必装植入体、原版确实会进入抽取循环」。
但 19:12 的日志证明**不够** —— 前缀被调用了、却放行了，原版照样崩：

```
ERROR: RandomElementByWeight with totalWeight=0 - use TryRandomElementByWeight.
ERROR: Error while generating pawn. Rethrowing. Exception: System.NullReferenceException
at RimWorld.PawnTechHediffsGenerator.GenerateTechHediffsFor (...) [0x001ac]
  - TRANSPILER ZuoYao.RavenRace.Harmony: ...Patch_FusangFluidImplantPreference:Transpiler
  - PREFIX     gnh.cn.cys.localfixes: GNH.LocalFixes.TechHediffsZeroBudgetFix:Prefix   ← 我们被调用了
```

于是把前缀改成「在候选里找一个**真正抽得中**的（市值 > 0、标签命中、未 disallow、
非暴力件而小人又禁暴力）」，找不到才跳过。**结果还是不够** —— 反编译原方法后原因很清楚：

```csharp
float partsMoney = pawn.kindDef.techHediffsMoney.RandomInRange;   // ← 每只 Pawn 随机抽一次
...
IEnumerable<ThingDef> source = DefDatabase<ThingDef>.AllDefs.Where(x =>
    x.isTechHediff
    && !tmpGeneratedTechHediffsList.Contains(x)      // ← 同一只小人装过的不要再挑
    && x.BaseMarketValue <= partsMoney               // ← 用**抽到的**值做门槛
    && ...);
if (source.Any()) {
    ThingDef thingDef = source.RandomElementByWeight(w => w.BaseMarketValue);
    partsMoney -= thingDef.BaseMarketValue;           // ← 崩在这里
    InstallPart(pawn, thingDef);                      // ← 还没执行到
}
```

前缀能看到的是 `techHediffsMoney` 的**范围**，看不到这一只 Pawn 抽到了多少；
而且 `tmpGeneratedTechHediffsList` 会把 `MaxAmount > 1` 时的第二轮候选掏空。
这两件事都**无法在前缀里可靠预测**（替它抽签会改变后面所有随机数，代价更大）。

**所以第二层做成收尾器（Finalizer）**：只在真的抛 `NullReferenceException` 时接管，
记一条写明 PawnKind 的 `Warning`（不静默），然后安静结束这次调用；其它异常原样抛出。

这样做之所以安全，是因为**崩溃点与「改坏东西」之间还隔着一步**：
NRE 发生在 `partsMoney -= thingDef.BaseMarketValue`（`thingDef` 为 null），
此时 `partsMoney` 没动、`InstallPart` 还没执行 —— **没有装到一半的残留**。
被兜住的结果与原版真正想要的语义（它自己的报错信息就在教人改用
`TryRandomElementByWeight`）完全一致：这次抽不中，不装这一个。

> 顺带说明：堆栈里那行 `TRANSPILER ZuoYao.RavenRace …` 不是它的锅。
> RavenRace 只是把同一个调用点换成了自己的 `ChooseImplant`，
> 而 `ChooseImplant` 最后一行 fallback 仍是原版那句 `source.RandomElementByWeight(weight)` ——
> 原版的边界缺陷被完整保留了下来。

### 第 11 项与第 8 项的关联（2026-10-04 实测案例，值得一读）

第 8 项的补丁会做一件 VFE 作者本来就想做的事：把 `Joy_Piano` 的
`ParentName` 改成 `MusicalInstrumentBase`，让它改用皇权的音乐系统。
副作用是这个 def 的 `thingClass` 变成了 `Building_MusicalInstrument`，
**而改动之前就已经摆在地图上的那架旧钢琴，它的对象真实类型仍然是 `Verse.Building`** ——
对象的类型是改不掉的。于是它就成了「def 说是乐器、对象却不是乐器」的脏对象：

- 游戏的分组判定（`Verse.ThingListGroupHelper.Includes`）**只看 `def`**，所以它被判进「乐器」分组；
- 而 `MusicManagerPlay.UpdateMusicFadeout` 会把分组里的东西**强行转成** `Building_MusicalInstrument`，
  于是每帧抛 `InvalidCastException`。每次异常都要抓堆栈并写日志，实测把游戏拖到**每秒约 2 帧**。

第 11 项的修法：给 `ListerThings.Add` 挂一个收尾器，在「登记进分组」的那一刻就把它拦下来，
从源头不让它进组；另有 `MusicManagerPlay.UpdateMusicFadeout` 上的收尾器作为兜底。

> ⚠️ **这里踩过一个坑，已改正，记录下来避免重犯**：
> 最初的做法是写一个 `Verse.MapComponent` 子类来定期清理。但反编译 `Verse.Map.ExposeComponents`
> 可以看到 `Scribe_Collections.Look(ref components, "components", LookMode.Deep, this)` ——
> **MapComponent 会被写进玩家的存档**。将来一旦卸载本模组，存档里那条
> `<li Class="GNH.LocalFixes.MusicGroupSanitizer" />` 就会变成
> `Can't load abstract class Verse.MapComponent` 这样的脏数据。
> 所以最终改成了**不新增任何会被存档记录的东西**的方案。


## ⚠️ 硬性规则（2026-10-02 事故教训，改本工程前必读）

1. **补丁安装必须幂等。** RimWorld 的 `LoadedModManager.CreateModClasses()` 一局之内会**被多次调用**
   （触发它的是开发者工具的**热重载**与**切换语言**；**不含**打开游戏内的「模组」页面 —— 已反编译核实）。因此**绝不能在 `Mod` 子类构造函数里做无保护的
   补丁安装** —— Harmony 补丁是叠加的，每次重入都会再套一层 prefix，
   表现为「越操作越卡」直至卡死崩溃。所有安装点都必须有静态标志守卫：
   `if (installed) return; installed = true;`

   **v1.3.10 起不再用 `Harmony.PatchAll` 一把梭**，改为「逐类隔离安装」
   （见 `src/LocalFixesMod.cs` 里的 `InstallAllPatchClasses`）。改的原因有两条：

   - `PatchAll` 内部第一步是 `GetTypes()`，而本程序集里有一个类在**编译期**引用了
     `ElToro_BAddon.dll` 与 `RJW.dll`。用户一旦禁用这两个模组，`GetTypes()` 就会抛
     `ReflectionTypeLoadException` —— **整批类型都取不到，补丁包会一个都装不上**。
     现在改为兜住该异常，把能加载的那部分类型继续用完。
   - `PatchAll` 遇到任何一个补丁类失败就**整体中止**，排在后面的类全都不装。
     现在每个类单独 `try/catch`，失败只记一条 Error，不影响别的类，而且下次会单独重试。

   相应的，防重复标记也从「一个全局 bool」改成了「按补丁类记录的集合」：
   失败的类能单独重试，已经装好的那些绝不会被重复安装。

2. **`AccessTools.Method(type, name, parameters, generics)` 的最后一个参数是「泛型类型参数」，不是返回类型。**
   对非泛型方法必须传 `null`，否则抛 `InvalidOperationException: not a generic method definition`。
   需要按返回类型筛选时，拿到 `MethodInfo` 后自行比较 `ReturnType`。

3. **验证「时机正确」≠ 验证「调用次数」。** 把补丁或副作用挂到某个入口之前，除了确认它"足够早"，
   还必须确认它"只会跑一次"。RimWorld 中 `Mod` 构造函数、`[StaticConstructorOnStartup]`、
   `CreateModClasses` 的调用次数并不相同：静态构造受 CLR 保证只跑一次；
   `CreateModClasses()` 本身一局内会被多次调用，但它有一句
   `if (!runningModClasses.ContainsKey(type))` 去重（`runningModClasses` 是静态字段，只在
   `LoadedModManager` 的静态构造里创建一次、从不清空），所以正常路径下同一个 `Mod` 类只构造一次。
   **唯一的例外是「`Mod` 构造函数自己抛异常」**：`runningModClasses[type] = Activator.CreateInstance(...)`
   是「先构造、后赋值」，构造抛异常时字典里留不下记录，下次 `CreateModClasses()` 还会再构造一次。
   （已反编译核对：`runningModClasses` 全库只有 6 处引用，没有任何 `Clear`。）

4. **改完必看自检日志的时间戳。** 若自检日志出现时刻与预期加载阶段不符
   （例：游戏 18:45 启动，自检 19:06 才打印），说明该入口被推迟或重复触发，必须查清再收工。

## 事故记录

- **v1.2.0（已废弃）**：把 `PatchAll` 放进 `Mod` 构造函数且无幂等守卫，并且在同一个构造函数里
  调用 `AccessTools.Method` 时误用了 `generics` 参数 → 抛 `InvalidOperationException`。两件事叠加出事故：
  构造函数抛异常后 `runningModClasses` 里写不下这一项（先构造、后赋值），
  于是**每次** `CreateModClasses()`（打开游戏内模组管理器就会触发）都重新构造这个 `Mod` 类、
  重新跑一遍 `PatchAll` → Harmony 补丁层层叠加 → 游戏卡死崩溃；
  而那个异常被吞掉，导致第 5 项修复从未生效却没人知道。
- **v1.2.1**：加 `installed` 静态守卫（`LocalFixesMod` 与 `VFEEInstrumentSpaceFix` 双重），
  并将 `AccessTools.Method` 的 `generics` 改为 `null`。

---

## 关于本仓库

**许可**：[MIT](LICENSE)

**编译需自备 `refs\`**：本工程的 `<Reference>` 指向 `refs\` 下的程序集 —— 那些是游戏本体与第三方模组的 DLL，
版权不属于本项目，因此不纳入仓库。编译前请自行准备：

| 需要的 DLL | 来源 |
| --- | --- |
| `Assembly-CSharp.dll`、`UnityEngine.dll`、`UnityEngine.CoreModule.dll` | RimWorld 安装目录 `RimWorldWin64_Data\Managed\` |
| `0Harmony.dll` | [Harmony 模组](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) 的 `Assemblies\` |
| `RJW.dll` | RimJobWorld 模组 |
| `ElToro_BAddon.dll` | ElToros Bestiality Addon 模组 |

放齐后：

```powershell
dotnet build GNH.LocalFixes.csproj -c Release
```

产物 `bin\Release\` 就是可直接部署的模组结构（About + Assemblies + Patches），整体复制到 `RimWorld\Mods\` 即可。
