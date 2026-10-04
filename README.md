# GNH 本地修复补丁（GNH.LocalFixes）

> **📦 下载**：[最新版本（Release）](https://github.com/1579486875/GNH-LocalFixes/releases/latest) —— 下载 zip，解压后放进 `Mods\` 目录即可（内含编译好的 dll，需要 Harmony）。

本机模组组合的修复合集。packageId `gnh.cn.cys.localfixes`。
部署目录名：`GNH-本地修复补丁`（放进 RimWorld 的 `Mods\` 目录）

- 模组版本：**1.3.9**　·　适用版本：RimWorld **1.6**　·　依赖：**Harmony**

## 构建与部署

    dotnet build GNH.LocalFixes.csproj -c Release
    # bin\Release\ 直接就是可部署的模组结构（About + Assemblies + Patches），整体复制到 Mods 目录

`refs\` 下的引用 DLL 不会进入产物（所有 `<Reference>` 都有 `<Private>false</Private>`）；
`EnableDefaultNoneItems=false` 用于防止把 refs 误打包进模组。

## 八项修复

八项互相独立，均为最小侵入，可整体或逐项停用。

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

## ⚠️ 硬性规则（2026-10-02 事故教训，改本工程前必读）

1. **补丁安装必须幂等。** RimWorld 的 `LoadedModManager.CreateModClasses()` 一局之内会**被多次调用**
   （实测：每次打开游戏内模组管理器都会触发）。因此**绝不能在 `Mod` 子类构造函数里做无保护的
   `Harmony.PatchAll()`** —— Harmony 补丁是叠加的，每次重入都会再套一层 prefix，
   表现为「越操作越卡」直至卡死崩溃。所有安装点都必须有静态标志守卫：
   `if (installed) return; installed = true;`

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
