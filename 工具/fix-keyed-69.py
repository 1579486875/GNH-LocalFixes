# -*- coding: utf-8 -*-
"""把三语 Keyed 里的【69】条目从旧 key 换成新 key 与新文案。

旧 key：GNH_LocalFixes_Toggle_nmmTabMechanoidMisjudge(_Desc)
新 key：GNH_LocalFixes_Toggle_nmmTabCustomRaceMissing(_Desc)

做法：整段删掉旧的两行，再插到 anchor（【68】的 _Desc）后面。
写完用 xml.etree 解析校验，并核对尖括号增量。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
LANGDIR = os.path.join(ROOT, "Languages")

ANCHOR = "GNH_LocalFixes_Toggle_descriptionWhitespaceTrim_Desc"
OLD_KEY = "nmmTabMechanoidMisjudge"
NEW_KEY = "nmmTabCustomRaceMissing"

TEXTS = {
    "ChineseSimplified": (
        "人工种族的「裸体评价」页签不出现（【69】）",
        "有些种族（实测 Aya 系列 13 个全中）在自己 XML 里另起了一个根，"
        "并手抄了一份 15 项的原版页签列表 —— 既不继承原版的 BasePawn，"
        "那份拷贝里也没有本页签。而本页签是靠补丁挂到 BasePawn 上的，"
        "所以这类种族天然拿不到，页签压根没进它们的页签列表。"
        "结果是模组机制一切正常（全裸照常遮体、走光照常掉心情），只有这个页签没有，"
        "而且与年龄无关。"
        "本项在语言注入之后、解析页签之前，给「自己写了页签列表、但漏了这一项」的人形种族补上。"
        "★ 判据用原版必有的「健康」页签：只补本来就是人形 pawn 页签栏的 def；"
        "★ 原版人类、鼠族这类正常继承的种族本来就含本页签，会被直接跳过；"
        "★ 动物没有健康页签、物品建筑没有种族定义，都不在范围内；"
        "★ 关掉本项 → 页签回到原状。",
    ),
    "ChineseTraditional": (
        "人工種族的「裸體評價」頁籤不出現（【69】）",
        "有些種族（實測 Aya 系列 13 個全中）在自己 XML 裡另起了一個根，"
        "並手抄了一份 15 項的原版頁籤列表 —— 既不繼承原版的 BasePawn，"
        "那份拷貝裡也沒有本頁籤。而本頁籤是靠補丁掛到 BasePawn 上的，"
        "所以這類種族天然拿不到，頁籤根本沒進它們的頁籤列表。"
        "結果是模組機制一切正常（全裸照常遮體、走光照常掉心情），只有這個頁籤沒有，"
        "而且與年齡無關。"
        "本項在語言注入之後、解析頁籤之前，給「自己寫了頁籤列表、但漏了這一項」的人形種族補上。"
        "★ 判據用原版必有的「健康」頁籤：只補本來就是人形 pawn 頁籤欄的 def；"
        "★ 原版人類、鼠族這類正常繼承的種族本來就含本頁籤，會被直接跳過；"
        "★ 動物沒有健康頁籤、物品建築沒有種族定義，都不在範圍內；"
        "★ 關掉本項 → 頁籤回到原狀。",
    ),
    "English": (
        "No nudity tab for races that hard-code their own tab list (69)",
        "Some races (all 13 Aya races I checked) start a fresh root in their own XML and "
        "copy the vanilla 15-entry inspectorTabs list by hand. That root does not inherit "
        "from BasePawn, and the copied list does not contain this tab. Since the tab is "
        "attached by a patch on BasePawn, those races never receive it - it simply never "
        "enters their own list. The mod's mechanics keep working normally (nudity is still "
        "covered, streaking still drops mood), only the tab is missing, and age has nothing "
        "to do with it. "
        "This fix, running after language injection and before the tab list is resolved, "
        "adds the tab to humanlike races that wrote their own list but missed this entry. "
        "* ITab_Pawn_Health is used as the test, so only real humanlike pawn tab lists are touched. "
        "* Vanilla humans and races that inherit normally (e.g. Ratkin) already have the tab "
        "and are skipped. "
        "* Animals have no health tab and items/buildings have no race, so they are out of scope. "
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
        nl = "\r\n" if "\r\n" in text else "\n"

        # 1) 删掉旧的两行（整行）
        removed = 0
        lines = text.split(nl)
        keep = []
        for ln in lines:
            if ("GNH_LocalFixes_Toggle_%s>" % OLD_KEY) in ln or \
               ("GNH_LocalFixes_Toggle_%s_Desc>" % OLD_KEY) in ln:
                removed += 1
                continue
            keep.append(ln)
        text = nl.join(keep)

        if ("GNH_LocalFixes_Toggle_%s>" % NEW_KEY) in text:
            print("%-20s 新 key 已存在（旧 key 删除 %d 行），跳过插入" % (lang, removed))
            if write:
                open(path, "wb").write(text.encode("utf-8-sig"))
            continue

        lt_before = text.count("<")

        block = (
            nl + "  <GNH_LocalFixes_Toggle_%s>%s</GNH_LocalFixes_Toggle_%s>" % (NEW_KEY, esc(title), NEW_KEY)
            + nl + "  <GNH_LocalFixes_Toggle_%s_Desc>%s</GNH_LocalFixes_Toggle_%s_Desc>"
            % (NEW_KEY, esc(desc), NEW_KEY)
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
            print("%-20s 已写入（删旧 %d 行 / 新增 XML 通过）" % (lang, removed))
        else:
            print("%-20s [dry] 删旧 %d 行，可插入" % (lang, removed))

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
