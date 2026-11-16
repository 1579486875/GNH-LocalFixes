# -*- coding: utf-8 -*-
"""把 v1.4.61 的条目插入《技术说明.md》顶部（在 v1.4.60 之前）。"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PATH = os.path.join(ROOT, "技术说明.md")

ENTRY = """## v1.4.61（2026-11-16）· 【69】人工种族的「裸体评价」页签不出现

### 【69】RJW 用字符串包含把人工种族当成机械体，页签跟着消失

· **症状**（玩家实测反馈，两轮会话 + 截图 + 对照实验确认）：

  · 原版人类（24 岁）页签栏：`幻化 | 裸体 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康`
  · 某「人工种族」（16 岁，随后在人物编辑器里改成 20 岁并存读档）：
    `幻化 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康` ← **没有「裸体」**
  · 但**机制完全正常**：全裸照常遮体、走光照常掉心情、评语照常生成。
  · **改年龄无效** —— 与年龄无关（这一条很关键，它直接排掉了「未成年」假设）。

· **判定链**（反编译 1.7.5011 的 `Assembly-CSharp` + `RJW.dll` + `NudityMattersMore_opinions.dll`）：

  页签是否出现只看 `ITab_Pawn_NMMOpinions.IsVisible`，它只有三项：

  ```
  return canDoLoving && isHuman && !isEntityOrGhoul;
  ```

  | 项 | 实现 | 对人工种族 |
  |---|---|---|
  | `isHuman` | `pawn.def.race.Humanlike` → `RaceProperties.Humanlike => (int)intelligence >= 2` | 继承 Human ⇒ **true** |
  | `isEntityOrGhoul` | `if (!pawn.IsEntity) return pawn.kindDef == PawnKindDefOf.Ghoul;` | ⇒ **false**，取反后 true |
  | `canDoLoving` | `rjw.xxx.can_do_loving(pawn)` | ← **就是这里** |

  `can_do_loving` 的**第一行**：

  ```csharp
  if (is_mechanoid(pawn)) return false;
  ```

  而 `rjw.xxx.is_mechanoid` 的判据是一串**字符串包含**：

  ```csharp
  if (AndroidsCompatibility.IsAndroid(pawn)) return false;          // ← 唯一的豁免
  return pawn.RaceProps.IsMechanoid
      || pawn.RaceProps.FleshType == FleshTypeDefOf.Mechanoid
      || pawn.RaceProps.FleshType.corpseCategory.ToString().Contains("CorpsesMechanoid")
      || pawn.RaceProps.FleshType.defName.Contains("GR_Mechanoid")
      || pawn.RaceProps.FleshType.defName.Contains("MechanisedInfantry")
      || pawn.RaceProps.FleshType.defName.Contains("Android");      // ← 名字带 Android 就算
  ```

  豁免只认一个 mod 的扩展类型（`rjw.AndroidsCompatibility` 的静态构造写死了）：

  ```csharp
  typeName = "Androids.SexualizeAndroidRJW";
  ```

· **根因**：**除 `Androids` 那一家之外，任何 `FleshType.defName` 里含 `Android` 的种族
  都会被 RJW 判成机械体** ⇒ `can_do_loving` 第一行返回 false ⇒ 页签消失。

  这一条同时解释了三个现象：与年龄无关（第一行就拦了，压根走不到年龄判断）、
  只对 mod 种族发生（原版人类 `FleshType` 是 `Flesh`）、机制却完全正常
  （NMM 的裸体判定走自己的代码，不经过 `is_mechanoid`）。

· **修法**：给 `ITab_Pawn_NMMOpinions.get_IsVisible` 加 **Postfix**。
  原方法返回 false 之后，只有**同时**满足下面四条才翻成 true：

  1. `pawn != null`；
  2. `pawn.def.race.Humanlike` —— 是人形（真机械体、动物在这一步被排除）；
  3. `!EntityOrGhoul(pawn)` —— 不是 Anomaly 实体、不是 Ghoul；
  4. **`is_mechanoid(pawn) == true`** —— 确认它正是「被判成机械体的那一种」。

  **第 4 条是关键**：它把放宽严格限制在「RJW 把一个人形种族当成机械体」这一种情形。

  | 情形 | 结果 |
  |---|---|
  | 未成年的人形种族 | `is_mechanoid` 为 false → **仍然不显示**（尊重作者原意） |
  | 真正的机械体 | 不是 `Humanlike` → 不动 |
  | Ghoul / Anomaly 实体 | 不动 |
  | 原版人类 | 本来就显示，Postfix 直接跳过，**零影响** |
  | 被 `FleshType` 名字误伤的人工种族 | **显示**（这正是要修的） |

  ⇒ **没有推翻作者的任何一条设计**，只是把「RJW 的误判」从可见性判定里摘出去。

· **顺带查清的一件事（玩家问「机性恋模组有没有影响」）**：

  `ElToro.MAddon`（`ElToro.MAddon`，本地目录 `RJW_玩法_ElToro的机性恋`）**不是**这条问题的成因 ——
  它的 5 个 Harmony patch 一个都不碰 `can_do_loving` / `is_mechanoid` / `IsVisible`，
  它让机械体「能做」靠的是自己那套 `JobDriver_MAddonMCP_*`，**全程绕开 `can_do_loving`**。

  但它**用的是同一个误判**：

  ```csharp
  // ElToro_MAddon.MCPTargetGrantUtility.TryGrant
  if (p != null && !p.Dead && p.Faction == Faction.OfPlayer
      && xxx.is_mechanoid(p)              // ← 同一个 rjw.xxx.is_mechanoid
      && defs.SubcoreBioprinting_RP.IsFinished)
      p.abilities.GainAbility(defs.MCPTarget);   // 「植入生物打印机」
  ```

  ⇒ 那个「人工种族」如果被误判，会**同时**得到两个后果：
  **裸体评价页签消失**，以及 **被授予「植入生物打印机」能力**。

  这给了玩家一个零成本的验证法：**选中那个 pawn，看有没有「植入生物打印机」** ——
  有，就说明 `is_mechanoid` 确实为 true，整条推理闭合。

· **自动让路**：
  ① 没装「裸体评价更多看法」→ 找不到类型，打一行 Message 跳过；
  ② 作者以后自己修了 → `is_mechanoid` 不再是 false 的原因，本项自然什么都不做；
  ③ 没装 RJW 或 RJW 改了这个方法 → 拿不到 `is_mechanoid`，一律**不放宽**（保守值）；
  ④ 玩家关掉本项 → 整项不安装，回到原状。

· **为什么全程用反射**：本补丁包不依赖 NMM，也不依赖 RJW。直接 `using` 那些类型会让
  没装它们的玩家在加载本补丁包时就炸。所以全部走 `AccessTools.TypeByName`，
  拿不到就当「不适用」，一个 `NullReferenceException` 都不往外扔。

· **配套工具**：无（本条是纯 C# 补丁；判定依据来自反编译，未新增扫描脚本）。

"""


def main():
    write = "--write" in sys.argv
    raw = open(PATH, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")

    if "v1.4.61" in text:
        print("v1.4.61 已存在，跳过")
        return 0

    nl = "\r\n" if "\r\n" in text else "\n"
    anchor = "## v1.4.60（2026-11-16）"
    pos = text.find(anchor)
    if pos < 0:
        print("找不到锚点 %s" % anchor)
        return 1

    block = ENTRY.replace("\n", nl)
    new_text = text[:pos] + block + text[pos:]
    print("可插入：%d 字符 → 新文件 %d 字符" % (len(block), len(new_text)))

    if write:
        open(PATH, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8"))
        back = open(PATH, "rb").read().decode("utf-8-sig")
        heads = re.findall(r"^## (v[\d.]+)", back, re.M)
        assert heads and heads[0] == "v1.4.61", "首个版本小节应为 v1.4.61，实际 %r" % (heads[:2],)
        assert back.count("v1.4.60") == text.count("v1.4.60"), "旧条目被破坏"
        print("已写入。复核：首个版本小节 = %s，v1.4.60 出现次数不变（%d）"
              % (heads[0], back.count("v1.4.60")))
    else:
        print("[dry] 未写入")
    return 0


if __name__ == "__main__":
    sys.exit(main())
