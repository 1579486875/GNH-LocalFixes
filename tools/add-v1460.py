# -*- coding: utf-8 -*-
"""把 v1.4.60 的条目插入《技术说明.md》顶部（在 v1.4.59 之前）。"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PATH = os.path.join(ROOT, "技术说明.md")

ENTRY = """## v1.4.60（2026-11-16）· 【68】翻译给描述多带了首尾空白

### 【68】`Config error in X: description has trailing whitespace`

· **症状**：DevMode 下每次加载刷
  `Config error in LDR_ISBM_silo: description has trailing whitespace`
  （本机跨两次启动各 1 条，累计 2 条）。

· **根因**：真凶不是源 def，是它的**简体中文翻译**。
  `…\\294100\\3695591404\\1.6\\Languages\\ChineseSimplified (简体中文)\\DefInjected\\ThingDef\\Buildings_MissileSilo_LDR.xml`
  里 `<LDR_ISBM_silo.description>` 结尾多了一个**半角空格**（同文件另外 9 条 description 都干净）。

  `Defs/*.xml` 的 description 走 `DirectXmlToObjectNew`，多行缩进由引擎处理；
  `DefInjected` 走 `LoadedLanguage.InjectIntoData_BeforeImpliedDefs() → field.SetValue()`，
  **原样赋值、不 trim** —— XML 里写什么，字段里就是什么。
  这就是为什么全库几万条多行 description 零报错，而扫描出的 20 条首尾空白**全部**来自 DefInjected。

· **为什么扫出 20 条、日志只报 1 条**：`Def.ConfigErrors()` 确实查首尾空白
  （`char.IsWhiteSpace(description[0])` / `description[length-1]`），
  但**很多子类 override 了它却没调 `base.ConfigErrors()`** ——
  已反编译确认 `Verse.GeneDef.ConfigErrors()` 通篇没有 base 调用，
  所以 `RJW_Traits_Bimbo_Gene` 那条前导空格**永远不会被报出来**；
  `IRBM.MissileBodyDef`（`IRBM_DF41A`）同理。
  只有 `ThingDef`（调了 base）的 `LDR_ISBM_silo` 露出来。
  **其余 19 条不是误报**，它们只是被各自的类藏住了 —— 一旦作者更新时换了基类、
  或游戏某版补上 base 调用，就会集体冒出来。

· **修法**：挂 `LoadedLanguage.InjectIntoData_BeforeImpliedDefs` 的 **Postfix**，
  遍历 `GenDefDatabase.AllDefTypesWithDatabases()` →
  `GetAllDefsInDatabaseForDef(Type)`，把所有 Def 的 `description` 首尾 `Trim()`。
  （不用 `[StaticConstructorOnStartup]`，原因见下。）

· **加载时序**（反编译 `Verse.PlayDataLoader.DoPlayLoad()` 逐段确认）：

  | 步 | 动作 |
  |---|---|
  | ① | `LoadedModManager.LoadAllActiveMods()` ← 本模组构造函数在这里 |
  | ② | `DefDatabase<T>.AddAllInMods()` ← Def 进全局库 |
  | **⑦** | **`LanguageDatabase.activeLanguage.InjectIntoData_BeforeImpliedDefs()`** ← 翻译注入 |
  | ⑧ | `DefGenerator.GenerateImpliedDefs_PreResolve()` |
  | **⑪** | **`if (Prefs.DevMode) DefDatabase<T>.ErrorCheckAllDefs()`** ← 红字在这里报 |
  | ⑫ | `ExecuteWhenFinished( InjectIntoData_AfterImpliedDefs )` |
  | **⑬** | **`ExecuteWhenFinished( StaticConstructorOnStartupUtility.CallAll() )`** |

  ⇒ `[StaticConstructorOnStartup]` 在 ⑬，**晚于** ⑪ 的校验，用它 trim 来不及；
  ⑦ 是 ⑪ 之前**最后一个**能碰到翻译结果的公开入口。

· **为什么 trim 是「修好」而不是「掩盖」**：游戏自己在 `Def.ConfigErrors()` 里把首尾空白
  **明确定义为错误**，所以裁掉它们 = 把数据修成引擎认可的状态。
  `string.Trim()` 去掉的是不可见字符，不改动任何一个人能读到的字，译文语义零变化。

· **刻意不动的东西**：
  ① **不碰 `label`** —— label 会参与字符串拼接，作者可能故意留空格当分隔符
     （扫描时也看到 `MechBaby_BornFromMech.label`、`Conversion.comps.2.label` 带前导空格，
     但那不在「红字」范畴，trim 它有实际破坏风险）；
  ② **整条全是空白的 description 不动** —— 裁成空串会把「首尾空白」升级成
     「empty description」，越修越糟。

· **向下兼容**：`GenDefDatabase.AllDefTypesWithDatabases()` /
  `GetAllDefsInDatabaseForDef(Type)` / `Def.description`（public string）/
  `LoadedLanguage.InjectIntoData_BeforeImpliedDefs()`（void，无参，非虚）
  在 **1.6.4871** 与 **1.7.5011** 两套 `Assembly-CSharp` 里逐一核对过，签名完全一致。

· **幂等**：已 trim 过的再 trim 不变；热重载 / 切语言重复调用都安全。
  作者哪天自己改对了，本项就什么都不做，日志也不再打印。

· **配套工具**：`tools/scan-desc-whitespace.py`
  （全库扫 32,842 个 DefInjected XML + 12 个 DLC `.tar` 语言包；
  加 `--red-only` 只看会让 `ConfigErrors` 报红的两段式 `defName.description`）。

"""


def main():
    write = "--write" in sys.argv
    raw = open(PATH, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")

    if "v1.4.60" in text:
        print("v1.4.60 已存在，跳过")
        return 0

    nl = "\r\n" if "\r\n" in text else "\n"
    anchor = "## v1.4.59（2026-11-16）"
    pos = text.find(anchor)
    if pos < 0:
        print("找不到锚点 %s" % anchor)
        return 1

    block = ENTRY.replace("\n", nl)
    new_text = text[:pos] + block + text[pos:]

    print("可插入：%d 字符 → 新文件 %d 字符" % (len(block), len(new_text)))
    if write:
        open(PATH, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8"))
        print("已写入（BOM=%s）" % bom)
        # 复核：文件开头是标题段，所以不能断言 startswith；改断言「首个版本小节是 v1.4.60」
        back = open(PATH, "rb").read().decode("utf-8-sig")
        import re as _re
        heads = _re.findall(r"^## (v[\d.]+)", back, _re.M)
        assert heads and heads[0] == "v1.4.60", "首个版本小节应为 v1.4.60，实际 %r" % (heads[:2],)
        assert back.count("v1.4.59") == text.count("v1.4.59"), "旧条目被破坏"
        print("复核通过：首个版本小节 = %s，v1.4.59 出现次数不变（%d）"
              % (heads[0], back.count("v1.4.59")))
    else:
        print("[dry] 未写入")
    return 0


if __name__ == "__main__":
    sys.exit(main())
