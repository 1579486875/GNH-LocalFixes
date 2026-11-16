# -*- coding: utf-8 -*-
"""把【69】nmmTabMechanoidMisjudge 的两个 Keyed 条目插入三语文件。

守则要求：不用 PowerShell 处理含尖括号的内容；用 Python 读写，写完立刻用
xml.etree 解析校验，并核对 < 的增量正好等于我插入的那 4 个。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
LANGDIR = os.path.join(ROOT, "Languages")

ANCHOR = "GNH_LocalFixes_Toggle_descriptionWhitespaceTrim_Desc"
KEY = "nmmTabMechanoidMisjudge"

TEXTS = {
    "ChineseSimplified": (
        "人工种族的「裸体评价」页签不出现（【69】）",
        "「裸体评价更多看法」的页签是否显示，取决于它的 IsVisible："
        "canDoLoving && Humanlike && !EntityOrGhoul。第一项来自 RJW 的 can_do_loving，"
        "而它的**第一行**就是「如果是机械体就返回 false」。"
        "RJW 判机械体用的是字符串包含：只要 FleshType 的名字里含 Android 就算，"
        "唯一豁免是 Androids 那一个扩展。"
        "于是除 Androids 之外，任何名字带 Android 的人工种族都会被当成机械体，"
        "can_do_loving 直接返回 false，页签随之消失 —— 但模组机制完全正常"
        "（全裸照常遮体、走光照常掉心情），因为裸体判定走的是另一套代码。"
        "本项只在「人形 + 非 entity/ghoul + 确实被误判成机械体」三条同时成立时才让页签显示。"
        "★ 未成年的人形种族不会被放宽（尊重作者原意）；"
        "★ 真机械体、Ghoul、Anomaly 实体一律不动；"
        "★ 原版人类本来就显示，零影响；关掉本项则回到原状。",
    ),
    "ChineseTraditional": (
        "人工種族的「裸體評價」頁籤不出現（【69】）",
        "「裸體評價更多看法」的頁籤是否顯示，取決於它的 IsVisible："
        "canDoLoving && Humanlike && !EntityOrGhoul。第一項來自 RJW 的 can_do_loving，"
        "而它的**第一行**就是「如果是機械體就回傳 false」。"
        "RJW 判斷機械體用的是字串包含：只要 FleshType 的名字裡含 Android 就算，"
        "唯一豁免是 Androids 那一個擴充。"
        "於是除 Androids 之外，任何名字帶 Android 的人工種族都會被當成機械體，"
        "can_do_loving 直接回傳 false，頁籤隨之消失 —— 但模組機制完全正常"
        "（全裸照常遮體、走光照常掉心情），因為裸體判定走的是另一套程式碼。"
        "本項只在「人形 + 非 entity/ghoul + 確實被誤判成機械體」三條同時成立時才讓頁籤顯示。"
        "★ 未成年的人形種族不會被放寬（尊重作者原意）；"
        "★ 真機械體、Ghoul、Anomaly 實體一律不動；"
        "★ 原版人類本來就顯示，零影響；關掉本項則回到原狀。",
    ),
    "English": (
        "No nudity tab for \"artificial\" races (69)",
        "Whether the Nudity Matters More opinions tab appears depends on its IsVisible: "
        "canDoLoving && Humanlike && !EntityOrGhoul. The first term comes from RJW's "
        "can_do_loving, whose **very first line** is \"if this is a mechanoid, return false\". "
        "RJW decides \"mechanoid\" by string containment: any FleshType whose defName contains "
        "\"Android\" counts, and the only exemption is the Androids extension. "
        "So apart from Androids itself, any artificial race with Android in its FleshType name "
        "is treated as a mechanoid, can_do_loving returns false, and the tab disappears - "
        "while the mod's own mechanics keep working perfectly (nudity is still covered, "
        "streaking still drops mood), because that logic runs through different code. "
        "This fix shows the tab again only when all three hold: humanlike, not an entity/ghoul, "
        "and genuinely misjudged as a mechanoid. "
        "* Humanlike minors are NOT relaxed (the author's intent is kept). "
        "* Real mechanoids, ghouls and anomalies are untouched. "
        "* Vanilla humans already show the tab, so they are unaffected. "
        "* Disable this entry and everything goes back to the original behaviour.",
    ),
}


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def main():
    write = "--write" in sys.argv
    ok = True

    for lang, (title, desc) in TEXTS.items():
        path = os.path.join(LANGDIR, lang, "Keyed", "GNH_LocalFixes.xml")
        if not os.path.isfile(path):
            print("缺失：%s" % path)
            ok = False
            continue

        raw = open(path, "rb").read()
        text = raw.decode("utf-8-sig")
        lt_before = text.count("<")

        if ("GNH_LocalFixes_Toggle_%s>" % KEY) in text:
            print("%-20s 已存在，跳过" % lang)
            continue

        nl = "\r\n" if "\r\n" in text else "\n"
        block = (
            nl + "  <GNH_LocalFixes_Toggle_%s>%s</GNH_LocalFixes_Toggle_%s>" % (KEY, esc(title), KEY)
            + nl + "  <GNH_LocalFixes_Toggle_%s_Desc>%s</GNH_LocalFixes_Toggle_%s_Desc>"
            % (KEY, esc(desc), KEY)
        )

        pat = re.compile(r"(<" + ANCHOR + r">.*?</" + ANCHOR + r">)", re.S)
        m = pat.search(text)
        if not m:
            print("%-20s 找不到锚点 %s" % (lang, ANCHOR))
            ok = False
            continue

        new_text = text[:m.end()] + block + text[m.end():]

        try:
            ET.fromstring(new_text.encode("utf-8"))
        except ET.ParseError as e:
            print("%-20s XML 校验失败：%s" % (lang, e))
            ok = False
            continue

        added = new_text.count("<") - lt_before
        if added != 4:
            print("%-20s 尖括号增量异常：期望 4，实际 %d" % (lang, added))
            ok = False
            continue

        if write:
            open(path, "wb").write(new_text.encode("utf-8-sig"))
            print("%-20s 已写入（+%d 尖括号，XML 通过）" % (lang, added))
        else:
            print("%-20s [dry] 可插入（XML 通过）" % lang)

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
