# 【71】RJW `RacePartDef.hediffName` 被汉化包误译 —— 诊断与修复难点

**日期：2026-11-27**
**状态**：**已完整定位根因与修复难点，但运行时修复连续 6 版未成功，已回退代码。下一轮可据此直接继续。**

---

## 1. 现象

游戏日志 845 个 ERROR 里，**700 个（83%）是同一条模式**：

```
[RJW] Could not find a HediffDef named 肛门 referenced by RacePartDef named Anus.        × 200
[RJW] Could not find a HediffDef named 乳房 referenced by RacePartDef named Breasts.     × 192
[RJW] Could not find a HediffDef named 阴道 referenced by RacePartDef named Vagina.      × 104
[RJW] Could not find a HediffDef named 阴茎 referenced by RacePartDef named Penis.       × 84
[RJW] Could not find a HediffDef named 机械阴道 referenced by RacePartDef named MechVagina × 23
[RJW] Could not find a HediffDef named 机械植入器 referenced by RacePartDef named MechImplanter × 22
…（共 65 个 def 被污染）
```

---

## 2. 根因（已确证，可直接复验）

### 2.1 RJW 侧：`hediffName` 是 **defName 引用**

反编译 `rjw.RacePartDef`（`RJW_核心_本体/1.7/Assemblies/RJW.dll`，MVID `83b5bb80…`）：

```csharp
public string hediffName;          // ← HediffDef 的 defName 引用，不是给人看的文本

public bool TryGetHediffDef(out HediffDef hediffDef)
{
    hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(hediffName);
    if (hediffDef == null)
    {
        ModLog.Error("Could not find a HediffDef named " + hediffName
                   + " referenced by RacePartDef named " + base.defName + ".");
        return false;
    }
    return true;
}
```

RJW 本体 `1.7/Defs/RaceSupport/RacePartDefs.xml` 的写法：

```xml
<rjw.RacePartDef>
    <defName>Anus</defName>
    <hediffName>Anus</hediffName>     ← 作者原值就是 defName
</rjw.RacePartDef>
```

**实测统计**：该文件 61 个 RacePartDef 里，**60 个 `hediffName` 与 `defName` 完全相同，0 个不同**。

### 2.2 汉化包侧：把它当普通文本翻了

```
Mods/!Translation_AI_Pack/Languages/ChineseSimplified/DefInjected/rjw.RacePartDef/
    rim_job_world_AutoTranslated.xml          60 条污染
    castle_tentaclecults_AutoTranslated.xml    5 条污染
```

内容形如：

```xml
<Anus.hediffName>肛门</Anus.hediffName>
<Breasts.hediffName>乳房</Breasts.hediffName>
```

**违反「边缘世界本地化翻译提示词」第 5 条**：defName 引用必须原样保留，绝不能译。

译了之后 `GetNamedSilentFail("肛门")` 必然返回 null → 每个引用它的种族各报一次 → 700 条。

### 2.3 不是 RJW 的 bug

报错里 `referenced by RacePartDef named Anus` 仍是英文 —— 说明被污染的只有 `hediffName`，`defName` 没事。

---

## 3. 修复方案（已设计好，但实现踩了 6 个坑）

**目标**：游戏启动时把查不到的 `hediffName` 还原成 `defName`。

**为什么不改汉化包的文件**：
- 那是别人的模组，模板 §0 明确「绝不去改别人模组目录里的东西」
- 对方一更新就被覆盖
- 我们运行时修，换任何汉化包都管用

**安全性**：改之前先验证 `defName` 能查到真实 HediffDef，验证不过就保持原样（宁可留一条红字，也不静默改可能合法的数据 —— 模板 §3.1 反向要求）。

`ResolveReferences()` 只处理 `fluidType`、**不缓存 `hediffName`**，所以运行时改字段立即生效。

---

## 4. 六个坑（这是本轮最有价值的部分）

连续 6 版都没成功，每次都要重启游戏 40-70 分钟验证。**把每个坑记下来，下一轮别再踩。**

### 坑 1：`DefDatabase<Def>.AllDefsListForReading` 永远是空表

```csharp
foreach (Def d in DefDatabase<Def>.AllDefsListForReading)   // ← 空
```

**这正是模板附录 A 第 33 条**。`DefDatabase<Def>`（**基类**那个泛型实例）的 `defsList` 从不被填充，游戏只往 `DefDatabase<TraitDef>` 这类**具体类型**里加。

日志：`【71】没找到任何 RacePartDef，本项跳过。`

### 坑 2：自己反射造 `DefDatabase<rjw.RacePartDef>` 也是空

```csharp
Type dbType = typeof(DefDatabase<>).MakeGenericType(racePartDefType);
PropertyInfo p = dbType.GetProperty("AllDefsListForReading", ...);
```

**仍然空**。日志（12:08:06）：`DefDatabase<RacePartDef> 里没有任何条目`。

### 坑 3：`GenDefDatabase.GetAllDefsInDatabaseForDef` 也是空

反编译它的实现：

```csharp
return ((IEnumerable)GenGeneric.GetStaticPropertyOnGenericType(
    typeof(DefDatabase<>), defType, "AllDefs")).Cast<Def>();
```

走的还是 `DefDatabase<T>`，**同样没被填充**。

### 坑 4：真正的数据源是 `ModContentPack.AllDefs`（但时机不对）

反编译 `DefDatabase<T>.AddAllInMods()`：

```csharp
foreach (T t in GenDefDatabase.DefsToGoInDatabase<T>(mod)) AddDef(t, sourceName);
// 而 DefsToGoInDatabase<T>(mod) => mod.AllDefs.OfType<T>();
```

**所以 `mod.AllDefs` 才是源数据。** 改用它 —— 但**在 `Mod` 构造函数里读，仍然空**。

### 坑 5：`Mod` 构造函数跑在 `AddAllInMods` **之前**

反编译 `Verse.PlayDataLoader.DoPlayLoad`：

```csharp
LoadedModManager.LoadAllActiveMods();          // ① 跑 Mod 构造函数
…
Parallel.ForEach(typeof(Def).AllSubclasses(), defType =>
    GenGeneric.InvokeStaticMethodOnGenericType(
        typeof(DefDatabase<>), defType, "AddAllInMods"));   // ② 才填充 DefDatabase
…
LongEventHandler.ExecuteWhenFinished(delegate {
    StaticConstructorOnStartupUtility.CallAll();            // ③ 最晚，def 早已就绪
});
```

**`TryInstall` 在 `LocalFixesMod` 构造函数里调 → 属于阶段 ① → 读不到任何 def。**

### 坑 6（**最隐蔽**）：`TryInstall` 引用那个类 → CLR 提前触发静态构造

改成 `[StaticConstructorOnStartup]` 后仍然失败，日志显示**两次「读不到」只差 1 毫秒**。

原因：

```csharp
// LocalFixesMod 构造函数里
TryInstall("rjwRacePartHediffNameFix", "...", RjwRacePartHediffNameFix.Install);
//                                              ↑ 一旦引用这个类，CLR 就在这里
//                                                触发它的静态构造（阶段 ①）
```

于是静态构造在**阶段 ①** 就跑了一次（读不到 def，`done = true`）；
之后 `CallAll()`（阶段 ③）再触发时，**CLR 保证静态构造只跑一次，不会再跑**。

**这就是为什么改成 `[StaticConstructorOnStartup]` 也没用。**

---

## 5. 下一轮的正确做法

**核心约束**：既要让闸门「开关接线」看到接线，又**不能让 `LocalFixesMod` 引用到 `RjwRacePartHediffNameFix` 这个类**。

**方案**：`TryInstall` 传一个**空 lambda**：

```csharp
// LocalFixesMod 里
TryInstall("rjwRacePartHediffNameFix", "RjwRacePartHediffNameFix", EmptyInstall);
private static void EmptyInstall() { }
```

```csharp
// RjwRacePartHediffNameFix.cs
[StaticConstructorOnStartup]
public static class RjwRacePartHediffNameFix
{
    static RjwRacePartHediffNameFix()
    {
        Install();
        // 自己记账（因为不经过 TryInstall）
        if (LocalFixesMod.IsFixEnabled("rjwRacePartHediffNameFix"))
            FixReport.MarkLoaded("rjwRacePartHediffNameFix");
    }
    …
}
```

**再加一条**：Entry 的 `patchClassName` 要传 `null`（缺省）—— 因为它不是 Harmony 补丁类，闸门会按 `PatchClass` 方式去查、查不到会报错。

**这两条是第 7 版的做法，但因为回退前的脚本锚点没匹配上、改动没落地，尚未验证。**

**下一轮验证要点**：看日志里是否出现

```
【71】已把 N 个 RacePartDef 的 hediffName 还原成 defName（…）
```

并且重启后 `Could not find a HediffDef named` 的计数应从 **700 降到 0**。

---

## 5b. 第 7 版实施记录（2026-11-27 下午，代码已落地）

按 §5 的方案重建，针对六个坑逐个设防：

| 坑 | 防护 |
|---|---|
| 1 `DefDatabase<Def>` 空表 | 不用它 |
| 2 自己反射拿错实例 | 不用手写反射 |
| 3 `GetAllDefsInDatabaseForDef` 空 | 只当**路径 A**，空了走 B |
| 4 `AllDefs` 是真正的数据源 | 当**路径 B** 兜底 |
| 5 `Mod` 构造函数早于 `AddAllInMods` | 扫描移到 `[StaticConstructorOnStartup]` |
| 6 `TryInstall` 引用类 → 提前触发静态构造 | 传 `EmptyInstall` 空方法 |

**代码结构**：

```
RjwRacePartHediffNameFix  [StaticConstructorOnStartup]
  static ctor
    → Install()
        → CollectRacePartDefs(type)
            路径 A: GenDefDatabase.GetAllDefsInDatabaseForDef(type)
            路径 B: LoadedModManager.RunningModsListForReading 的 AllDefs
        → 逐项：hediffName 查不到 HediffDef → 改回 defName（先验证 defName 能查到）
    → FixReport.MarkLoaded("rjwRacePartHediffNameFix")
```

```
LocalFixesMod ctor
  TryInstall("rjwRacePartHediffNameFix", "RjwRacePartHediffNameFix", EmptyInstall);
  private static void EmptyInstall() { }        ← 不引用那个类
```

**反编译验证（关键）**：

| 检查 | 结果 |
|---|---|
| `RjwRacePartHediffNameFix` 带 `[StaticConstructorOnStartup]` | ✅ 在（`search_attributes` 查到） |
| `RjwRacePartHediffNameFix` 在程序集里的**引用数** | **0 处**（`find_usages`）→ 静态构造只会在 `CallAll()` 触发一次 |

**编译与闸门**：0 警告 0 错误；闸门 **26/26**；DLL 三处同 SHA256（`9ad697fe6f25`）。

**版本与文档同步**：1.4.65 → **1.4.66**；开关 77 → **78**；About 速览 70 → **71 条**；
README 全部数字、工坊简介三语（7,506 / 7,498 / 7,424 字节）、三语语言键（174 → 176）。

**✅ 实机验证通过（2026-11-27 13:53:42）**：

```
INFO [GNH LocalFixes] 【71】已把 59 个 RacePartDef 的 hediffName 还原成 defName
     （汉化包把 defName 引用当文本翻了，导致 RJW 刷「Could not find a HediffDef named 中文…」）。
     样例：Anus（原值「肛门」）；ArchotechAnus（原值「超凡仿生肛门」）；
           ArchotechBreasts（原值「超凡仿生乳房」）；ArchotechPenis（原值「超凡仿生阴茎」）；
           ArchotechVagina（原值「超凡仿生阴道」）；BionicAnus（原值「仿生肛门」）

WARN [GNH LocalFixes] 【71】另有 1 个 RacePartDef 的 hediffName 查不到对应 HediffDef，
     且用 defName 也查不到 —— 那是那个 RacePartDef 本身指向了不存在的 HediffDef，
     不是译名问题，按「拿不准不动」原则保持原样。
```

**效果对比（同一份 1.7 日志口径）**：

| 指标 | 修复前 | 修复后 | 变化 |
|---|---|---|---|
| **ERROR 总数** | **846** | **144** | **−702（−83%）** |
| **`Could not find a HediffDef named` 红字** | **700** | **0** | **全部消除** |
| 其中被还原的 RacePartDef | — | **59 个** | — |
| 按原则保留原样的 | — | **1 个** | 它本身指向不存在的 HediffDef |
| 本补丁自身 ERROR | 0 | **0** | — |

**剩下 144 个 ERROR 全部是第三方模组的**（与修复前那 145 个一一对应）：
`SpaceTorpedoes` 跨引用 ×3、`GodHandMod` 彩蛋 ×1、外星人SA / Ratkin Gene Expand / Raven Race
的 Patch 失败各 ×2、`PurePatcher` / `NiraRjwPatch` / `GenevaChecklistAchievements` /
`Teacher.UAP` 各 ×1、`Misconfigured PrefixCapturedVar` ×2 等。

**⚠ 注意一个观察口径的坑**：用 `grep 'Could not find a HediffDef named'` 全日志搜，
会匹配到**本项自己的 INFO 日志**（因为那句日志里引用了报错原文），
看起来像「还剩 1 条」。必须**限定 ERROR 级别**再数：

```bash
grep -E '^[0-9-]+ [0-9:.]+ +ERROR ' Player.log | grep -c 'Could not find a HediffDef named'
# → 0
```

**另一个旁证**：【71】的日志出现在 13:53:42（启动后约 44 分钟），
而不是像前几版那样在启动 1 分钟就出现 —— 这正说明这次**没有构造函数提前触发静态构造**，
防护生效了。

---

## 6. 上一轮为什么回退（历史记录）

- 6 次重启（每次 40-70 分钟）都没成功，继续盲试的时间成本已经高于收益
- 回退到已验证全绿的 `e6b4a11`（1.4.65 / 77 开关 / 闸门 26/26），保证交付物是干净的
- **根因、六个坑、第七版方案都已写在本文档里**，下一轮可以直接做，不用重新查

---

## 7. 顺带确认的两件事（与本文档主题无关，但同批查出来的）

### 7.1 那条「我修复了大部分的报错」不是 bug

```
ERROR [Verse] 我修复了大部分的报错，但是我保留了一部分，我觉得保留了一部分红字，才知道你玩的是RIMWORLD
当然，你可以在设置中关闭这条报错
          ——Pa
    at GodHandMod.FlavorErrorDisplayer+<>c.<.cctor>b__0_0()
```

来自 `GodHandMod` 的「风味错误显示」（`FlavorErrorDisplayer`），**是故意留的彩蛋**，设置里能关。

### 7.2 其余 145 个 ERROR 的归属

| 数量 | 来源 | 性质 |
|---|---|---|
| 3 | `SpaceTorpedoes` ThingCategoryDef 跨引用失败 | 某模组 def 引用 |
| 6 | 外星人SA / Ratkin Gene Expand / Raven Race 的 Patch 失败 | 那三个模组自己的 xpath 没命中 |
| 2 | `NiraRjwPatch` / `GenevaChecklistAchievements` | `ReflectionTypeLoadException` |
| 1 | `Harvest_Organs_Post_Mortem` | 缺 `Autopsy.dll`（附录 B 的 P0 级，但是第三方的） |
| 1 | `PurePatcher` | `BadImageFormatException` |
| 1 | `AutoTranslationCore` | 翻译加载失败 |
| 1 | `Teacher.UAP` | 重复 `InteractionDef` |
| 2 | `Misconfigured PrefixCapturedVar` | 某模组 XML 写法 |
| 其余 | `animal` RaceTagDef / `swimmers` ThingDef | 跨引用失败 |

**我们的补丁：0 个 ERROR。**
