# -*- coding: utf-8 -*-
"""把【68】descriptionWhitespaceTrim 的两个 Keyed 条目插入三语文件。

守则要求：不用 PowerShell 处理含尖括号的内容；用 Python 读写，写完立刻用
xml.etree 解析校验，并核对 < 的数量与原文件一致（只允许新增我插入的那些）。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
LANGDIR = os.path.join(ROOT, "Languages")

ANCHOR = "GNH_LocalFixes_Toggle_tsindGarageDoorsQuiet_Desc"

TEXTS = {
    "ChineseSimplified": (
        "翻译给描述多带了首尾空白（【68】）",
        "有些模组的 DefInjected 翻译在 description 结尾多留了空格或换行。"
        "翻译是原样写进字段的（不像源 def 那样由引擎处理缩进），"
        "于是那个空白就留在了 def.description 里；DevMode 下每次加载都刷"
        "「Config error in X: description has trailing whitespace」。"
        "本机全库扫出 20 条这种翻译，日志里却只见到 1 条 —— "
        "因为很多 Def 子类 override 了 ConfigErrors 却没调 base，把其余 19 条藏住了。"
        "本项在翻译注入完成之后、DevMode 校验之前，把所有 Def 的 description 首尾空白裁掉。"
        "★ 只去不可见字符，一个可见的字都不改；"
        "★ 这是把数据修成引擎认可的状态，不是捂住报警；"
        "★ 不动 label（它会参与字符串拼接）；"
        "★ 整条全是空白的 description 不动，以免升级成「empty description」；"
        "★ 作者自己改对了本项就不做事，关掉则红字原样回来。",
    ),
    "ChineseTraditional": (
        "翻譯給描述多帶了首尾空白（【68】）",
        "有些模組的 DefInjected 翻譯在 description 結尾多留了空格或換行。"
        "翻譯是原樣寫進欄位的（不像源 def 那樣由引擎處理縮排），"
        "於是那個空白就留在了 def.description 裡；DevMode 下每次載入都刷"
        "「Config error in X: description has trailing whitespace」。"
        "本機全庫掃出 20 條這種翻譯，日誌裡卻只見到 1 條 —— "
        "因為很多 Def 子類 override 了 ConfigErrors 卻沒呼叫 base，把其餘 19 條藏住了。"
        "本項在翻譯注入完成之後、DevMode 校驗之前，把所有 Def 的 description 首尾空白裁掉。"
        "★ 只去不可見字元，一個可見的字都不改；"
        "★ 這是把資料修成引擎認可的狀態，不是捂住報警；"
        "★ 不動 label（它會參與字串拼接）；"
        "★ 整條全是空白的 description 不動，以免升級成「empty description」；"
        "★ 作者自己改對了本項就不做事，關掉則紅字原樣回來。",
    ),
    "English": (
        "Translation adds leading/trailing whitespace to descriptions (68)",
        "Some mods' DefInjected translations leave a space or newline at the end of a description. "
        "Translations are written into the field verbatim (unlike source defs, where the engine "
        "handles indentation), so the whitespace stays in def.description, and with DevMode on "
        "every load logs \"Config error in X: description has trailing whitespace\". "
        "A full scan of this machine found 20 such translations, yet only 1 appeared in the log - "
        "many Def subclasses override ConfigErrors without calling base, hiding the other 19. "
        "This fix trims leading/trailing whitespace from every Def's description right after "
        "language injection and before DevMode validation. "
        "* Only invisible characters are removed; not a single visible character changes. "
        "* This puts the data into the state the engine itself requires; it does not silence the warning. "
        "* Labels are left alone (they take part in string concatenation). "
        "* Descriptions that are entirely whitespace are left alone, so they do not become \"empty description\". "
        "* If the author fixes it, this fix does nothing; disable it and the errors come back.",
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

        if "GNH_LocalFixes_Toggle_descriptionWhitespaceTrim>" in text:
            print("%-20s 已存在，跳过" % lang)
            continue

        nl = "\r\n" if "\r\n" in text else "\n"
        block = (
            nl + "  <GNH_LocalFixes_Toggle_descriptionWhitespaceTrim>%s"
            "</GNH_LocalFixes_Toggle_descriptionWhitespaceTrim>"
            % esc(title)
            + nl + "  <GNH_LocalFixes_Toggle_descriptionWhitespaceTrim_Desc>%s"
            "</GNH_LocalFixes_Toggle_descriptionWhitespaceTrim_Desc>" % esc(desc)
        )

        # 锚点：整行 <ANCHOR>…</ANCHOR> 之后
        pat = re.compile(r"(<" + ANCHOR + r">.*?</" + ANCHOR + r">)", re.S)
        m = pat.search(text)
        if not m:
            print("%-20s 找不到锚点 %s" % (lang, ANCHOR))
            ok = False
            continue

        new_text = text[:m.end()] + block + text[m.end():]

        # 校验：能解析
        try:
            ET.fromstring(new_text.encode("utf-8"))
        except ET.ParseError as e:
            print("%-20s XML 校验失败：%s" % (lang, e))
            ok = False
            continue

        lt_after = new_text.count("<")
        added = lt_after - lt_before
        expect = 4  # <Tag> <Tag> </Tag> </Tag>
        if added != expect:
            print("%-20s 尖括号增量异常：期望 %d，实际 %d" % (lang, expect, added))
            ok = False
            continue

        if write:
            with open(path, "wb") as f:
                f.write(new_text.encode("utf-8-sig"))
            print("%-20s 已写入（+%d 尖括号，XML 校验通过）" % (lang, added))
        else:
            print("%-20s [dry] 可插入（+%d 尖括号，XML 校验通过）" % (lang, added))

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
