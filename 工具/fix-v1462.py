# -*- coding: utf-8 -*-
"""把《技术说明.md》里 v1.4.61 那一段（方向错误的初版）整体替换为 v1.4.62 的正确内容。

初版把原因判成「RJW 的 is_mechanoid 把人工种族当机械体」，后经 13 个 Aya 种族实测推翻。
正确原因是这些种族自己写死 inspectorTabs、不继承 BasePawn。
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PATH = os.path.join(ROOT, "技术说明.md")

NEW = """## v1.4.62（2026-11-16）· 【69】人工种族的「裸体评价」页签不出现

### 【69】那些种族根本不继承 `BasePawn`，所以拿不到挂在它上面的页签

· **症状**（玩家实测，两轮会话 + 截图 + 改年龄对照）：

  · 原版人类（24 岁）：`幻化 | 裸体 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康`
  · 某「人工种族」（16 岁，在人物编辑器改成 20 岁并存读档后）：
    `幻化 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康` ← **没有「裸体」**
  · 但机制完全正常：全裸照常遮体、走光照常掉心情、评语照常生成。
  · **改年龄无效** —— 与年龄无关。

· **根因**：「裸体评价更多看法」用一条 XML patch 把页签挂到原版 Pawn 的公共父节点上
  （`Common/Patches/ReplaceNMMTab.xml`）：

  ```
  <xpath>Defs/ThingDef[@Name="BasePawn"]/inspectorTabs</xpath>
  <value><li>NudityMattersMore_opinions.ITab_Pawn_NMMOpinions</li></value>
  ```

  原版 `Human` 是 `<ThingDef Name="Human" ParentName="BasePawn">`，自己没写
  `inspectorTabs`，于是**继承**到含本页签的那一份 —— 原版人类因此正常。

  而 Aya 系列种族走的是另一条路：**在自己 XML 里另起一个根，并手抄了一份 15 项的
  原版页签列表**：

  ```xml
  <ThingDef Name="HAR_Nearmaere_BasePawn" Abstract="True">
    …
    <inspectorTabs>
      <li>ITab_Pawn_Health</li>
      <li>ITab_Pawn_Needs</li>
      … 共 15 项，与原版 BasePawn 完全一致 …
    </inspectorTabs>
  ```

  **唯独没有本页签**，而且这条链路根本不经过 `BasePawn` —— 之后任何人往 `BasePawn`
  上打补丁都影响不到它。

  已逐个核实的 **13 个 Aya 种族**（Nearmare / Xenoorca / Idhale / Silkiera / Neclose /
  Littluna / Solark / Saclean / Nexaga / Qualeela / Idearn / Chaoura / Outerm）：
  **全部自写 `inspectorTabs`，全部不含本页签**。

  ⇒ 页签压根没进 `ThingDef.inspectorTabs`，`ResolveReferences` 自然不把它解析进
  `inspectorTabsResolved`。这跟 `IsVisible`、跟年龄、跟裸体逻辑都没有关系 ——
  所以「机制一切正常、只有这个页签没了」。

· **修法**：给「自己写了页签列表、却漏了这一项」的 ThingDef 补上。
  挂 `LoadedLanguage.InjectIntoData_BeforeImpliedDefs` 的 **Postfix**（与【68】同一个
  方法，Harmony 允许多个 Postfix 并存）。

  时序依据（反编译 `PlayDataLoader.DoPlayLoad()`）：

  | 步 | 动作 |
  |---|---|
  | ② | `DefDatabase<T>.AddAllInMods()` ← Def 进全局库，`inspectorTabs` 已是 `List<Type>` |
  | **⑦** | **`InjectIntoData_BeforeImpliedDefs()`** ← **我们在这里补** |
  | ⑨ | `DefDatabase<ThingDef>.ResolveAllReferences()` → `ResolveReferences()` **无条件重建** `inspectorTabsResolved` |
  | ⑪ | `if (Prefs.DevMode) ErrorCheckAllDefs()` |

  ⑨ 里是 `inspectorTabsResolved = new List<InspectTabBase>()` 然后逐个
  `InspectTabManager.GetSharedInstance(...)`，所以在 ⑦ 补进去的类型会正常生效，
  不需要自己去碰 `inspectorTabsResolved`。

  补的四条判据（**同时**成立才动）：

  1. 本页签类型存在（没装 NMM 直接退出）；
  2. `def.race != null` —— 是 pawn，不是物品/建筑；
  3. `def.inspectorTabs` 非空 **且含 `ITab_Pawn_Health`** —— 确认这确实是一份
     「人形 pawn 的页签列表」；
  4. 目前不含本页签 —— 防重复，也保证本来就有的种族一点不受影响。

  ⇒ 原版人类、鼠族这类正常继承的被第 4 条挡掉；动物没有健康页签，第 3 条挡掉；
  物品/建筑没有 `race`，第 2 条挡掉。

· **⚠ 这一条走过弯路，记录在此避免重蹈**：

  v1.4.61 的初版把原因判成「RJW 的 `rjw.xxx.is_mechanoid` 用
  `FleshType.defName.Contains("Android")` 判机械体，把人工种族误判成机械体，
  于是 `can_do_loving` 返回 false、页签消失」。

  **该推断已被实测推翻**：
  · 13 个 Aya 种族的 `FleshType` 要么继承默认值（`Normal`），要么是 `HAR_xx_Flesh`，
    **没有一个含 `Android`**；
  · `intelligence` 是 `Humanlike`（`ToolUser` 只出现在 `_NPC` 变体上，那是敌方单位，
    不是玩家殖民者）；
  · 它们也没有声明 RJW 的 `RaceGroupDef`，所以 `can_do_loving` 里那两条年龄判据都不会命中。

  **教训**：`is_mechanoid` 的字符串匹配确实是个隐患（另见给作者的报告），
  但它**不是**这次的原因。判断「页签为什么不出现」时，先确认页签有没有进
  `inspectorTabs`，再去看 `IsVisible` —— 顺序反了就会一路查到错的地方。

· **自动让路**：没装 NMM → 打一行 Message 跳过；作者以后改成继承 `BasePawn`
  或自己补上这一项 → 第 4 条挡掉；玩家关掉本项 → 整项不安装。

"""


def main():
    write = "--write" in sys.argv
    raw = open(PATH, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    nl = "\r\n" if "\r\n" in text else "\n"

    # 定位旧 v1.4.61 段（从它的标题到下一个 ## v1.4.60 之前）
    m = re.search(r"^## v1\.4\.61.*?(?=^## v1\.4\.60)", text, re.S | re.M)
    if not m:
        print("找不到 v1.4.61 段，改为在 v1.4.60 之前插入")
        pos = text.find("## v1.4.60")
        if pos < 0:
            print("连 v1.4.60 都找不到")
            return 1
        new_text = text[:pos] + NEW.replace("\n", nl) + text[pos:]
    else:
        old_len = len(m.group(0))
        new_text = text[:m.start()] + NEW.replace("\n", nl) + text[m.end():]
        print("替换 v1.4.61 段：%d 字符 -> %d 字符" % (old_len, len(NEW)))

    print("新文件 %d 字符" % len(new_text))

    if write:
        open(PATH, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8"))
        back = open(PATH, "rb").read().decode("utf-8-sig")
        heads = re.findall(r"^## (v[\d.]+)", back, re.M)
        assert heads and heads[0] == "v1.4.62", "首个版本小节应为 v1.4.62，实际 %r" % (heads[:3],)
        # 只断言「v1.4.61 的标题」已被替换；正文里保留那次弯路的说明是有意的引用。
        assert "## v1.4.61" not in back, "v1.4.61 标题应已被替换掉"
        assert back.count("v1.4.60") == 1, "v1.4.60 条目数异常"
        print("已写入。复核：首节 %s；v1.4.61 已无；v1.4.60 仍在" % heads[0])
    else:
        print("[dry] 未写入")
    return 0


if __name__ == "__main__":
    sys.exit(main())
