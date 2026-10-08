# GNH 本地修复补丁（GNH.LocalFixes）

> **📦 下载**：[最新版本（Release）](https://github.com/1579486875/GNH-LocalFixes/releases/latest) —— 下载 zip，解压后放进 `Mods\` 目录即可（内含编译好的 dll，需要 Harmony）。

本机模组组合的修复合集。packageId `gnh.cn.cys.localfixes`。
部署目录名：`GNH-本地修复补丁`（放进 RimWorld 的 `Mods\` 目录）

- 模组版本：**1.3.25**　·　适用版本：RimWorld **1.6**　·　依赖：**Harmony**

## 构建与部署

    dotnet build GNH.LocalFixes.csproj -c Release

构建完 `bin\Release\` 就是一个**完整可部署的模组目录**：

    bin\Release\
      About\About.xml                ← 模组元数据
      Assemblies\GNH.LocalFixes.dll  ← RimWorld 只从这个目录加载程序集
      Patches\*.xml                  ← 纯 XML 补丁
      README.md
      GNH.LocalFixes.dll             ← 编译器在根目录留的那一份，RimWorld 不读它，可无视

整体复制到 `RimWorld\Mods\GNH-本地修复补丁\` 即可。也可以直接用 `tools\deploy.ps1`
（备份旧文件后复制，并会先检查游戏是否正在运行）。

> **`Assemblies\` 那一层不能少。** RimWorld 不读模组根目录下的 dll ——
> 少了这层目录，模组会在列表里正常出现、进游戏也不报错，但补丁一个都不生效，
> 最容易被误判成「代码写错了」。v1.3.15 之前本工程的构建产物正是这个样子
> （dll 落在 `bin\Release\` 根下），现已由 csproj 里的 `GNHStageAssembliesFolder`
> 这个 Target 补齐。

`refs\` 下的引用 DLL 不会进入产物（所有 `<Reference>` 都有 `<Private>false</Private>`）；
`EnableDefaultNoneItems=false` 用于防止把 refs 误打包进模组。

> 从 1.3.15 起 `refs\` 里多了两个 Unity 模块（`UnityEngine.IMGUIModule.dll`、
> `UnityEngine.TextRenderingModule.dll`）—— 模组设置界面用到 `Widgets` 与 `Listing_Standard`，
> 它们的签名里出现 `GUIContent`，少这两个就编不过。`tools\sync-refs.ps1` 已经把它们
> 一起同步，csproj 里也加了「缺了就报错」的前置检查。

## 二十一项修复

二十一项互相独立，均为最小侵入，可整体或逐项停用。

> **本版（v1.3.25，2026-10-08）的重点变更 —— 第 20 项重新定位**
> 
> · **v1.3.24 对第 20 项的诊断是错的，本版已改正。**
>   上一版说「`Allies are Helpful` 的两个静态缓存字段没被初始化、初值是 `null`」，
>   修法是「为 `null` 时填一个空列表」。把 IL 逐条读出来复核之后确认：
>   那个 `.cctor` 有 117 字节 / 29 条指令，**明确把两个字段初始化成了空列表**；
>   而它们唯一的赋值来源（`GetTendTargets` / `GetRescueTargets`）各只有一个 `return`，
>   返回的都是当场 `new` 出来的 `List`，**也永远不会是 `null`**。
>   也就是说：**上一版那个修复从装上那天起就是一个永不生效的空操作。**
> 
> · **真正的原因**：`Postfix` 里 `Job val = __instance.jobs?.curJob;` 之后，
>   守卫写的是 `val?.def != null` —— `curJob` 为 `null`（角色当前没有工作，这在游戏里很常见）时
>   它算出来是 `false`、**不会提前返回**，代码继续往下走，
>   在后文直接读 `val.def`，于是抛空引用。
> 
> · **本版的修法**：用转译器把那个方法体里每一次「读 `Job.def` 字段」
>   换成 `null` 安全的读取。两者在求值栈上完全等价（都是「吃进一个 `Job`、吐出一个 `JobDef`」），
>   区别只在 `job` 为 `null` 时 —— 原来抛异常，现在返回 `null`。
>   另挂一个收尾器兜底。补丁类改名为 `AlliesAreHelpfulCurJobNullFix`。
>   实测：转译器在真实 IL 上匹配到 **5 处**读取并全部改写。
> 
> · 顺手订正了 `LocalFixesMod` 里关于「`GetTypes()` 什么时候会整体失败」的一段注释 ——
>   原文说「卸载 RJW / ElToro 就会让 `GetTypes()` 失败」，用离线宿主实测**并不成立**
>   （对那两个模组的引用全在方法体内部，签名里没有），已改为实测准确的说明。
>   其余 20 项一字未动。
> 
> ---
> 
> **上一版（v1.3.24，2026-10-08）的重点变更**
> 
> · **新增第 20 项 —— 一开殖民地就刷 `NullReferenceException`。**
>   `Allies are Helpful`（盟友来帮忙）的 `PawnTendAndRescuePatch.Postfix`
>   在校验「缓存里有没有目标」时，把本该是 `||` 的地方写成了 `&&`，
>   而它的静态构造函数是空的、两个缓存字段从始至终都是 `null`；
>   于是**每个殖民者每次 tick 都抛一次空引用**。
>   本补丁在它的收尾函数前面垫一道「缓存为空就先塞空表」的保险，
>   **不改动它的任何判断逻辑**，它原本想做的那件事照旧。
> 
> · **新增第 21 项 —— 日志被 `[RJW-Genes] multipreg checks` 刷爆。**
>   RJW 基因核心在开发者模式下把同一句话打印了 **31,415 次**，
>   占掉整份日志的 65%（2.16 MB 里的约 1.4 MB），而且**每行都是一次磁盘写入** ——
>   这既是噪音也是实打实的性能负担。本补丁只拦这一句，其余日志一字不动；
>   并且**第一次拦截时会打印一句说明**，不静默吞掉。
> 
> ---
> 
> **上一版（v1.3.23，2026-10-07）的重点变更**
> 
> · **第 3 项 VFEE 爵位补丁已重写。** 旧版按 `defName` 定位，命中的是「被 VFE 改成抽象模板的那几个原版 def」；
>   真正生效的 def 没有 `defName`、靠 `ParentName` 继承出来，拿不到旧版写进去的值。
>   加上 `disroom.mashiro`（本机第 1130 位，晚于 VFE 的第 464 位）把这类爵位的 `modExtensions`
>   整体替换成 `<modExtensions Inherit="False" />` —— 而 `Inherit="False"` 的语义是
>   「清空继承来的内容、只留自己写的」，写空元素就等于把 VFE 的扩展整片抹掉。
>   净效果：**帝国所有爵位的层级人物退化成同一个等级**（不崩，但功能倒退）。
>   新版按「实际生效的 def」定位，把 16 个爵位的 `kindForHierarchy` / `iconPath` 逐条补回，
>   并用 .NET 真 XPath 引擎跑通了「VFE → disroom → 本补丁」的完整流水线模拟：**期望值不符 = 0**。
> 
> · **新增第 19 项**（雪兔纹身图标）—— 这一条其实早就修好了，但一直没写进文档，本次补上。
> 
> · **一批代码加固**：所有补丁安装路径上的日志调用改为「日志失败也不许抛」
>   （此前日志子系统一旦故障，异常会从 catch 块漏进 `LocalFixesMod` 的构造函数，
>   把「某个补丁没装上」升级成「Harmony 补丁叠层、越玩越卡」）；
>   `SteamWorkshopHookFix` 在「模组页每帧 × 全部模组」这条路径上去掉了每次反射取值；
>   `MusicManagerFadeoutFix` 在极热路径上先做引用比较再退化到类型判定；
>   `CrossPromotionUnpatch` 的每帧入口补了 try/catch。
> 
> · **文档口径订正**：修掉 9 处与代码不符的注释、删掉 2 个从未使用的 `using`、
>   把 2 处魔数提成具名常量。


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
| 15 | **领袖头衔定好了会自己变回去**：改模因 / 点「随机符号」时，`IdeoFoundation.GenerateLeaderTitle()` 会把 `Ideo.leaderTitleMale` 无条件重新掷骰子；`<nameLocked>` 管不到它 | `src/Patch_IdeoFoundation_KeepLeaderTitle.cs` + `src/GNHLocalFixesSettings.cs` |
| 16 | **同一进程里连续开新局会卡死在殖民者生成**：`NudityMattersMore.CoverBody.UpdateIdeo` 的 tracker 参数被调用方写死传成 `null`，而它自己的静态字典跨局不清空、撞上同号残留后直接空引用（越开越容易炸） | `src/NmmIdeoTrackerNullFix.cs`（由 `src/LocalFixesMod.cs` 安装） |
| 17 | **Onahole 的 mimic 生成器刷「Could not find any RuleDef」**：模组把「1×1 小格子四边各内缩 2 格」得到的空矩形推给 `mimicSpawner` 符号，而 resolver 要求宽高 ≥ 1，于是必然拒绝。警告的字面意思（找不到 RuleDef）是误导的 —— 规则一直都在 | `src/BaseGenMimicSpawnerQuietFix.cs`（由 `src/LocalFixesMod.cs` 安装） |
| 18 | **金鸢尾兰「某不知名的沙皇」生成后变成智人**：`OASFC_BasePawn` 只写了 `race=Ratkin`、没有 `xenotypeSet`，生成时回退成默认的 Baseliner，鼠耳与尾巴全没了 | `Patches/OASFC_TsarXenotypeFix.xml` |
| 19 | **打开「意识形态」页面刷 `Could not load Texture2D` + NRE**：雪兔纹身 `SR_HuoShu_Tattoo_Slot1` 的 `iconPath` 写成大写 `S`，磁盘上却是小写 `s`；RimWorld 查贴图走的是启动时建的内存字典、精确匹配字符串，**不看文件系统**，所以大小写不一致照样 miss | `Patches/SnowRabbitTattooIconFix.xml` |
| 20 | **刷 `Exception ticking ...: NullReferenceException`**：`Allies are Helpful`（盟友来帮忙）的 `PawnTendAndRescuePatch.Postfix` 里，`Job val = __instance.jobs?.curJob;` 之后的守卫写成 `val?.def != null` —— 「角色当前没有工作」（`curJob` 为 `null`）时它算出来是 `false`、**不会提前返回**，代码继续往下走，在后文直接读 `val.def`，于是抛空引用并中断该角色那一次 `TickRare` | `src/AlliesAreHelpfulCurJobNullFix.cs`（由 `src/LocalFixesMod.cs` 安装） |
| 21 | **日志被 `[RJW-Genes] multipreg checks` 刷爆**：开发者模式下这一句打印了 **31,415 次**（占整份日志的 65%、约 1.4 MB），每行还各带一次磁盘写入 | `src/RjwGenesSpamQuietFix.cs`（由 `src/LocalFixesMod.cs` 安装） |

### 第 16 项：同一进程里连续开新局会卡死在殖民者生成（2026-10-06）

**症状**：同一个游戏进程里反复用 Quickstarter 开新局，开到第 8 次左右开始有概率卡死。
殖民者生成连抛 `NullReferenceException`，之后界面不再响应、日志也停了 ——
2026-10-06 实测第 8 次开新局连抛 4 次后彻底卡住（13:19:58 → 13:24:12 之间整整 5 分钟零日志），
玩家只能重开；第 9 次只抛 1 次，被 `PawnGenerator` 自己的重试兜住，地图正常生成。

**原因**（反编译 `NudityMattersMore.dll`，MVID `c7f1a2d25f9246cd94581be1c9ed2025`，
与出错堆栈里的那个完全一致）：

`NudityMattersMore.CoverBody.UpdateIdeo(Pawn pawn, PawnIdeoTracker tracker, int gameTick)`
的第二个参数被它的调用方硬编码传成 `null`：

    // NudityMattersMore.Patch_PawnGenerator_NMM.NMMPawnInitialization（HarmonyPostfix）
    CoverBody.UpdateIdeo(__result, null, 0);        ← 第二个实参写死 null

而方法内部只在「静态字典里还没有这个 thingIDNumber」时才会新建 tracker 并赋值：

    if (!PawnInteractionManager.ideoTrackers.ContainsKey(pawn.thingIDNumber))
    {
        PawnInteractionManager.ideoTrackers[pawn.thingIDNumber] = new PawnIdeoTracker();
        tracker = PawnInteractionManager.ideoTrackers[pawn.thingIDNumber];   ← 只有这条路径会赋值
    }
    ...
    tracker.lastCheckTick = gameTick;        ← 字典里已有记录时，这里就是对 null 解引用

堆栈里的偏移 `[0x00079]` 正是这一句，IL 是 `ldarg.1` / `ldarg.2` / `stfld lastCheckTick`。

关键在 `ideoTrackers` 是个 **`public static` 字段、跨局不清空**，而 RimWorld 每开一局都会
新建 `UniqueIDsManager`、`thingIDNumber` 从 1 重新数。于是新局里刚生成的殖民者会命中上一局
留下的同号记录 → `ContainsKey` 为 true → tracker 仍是 `null` → 崩。
这也解释了「越开越容易炸」：字典是累积的，撞号概率随开新局次数上升。

**修法**：给 `UpdateIdeo` 加前缀，只在「调用方传进来的 tracker 是 `null`，且字典里确实已有
同号记录」这一种组合下，把那条属于上一局的残留记录从字典里删掉 —— 原方法随即改走
「新建 tracker」分支，一切照旧。`CompTick` / `CoverCheck` 这些自己带 tracker 的调用点
原样放行，不受影响。另附一个收尾器，兜住同方法里 `memes` / `precepts` 两条 null 路径的空引用，
只吞 `NullReferenceException`、不静默（每次记 Warning，最多 8 条）。

**为什么走反射**：本补丁包必须能在 NMM 没装 / 被禁用时照常工作。一旦在编译期引用它的类型，
程序集里就会出现对该 dll 的依赖，运行库枚举类型时可能整批失败
（机制见 `LocalFixesMod.LoadAllTypesTolerantly` 的注释）。所以全部走 `AccessTools.TypeByName`，
字典用非泛型 `IDictionary` 操作 —— 既不认识 `PawnIdeoTracker`，也不需要认识。
找不到 NMM 时只打一条 Message 就跳过。

### 第 15 项：领袖头衔「改好了自己变回去」（2026-10-06）

**症状**：在 `.rid` 或文化界面里把领袖头衔定成「主席」，过一阵它自己变成了别的
（例如「游击队区域指挥官」）。改模因、点「随机符号」之后尤其必现。

**根因**（反编译 `RimWorld.IdeoFoundation.GenerateLeaderTitle` 得到）：

头衔存在 `Ideo.leaderTitleMale` / `leaderTitleFemale` 两个字段上。给它们赋值的几乎
只有 `IdeoFoundation.GenerateLeaderTitle()` 这一个方法，而它**从不读旧值，直接覆盖**：

    ideo.leaderTitleMale   = NameGenerator.GenerateName(request, null, false, "r_leaderTitle");
    ideo.leaderTitleFemale = ideo.leaderTitleMale;          // 男女强制同一个

该方法全游戏共 6 个调用点：`IdeoFoundation_Deity.Init`、`IdeoUIUtility.DoName`
（就是「随机符号」按钮）、`IdeoUIUtility.<>c__DisplayClass116_0`、
`Precept_Role.GenerateNameRaw`、`Dialog_ChooseMemes`（改模因）、`Dialog_ReformIdeo`。
最常撞上的是改模因那一条。

**为什么 `<nameLocked>` 救不了它**：那把锁只作用于 `Precept.name`
（`Ideo.RegenerateAllPreceptNames` 里明写 `if (precept.UsesGeneratedName && !precept.nameLocked)`）。
领袖头衔不是 Precept 的字段，是 `Ideo` 自己的两个字符串，完全不经过那把锁 ——
这就是它「怎么锁都锁不住」的原因。

**修法**：在方法最前面拦一道 ——

    头衔是空的 → 放行（新建文化的第一次生成还得靠原方法）
    头衔有内容 → 跳过原方法，谁也别想覆盖

`IdeoFoundation` 是**抽象类**，原版只有 `IdeoFoundation_Deity` 一个具体子类，
且该子类**没有重写**这个方法，所以补基类就覆盖了全部原版路径
（反编译核对 + 运行时反射复核，两条都过了）。

**开关**：模组设置里新增「保护自定义的领袖头衔」，**默认开**，关掉 = 完全恢复原版行为。
想给某个文化换个头衔时：临时关掉它，点一次「随机符号」，再打开。

**验证**（测试工程在 `E:\TAML\_work\gnh-localfixes-test\`）：

| 套件 | 跑法 | 结果 |
| --- | --- | --- |
| 判断逻辑离线测试 | `dotnet run -c Release` | **30 / 30 通过**（含 5 万次随机 fuzz） |
| 补丁运行时验证 | `powershell -ExecutionPolicy Bypass -File verify-patch-runtime.ps1` | **26 / 26 通过** |

运行时验证是在一个独立 PowerShell 进程里**真的**把补丁装进 `Assembly-CSharp`，
再真的调用一次 `GenerateLeaderTitle()` 对照行为：开关开着时头衔原样留下，
关掉之后原方法立刻把它清掉 —— 两个方向都验到了，而不是只看「补丁装上了」。
（该脚本必须保存为 **UTF-8 带 BOM**：PowerShell 5.1 读无 BOM 的 `.ps1` 会按 ANSI
解码，里面的中文注释会让解析器直接报 `Unexpected token`。）

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


## 2026-10-06 全量审计与修正

对补丁包做了一次三路独立审计（XML/Def 引用、构建配置/依赖、源码注释与质量），下面是发现并已修正的问题。

### 功能修正

- **`CcoeReflectionFix` 覆盖不全**：CCOE 有**三个** Harmony 前缀写了同一个有缺陷的表达式
  （`ArchotechVaginaEnhance.Patch_CumOut`、`MenstruationCycleNotSealedA.Patch_BeforeCumOut`、
  `MenstruationCycleNotSealedC.Patch_AfterCumOut`），此前只修了第一个，另外两个照旧刷
  `TargetException`。实测日志（12:49）里本补丁自述「fix is working」的同一秒，
  紧接着就是一条来自 `Patch_BeforeCumOut` 的异常。现已三个全覆盖（逐个目标记录安装状态，
  已装的不重装、没装上的下次重试）。
- **删除 `Patches/MiliraPortableConsoleLayerFix.xml`**：审计证明它要修的问题**不存在** ——
  作者已经在真正生效的那个 `<apparel>` 节点里写了 `<layers>`，而补丁的 `[not(layers)]`
  命中的是同一个 ThingDef 里**另一个**不含 layers 的兄弟节点，等于往重复字段上写值。
  四条证据：磁盘 XML 实际内容、运行时 `apparel.layers` 非空（无补丁时的快照）、
  日志堆栈来自 Character Editor 主菜单而非意识形态页面、它声称修好的那个 NRE 至今仍在。
  （误删可从 `_backup\` 取回。）
- **`LoadAllTypesTolerantly` 的兜底空洞**：原先遇到非 `ReflectionTypeLoadException`
  就返回 null，会让**所有**补丁一个都不装 —— 正是它想避免的「被拖死」。
  已改为退化到「按已知类名逐个取」，单个类坏掉不再拖垮整包。
- **`TechHediffsZeroBudgetFix` 去掉 LINQ**：该判断每个生成的 Pawn 都会跑，
  原先的 `.Any(tag => ...)` 会在循环内产生最多 6 次堆分配（lambda 捕获 + 枚举器装箱），
  已换成零分配的显式循环（新增 `TagListsOverlap`）。

### 注释修正（16 处，多为「代码改过、注释没跟上」）

`NmmIdeoTrackerNullFix`（开新局次数与耗时口径、调用点清单漏 `InfoHelper.IsUncaring`、
字段个数、把 600 tick 门槛误写成「每 tick」）· `LocalFixesMod`（那句与文件头反编译结论
正面矛盾的「每次打开模组管理器都会重新构造」）· `TechHediffsZeroBudgetFix`（两条互相打架的
过期判据）· `VFEEInstrumentSpaceFix`（「两个爵位」实为九个）· `FindModLanguageFix`
（症状归因错误，与同包 `XmlExtensionsFindModFix` 的结论相反）·
`VehicleFrameworkDebugFix`（「改名就注入不进去」说过头）。

### 构建与工具

- **`tools/sync-refs.ps1`**：原先只同步 3 个文件，而 csproj 硬性检查
  `UnityEngine.IMGUIModule.dll`，报错文案还指向这个补不齐的脚本 —— 叠加 `.gitignore`
  忽略 `refs\`，全新克隆会陷入「脚本补不齐 → 编译失败 → 报错又让你跑这个脚本」的死循环。
  已补齐 5 个游戏文件，并为 3 个模组来源文件加了缺失检查与出处提示。实测通过。
- **`tools/deploy.ps1`**：补上目标目录残留文件清理（避免「bin 里删了、部署目录还留着」）、
  退出码区分「没部署」与「部署了但哈希不符」、同秒重复备份的套娃问题、`-Configuration` 传错提示。
- **csproj**：`src\*.cs` → `src\**\*.cs`（子目录不再静默漏编译）；
  `LangVersion` 从 `latest` 钉死为 `9.0`（不再随 SDK 漂移）。

### 审计通过项（未改动）

`refs\` 8 个 DLL 与游戏本体 **SHA256 全部一致**；6 个 XML 补丁的语法 / 编码 / 启停保护 /
部署一致性全部通过；**27 项 def 与类型引用在 1.6 加载集内逐一确认可用**（含 `Rotti` 只在 1.5、
`VFESecurity.CompProperties_LongRangeArtillery` 只在 1.4/1.5 这类「按全版本扫会误判」的项）；
`NmmIdeoTrackerNullFix` 的性能经实测确认可忽略（`UpdateIdeo` 对每个 Pawn 最多每 600 tick 一次）。

### About.xml

`loadAfter` 补上了此前漏掉的 `vanillaexpanded.vfecore` 与 `eltoro.anims`
（前者是本补丁 `VFEPianoFirepitRestore.xml` 改的 Def 的宿主，原先能否正确「补做」
完全取决于 `disroom.mashiro` 恰好也排在后面）。


## 2026-10-07 重做：第 17 项（Onahole mimic 生成器）—— 前一版判断错了，这一版是实证的

**现象**：地图生成神殿/遗迹时刷

```
Could not find any RuleDef for symbol "mimicSpawner" with any resolver that could resolve rect=(...)
```

堆栈 `BaseGen.Resolve` ← `BaseGen.Generate` ← `GenStep_ScatterShrines.ScatterAt`。实测每次开局 **34 条**。

### ⚠ 先把错的说清楚

2026-10-06 那一版的结论是「**RuleDef 没能进入 `DefDatabase`，原因没写进日志，属模组自身问题**」，
并据此写了个「在 `Defs` 下追加同 symbol 的 RuleDef 兜底」的 XML 补丁。

**那个结论是错的，补丁也从原理上不可能生效**，已撤除（存档在 `_scratch\deprecated-patches\`）。
错在两处：

| 当时的判断 | 真相 |
|---|---|
| `CanResolve` 用基类默认实现，「恒为 true」 | 基类字段是 `public IntVec2 minRectSize = IntVec2.One;` —— **默认 `(1,1)`，不是 `(0,0)`**；`CanResolve` 要求宽高都 ≥ 1 |
| 那句警告 = 「规则没注册」 | `BaseGen.Resolve` 里「字典查不到」和「查到了但每个 resolver 都拒绝」**打印的是同一句话**，实际发生的是后者 |

### 真正的根因（运行时实证，非推测）

模组 `RJW_Onahole.Patches.SymbolResolver_Ancient_Patch` 里：

```csharp
resolveParams.rect = rp.rect.ContractedBy(2);      // 无条件四边各内缩 2 格
BaseGen.symbolStack.Push("mimicSpawner", resolveParams);
```

古代神殿会拿大量**只有 1×1 的子区域**去跑 resolver，收缩 2 格后就成了空矩形。
`CellRect.Width/Height` 的 getter 里有 `if (minX > maxX) return 0;`，所以空矩形的宽高是 **0**，
必然过不了 `minRectSize = (1,1)` 这道门 —— 于是 `tmpResolvers` 为空，打印那句措辞误导人的警告。

`verify-mimic-quiet.ps1` 把日志里的两组数字**逐字复现**了（这才是根因被钉死的证据）：

```
[PASS] ContractedBy(2) on it gives EXACTLY the logged text (51,238,47,234)
[PASS] its ContractedBy(2) gives EXACTLY the second logged shape (50,237,49,235)
```

### 处理

新增 `src/BaseGenMimicSpawnerQuietFix.cs`，在 `BaseGen.Resolve` 之前拦一道：
**只拦「symbol 是 `mimicSpawner` 且矩形宽或高小于 1」的请求**。

这种请求原版本来就会拒绝、而且什么都不做，所以跳过它与原版**完全等价**，只是不再刷警告；
矩形正常时一律放行，模组功能（古代神殿里的 mimic 生成）完全不受影响。
不引用 Onahole 模组的任何类型，只比对字符串 —— 模组没装时它永远走不到跳过分支。

### 顺带修掉的两个真 bug（都是本轮验证抓出来的）

| 问题 | 说明 |
|---|---|
| **嵌套类型名写错，补丁静默失效** | `AccessTools.TypeByName("RimWorld.BaseGen.SymbolStack.Element")` **永远返回 null** —— .NET 里嵌套类型的规范名要用 `+`：`SymbolStack+Element`。第一版会因此只打一句「已跳过」就不干活。现在改成 `FindResolveMethod` 从方法签名反取参数类型，一个名字都不用拼 |
| **日志调用能连累判断结果** | 返回 `false` 之前调 `Log.Message`；万一 `Log` 自己抛异常，异常会穿出前缀 —— 而 Harmony 前缀抛异常会让**整个 `BaseGen.Resolve` 失败**，那就不是「少消一条警告」，而是毁掉整张地图的生成。现在日志单独包 `try/catch`，与判断结果彻底解耦 |

### 验证（可复现）

```powershell
cd E:\TAML\_work\gnh-localfixes-test
powershell -ExecutionPolicy Bypass -File verify-mimic-quiet.ps1
```

```
RESULT: 58 checks, 0 failed, 0 skipped
ALL RUNTIME CHECKS PASSED
```

覆盖：`CellRect` 语义实证（含 `FromLimits` 会规范化排序这个反直觉行为）、补丁依赖的
API 可见性、Harmony 真装补丁并读回账本、前缀决策表（该拦 5 种 / 该放行 7 种 /
防御分支 3 种）、端到端对照实验（装补丁→原方法不跑；卸补丁→原方法跑；
装补丁 + 正常矩形→原方法照跑，证明没有过度拦截）。

### ⚠ 两个宿主环境的坑（写这类验证脚本必看）

| 坑 | 现象 | 处置 |
|---|---|---|
| `Verse.Log` 在 PowerShell 宿主里不可用 | `Message/Warning/Error` 全抛 `SecurityException: ECall methods must be packaged into a system module.`（Unity 的 `Debug` 是 ECall） | 这是宿主限制，游戏里正常。脚本里把 `Install()` 的最后一记日志当成预期内异常；端到端改用「**会不会抛异常**」作判据 —— 原方法一跑就必然撞上它，反而成了天然探针 |
| 参数位置的括号表达式不能跨行 | `Write-Host ("..."` 换行接 `+ $x)` → `Missing closing ')'`，而且报错行号会指到别处 | 拼成一行。已做最小复现确认：同一段代码并成一行即通过 |


## 2026-10-07 全量复审（兼容性 / 完整性 / 性能 / 编译 / XML / 翻译 / Def 引用 / 构建配置 / 注释）

对补丁包做了一轮逐维度复审。结论：**除一处注释表述外全部通过，未发现功能缺陷。**

### 复审方法与结论（可复现）

| 维度 | 方法 | 结论 |
|---|---|---|
| 编译 | 清空 `NoWarn` 后 `dotnet build -c Release` 完整重编译 | **0 warning / 0 error** |
| XML | 7 个补丁逐文件解析 + 尖括号/注释标记配对 | 全部配对，解析通过 |
| XML 极端输入 | 用 .NET 原生 XPath 引擎（与 RimWorld 同源）把 20 条 xpath 跑在 4 种极端文档上 | **0 异常**，幂等场景识别正确 |
| Def 引用 | 18 个 `defName` + 3 个 `@Name` 与全库索引（98,499 项）比对 | 全部存在 |
| C# 类型引用 | 在 DLL 里按类型名检索 | 全部命中（`VFESecurity.CompProperties_LongRangeArtillery` 按设计**应当**不存在） |
| 性能 | 扫 tick 钩子 / 热路径 / 分配 / 反射缓存 | **0 个 tick 驱动代码**；3 个高频补丁均有早退 |
| 幂等性 | 检查 9 个安装点的守卫 | 全部达标（`HashSet` + `lock` + 先立标记） |
| 注释 | 16 个文件逐一核对文件头、根因说明、交叉引用 | 16/16 有文件头；密度 33%~79% |
| 翻译 | 检查新增内容是否含玩家可见文本 | 无需 `Languages\`（新增的 `RuleDef` 无 label） |
| 构建配置 | `csproj` / `tools\*.ps1` 结构与 BOM | 规范 |

### 极端场景实测（20 条 xpath × 4 种极端输入）

结果记为「匹配数 / 异常数」：

| 极端输入 | 结果 | 说明 |
|---|---|---|
| 空 `<Defs/>` | 1 / **0** | 仅「往根节点追加 Def」那条命中，符合预期 |
| 全无关 Def（目标全不存在） | 1 / **0** | 其余全部安静跳过 |
| **幂等场景**（`OASFC_BasePawn` 已有 `xenotypeSet`） | 4 / **0** | 内层 `Conditional` 正确走 `match` 分支 → **不会重复添加** |
| 结构畸形（空 `comps` / `modExtensions` / `building`） | 6 / **0** | 空容器被正确地视为「缺该字段」 |

### 性能要点（为什么它不拖慢游戏）

- **没有任何 `Tick()` / `TickRare()` / `TickLong()` 钩子** —— 补丁全部是「加载期一次性」或「事件触发型」。
- 三个挂在**高频方法**上的补丁都做了早退：
  - `VerbProperties.AdjustedRange`（每次瞄准/射击）→ 仅在 `attacker == null` 时介入，正常路径只多一次判空；
  - `ListerThings.Add`（每个对象登记）→ 三层廉价过滤，非建筑直接返回；
  - `UIRoot_Update`（每帧）→ 帧计数节流，完成后每帧只剩一次 bool 判断。
- 反射目标与 `Type` 均已缓存；日志有次数上限（`MaxDetailedLogs`）与去重（`ErrorOnce`）。

### 本次修正（唯 1 处）

`GNH.LocalFixes.csproj` 的 `NoWarn` 注释原文写着「CS0618 … **见下方说明**」，
但下方**并无**该说明（悬空引用，对读者是死路）。已改为直接说明理由，
并补记「2026-10-07 清空 `NoWarn` 实测 0 触发」这一事实。

该修正为**纯注释变更**：重建后 DLL 哈希与修改前**完全一致**，故**无需重新部署**。

### 复审中查清、但**不属于本补丁包**的问题

**`Tried 300 times to generate age for X`**（日志里 7 个模板，含「某不知名的沙皇」）。

机制已定位到 `Verse.PawnGenerator.GenerateRandomAge`：年龄抽样须连过 5 道关卡
（`min/maxGenerationAge` 区间、发育阶段 `AllowedDevelopmentalStages`、
包含/排除年龄区间）。鼠族自带 `ageGenerationCurve = (14,0)(18,50)(23,100)(30,20)(40,0)`、
`lifeExpectancy` 70，因此走的是鼠族自有曲线而非游戏默认曲线。

但按该曲线加权，沙皇的 `[20,25]` 窗口命中率约 **43%**，300 次全失败在概率上不成立；
**确切触发点需在游戏内实测才能定论**。已逐项排除：鼠族 `defName` 冲突
（`3497673755` 未启用）、`AllowedDevelopmentalStages` 默认值（确为 `Adult`）、
`ValidateAndFix` 干预、`lifeStageAges` 继承断裂、曲线首末点校验失败。

**与本补丁包无关**：本包对沙皇只补 `xenotypeSet`，完全不涉及年龄生成。

### 第二轮复审（同日，换角度复查：运行时与顺序）

第一轮偏重「文件级」检查，第二轮专查静态检查看不见的两面 —— **运行时真实行为**与**加载顺序**。

#### ★ 修正：`loadAfter` 漏了脚踩花糕（真实隐患，已修）

`Patches/OASFC_TsarXenotypeFix.xml` 用 `PatchOperationConditional` 的 xpath 去查
`Defs/PawnKindDef[@Name="OASFC_BasePawn"]`。**xpath 问的是「这个 Def 现在有没有」，
不是「那个模组装了没」**，所以补丁**必须**排在 `OASFC_BasePawn` 的定义者
（`ww.oberoniaaurea.steppedflowercake`，工坊 3379736801）**之后**。

而原来的 `loadAfter` **没有**这一条。实测当时位次：本包 **1133**、脚踩花糕 **561** ——
**碰巧**在后，所以功能是好的；但一旦重排加载顺序（RimCrow 自动排序、手动拖动），
本包可能被挪到前面，此时 xpath 匹配不到 → 补丁**静默不生效**、**日志里一条线索都没有**。

**另一个容易搞混的点**：该补丁的 `FindMod` 守卫写的是
「`[OA]Ratkin Faction: Oberonia aurea`」（金鸢尾兰帝国，位次 348），因为补丁用到的
`Ratkin_OA` 异种人定义在那边；**但要改的 Def 在脚踩花糕里**。
**守卫通过 ≠ 目标已加载**，两者是两件事。

已补入 `loadAfter` 并写好注释。这与 2026-10-06 漏掉 `vanillaexpanded.vfecore`
（当时注释写着「属于碰运气」）是**同一类问题：新增补丁时忘记配顺序**。

#### 复核通过项

| 项目 | 方法 | 结论 |
|---|---|---|
| **`refs\` 快照是否过期** | 8 个 DLL 与实际游戏/模组文件逐一比 SHA256 | **全部一致**（`Assembly-CSharp` 15777280 B；`0Harmony` 2.4.1.0，位于 `2009463077\Current\Assemblies\`；`ElToro_BAddon` 与 `RJW` 均为 1.6 版） |
| **运行时是否真的装上** | 从两份日志提取 `[GNH LocalFixes]` | **14 个安装点全部成功、0 失败** |
| **加载顺序** | 7 个补丁的目标 Def × 全库定义者 × `loadAfter` | 除上述 1 处外全部齐备 |
| **翻译策略** | 查设置界面文本与其注释 | **有意写死中文**，理由已注明（语言包文件夹名带括号，走 `Languages\` 会静默失效） |
| **测试套件** | 跑 `_work\gnh-localfixes-test\` | **单元 30/30、运行时 26/26 全通过** |

**补丁在实机上真正拦下的问题**（证明不是「装了但没生效」）：

- `已兜住 PawnTechHediffsGenerator 的「总权重 0」空引用（Empire_Royal_Yeoman / Empire_Royal_Stellarch）`
- `拦下一个「标签写错」的乐器 … 否则游戏会每帧抛 InvalidCastException`
- `拦下「挂着配方但不是工作台」的对象`，累计 **11 个**
- `CCOE CumOut fix is working: swallowed a TargetException`

日志节流（`MaxDetailedLogs`）与去重（`ErrorOnce`）均已在实机验证生效。

**测试套件内容**（`_work\gnh-localfixes-test\`，10 个文件：3 个原有套件 + 7 个本次新增的审计／基准脚本）：

- `LeaderTitlePolicyTests.csproj` + `Program.cs`：纯逻辑单测，含 NUL / 零宽空格 U+200B /
  BOM U+FEFF / 代理对残片 / 10000 字符长标题 等边界输入，外加 **50000 次随机 fuzz**
  与纯函数性验证。
- `verify-patch-runtime.ps1`（321 行）：**真的**加载 Unity + `Assembly-CSharp` + 刚编出的 DLL，
  **真的**用 Harmony 装上补丁，核对 Harmony 记录的补丁目标与优先级；再造空白对象
  **真的调用** `GenerateLeaderTitle()`，验证「有头衔 → 拦住」与「关开关 → 放行」
  **双向**行为。进程退出即失效，不写文件、不碰存档。

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
- **v1.3.15 期间踩到的两个坑**（都不在运行逻辑里，但都会让人白折腾半天）：
  1. **`HarmonyLib.Priority` 的数值方向很容易记反。** 实测真值（从 `refs\0Harmony.dll`
     反射读出）是**数值越大越先执行**：
     `First=800, VeryHigh=700, High=600, HigherThanNormal=500, Normal=400,
     LowerThanNormal=300, Low=200, VeryLow=100, Last=0`。
     `Low` 是「靠后」而不是「靠前」—— 写之前先确认，别凭印象。
  2. **本仓库的 `.ps1` 脚本必须存成「UTF-8 带 BOM」。** Windows PowerShell 5.1 读无 BOM 的
     `.ps1` 会用系统 ANSI 代码页解码，脚本里的中文注释会变成乱码，甚至让解析器报出
     `Unexpected token '}'` 这种指向完全不相关行号的错（`verify-patch-runtime.ps1`
     就是这么先踩了一次）。`.cs` 文件不受影响：Roslyn 对无 BOM 的源文件按 UTF-8 解码，
     本工程所有 `.cs` 一直都是无 BOM 的。
  3. **部署备份绝不能留在 `Mods\` 目录里。** RimWorld 与 RimCrow 的扫法都是
     「`Mods\` 下每个子目录，只要有 `About\About.xml` 就算一个模组」。
     `tools\deploy.ps1` 的第一版把旧版本备份成
     `Mods\GNH-本地修复补丁.backup-<时间戳>\` —— 于是凭空多出一个**同 packageId 的
     第二个副本**，RimCrow 的「处理重复模组」界面立刻报冲突（一份 1.3.15 已启用、
     一份 1.3.14 未启用），还得手工去清。现已改为备份到工程目录下的 `_backup\`，
     并加进 `.gitignore`。

---

## 关于本仓库

**许可**：[MIT](LICENSE)

**编译需自备 `refs\`**：本工程的 `<Reference>` 指向 `refs\` 下的程序集 —— 那些是游戏本体与第三方模组的 DLL，
版权不属于本项目，因此不纳入仓库。编译前请自行准备：

| 需要的 DLL | 来源 |
| --- | --- |
| `Assembly-CSharp.dll`、`UnityEngine.dll`、`UnityEngine.CoreModule.dll`、`UnityEngine.IMGUIModule.dll`、`UnityEngine.TextRenderingModule.dll` | RimWorld 安装目录 `RimWorldWin64_Data\Managed\` |
| `0Harmony.dll` | [Harmony 模组](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) 的 `Assemblies\` |
| `RJW.dll` | RimJobWorld 模组 |
| `ElToro_BAddon.dll` | ElToros Bestiality Addon 模组 |

放齐后：

```powershell
dotnet build GNH.LocalFixes.csproj -c Release
```

产物 `bin\Release\` 就是可直接部署的模组结构（About + Assemblies + Patches），整体复制到 `RimWorld\Mods\` 即可。
