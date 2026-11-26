# WorldEdit 2.0（GNH 1.7 修复版）—— 已停用归档

停用日期：2026-11-19
原因：**原作者已发布官方 1.7 版，且比本修复版更完整。**

## 一句话

作者的 1.7 版已经包含我们大部分修复，而且补上了我们没做的一处（Unity 2022 鼠标适配）。
本修复版不再需要，留在 `_backup` 备查。

## 当初为什么要做这个修复版

工坊原版（3590928058）最后更新停在 2025-10-23，只声明到 1.6。
1.7 下会在启动时报 `ArgumentException` 并中断 Mod 构造函数，接着 `World.FinalizeInit` 的补丁空引用，
直接导致「生成地图失败」弹回主菜单。

我们修了三处（**其余代码未动**）：

| # | 问题 | 修法 |
|---|---|---|
| ① | `WE_MainButtonsRoot_MainButtonsOnGUI` 的 Prefix 把 `__instance` 声明成了 `ScenPart_PlayerFaction`，而目标方法属于 `MainButtonsRoot` | 删掉该参数（原方法体里没用它） |
| ② | `Page_SelectScenario.BeginScenarioConfiguration` 是 static 方法，补丁却声明了 `__instance` | 去掉 `__instance` |
| ③ | `WorldEdit` 构造函数每次实例化都无条件 `Harmony.PatchAll` | 加幂等守卫 |

## 作者的 1.7 版（2026-11-18 10:35 更新）对比

| 项目 | 本修复版 v2.0-fix2 | 作者版 1.7 | 结论 |
|---|---|---|---|
| **鼠标操作** | `Input.GetKey(KeyCode.Mouse1)` | **新增 `InputUtil` → `Input.GetMouseButton()`** | **作者更好** |
| ① `WE_MainButtonsRoot` 的 `__instance` | 已删 | **也删了**（`Boolean Prefix()`） | 平 |
| ② `Page_SelectScenario` 的 `__instance` | 已删 | 仍保留 | 实测无害（见下） |
| ③ 构造函数 `PatchAll` 幂等 | 有守卫 | 无条件 | 正常情况不触发 |
| 其他 1.7 适配 | 只动那 3 处 | **37 个类型有改动** | **作者更好** |

### 关于 ② 的实测结论

作者版至今保留：

```csharp
private static void Postfix(Page_SelectScenario __instance, ref Scenario scen)
{
    GameComponent_WorldEditTemplate.SelectedScenario = scen;   // __instance 根本没用到
}
```

而 `Page_SelectScenario.BeginScenarioConfiguration` 在 1.7 里确认是 `static`（反编译 `rw17` 得到 `isStatic = true`）。

**实测（2026-11-18）**：把本修复版改名让工坊版生效，跑了一次 1.7 启动：

| 检查项 | 结果 |
|---|---|
| `Error while instantiating a mod` | **0** |
| `ArgumentException` / `HarmonyException` | **0** |
| 8 条 ERROR 的归属 | 全是第三方的，与本修复版在时**完全一致** |

**结论**：Harmony 1.7 遇到「static 方法上声明 `__instance`」只是传 null，不抛异常。
因为那个参数本来就没用到，所以**无害**。

> ⚠ 这条推翻了本修复版 `About.xml` 里原来的说法。那句「1.7 换成 CoreCLR 后校验并拒绝」
> 只适用于 ①（`__instance` 类型不匹配），不适用于 ②。

## 作者的 `InputUtil` 为什么重要

Unity 2022 下 `Input.GetKey(KeyCode.MouseN)` 不响应，必须走 `Input.GetMouseButton(n)`。
本修复版的 `TileEditorWindow.WindowUpdate` 反编译出来是：

```
ldc.i4 324                        ← KeyCode.Mouse1
call UnityEngine.Input.GetKey     ← 不工作
```

作者版改成了：

```csharp
public static bool GetKey(KeyCode key) =>
    IsMouse(key) ? Input.GetMouseButton(Button(key)) : Input.GetKey(key);
// IsMouse: key >= Mouse0(323) && key <= Mouse6(329)
// Button:  key - 323
```

**这是本修复版漏掉的一处，也是决定换用作者版的主要原因。**

## 汉化分支还在，不用动

`Mods/FunkyShit.Mods.WorldEdit.zh.hc (GNH本地分支)` —— packageId `FunkyShit.Mods.WorldEdit.zh.hc`，
`loadAfter: FunkyShit.Mods.WorldEdit.alpha`。

键覆盖核对（2026-11-19）：汉化 214 个 Keyed 键，作者版 English 220 个，差的 6 个是
`LanguageInfo` / `friendlyNameEnglish` / `languageWorkerClass` 这类**语言元数据字段**，不是翻译键。

**所以汉化 100% 覆盖，不需要更新。**

## 怎么回退

万一作者版出了问题，把本目录移回 `Mods/` 并恢复 About：

```powershell
# 1. 移回
Move-Item "E:\TAML\_backup\WorldEdit-2.0-GNH修复版-已停用" `
          "D:\steam\steamapps\common\RimWorld\Mods\WorldEdit 2.0（GNH 1.7 修复版）"

# 2. 恢复 About（当前是 .disabled）
Rename-Item "...\About\About.xml.disabled" "About.xml"

# 3. 同时把工坊版（3590928058）取消订阅或禁用，两者 packageId 相同
```

## 目录内容

```
About/
  About.xml.disabled        ← 原 About，改名后 RimWorld 不会扫描到（2026-11-04 就已改名）
  Preview.png
1.7/Assemblies/
  WorldEdit 2.0.dll         ← 166,400 B，sha256 b11398298a64e9f3…，2026-10-28
Languages/
LoadFolders.xml
```

> 注：`About.xml` 早在 **2026-11-04** 就被改名成 `.disabled`，比本次停用早半个月。
> 这也解释了 2026-11-18 那次实测为什么直接测到了工坊版 —— 这个目录当时就已经不生效了。
