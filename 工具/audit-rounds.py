# -*- coding: utf-8 -*-
"""
逐轮取证脚本（本任务的 R9 起）。每一轮换一个维度，全部只读、可复算。

用法：python 工具/audit-rounds.py [轮次号]
      不带参数 = 全跑
"""
import os
import re
import sys
import glob
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))


def nums_of(text):
    return sorted(set(int(x) for x in re.findall(r"【(\d+)】", text)))


def r9_numbering_consistency():
    """R9：About.xml 与 FixToggles 的编号集合是否一致。"""
    about = open(os.path.join(BASE, "About", "About.xml"), encoding="utf-8-sig").read()
    fix = open(os.path.join(BASE, "源码", "FixToggles.cs"), encoding="utf-8-sig").read()
    na, nf = set(nums_of(about)), set(nums_of(fix))
    print("  About 编号 %d 个（%s~%s）" % (len(na), min(na), max(na)))
    print("  界面 编号 %d 个（%s~%s）" % (len(nf), min(nf), max(nf)))
    only_a = sorted(na - nf)
    only_f = sorted(nf - na)
    print("  只在 About：", only_a if only_a else "无")
    print("  只在界面  ：", only_f if only_f else "无")
    miss = [i for i in range(1, max(max(na), max(nf)) + 1) if i not in (na & nf)]
    print("  两边都缺：", miss if miss else "无 ✅")
    return not (only_a or only_f or miss)


def r10_lang_keys():
    """R10：三个语言文件的键集合是否一致（简繁英对齐）。"""
    ok = True
    sets = {}
    for lang in ("ChineseSimplified", "ChineseTraditional", "English"):
        d = os.path.join(BASE, "Languages", lang, "Keyed")
        if not os.path.isdir(d):
            continue
        keys = set()
        for p in glob.glob(os.path.join(d, "*.xml")):
            try:
                r = ET.parse(p).getroot()
            except Exception as e:
                print("  ✗ %s 解析失败：%s" % (p, e))
                ok = False
                continue
            for el in r:
                if isinstance(el.tag, str):
                    keys.add(el.tag)
        sets[lang] = keys
        print("  %-20s %d 个键" % (lang, len(keys)))
    if len(sets) > 1:
        base_lang = "ChineseSimplified" if "ChineseSimplified" in sets else list(sets)[0]
        for lang, ks in sets.items():
            if lang == base_lang:
                continue
            miss = sorted(sets[base_lang] - ks)
            extra = sorted(ks - sets[base_lang])
            if miss or extra:
                ok = False
                print("  %s 相对 %s：缺 %d（%s）/ 多 %d（%s）"
                      % (lang, base_lang, len(miss), miss[:4], len(extra), extra[:4]))
    return ok


def r11_doc_says_three():
    """R11：文档里「三个诊断」这种过时表述（现在是四个）。"""
    bad = []
    for pat in ("*.md", "About/*.xml", "Languages/*/Keyed/*.xml"):
        for p in glob.glob(os.path.join(BASE, pat)):
            t = open(p, encoding="utf-8-sig", errors="ignore").read()
            # 只认「指诊断工具」的语境；「那三个模组的 packageId」这种是正常的，不该报
            for m in re.finditer(r"三个诊断|那三个(?:回到|诊断|取证)|三个取证工具", t):
                bad.append((os.path.relpath(p, BASE), m.group(0)))
    for p, s in bad[:10]:
        print("  · %-46s %s" % (p, s))
    if not bad:
        print("  没有「三个诊断」这类过时表述 ✅")
    return not bad


def r12_profiler_group():
    """R12：性能剖析是否已成第二组、且无编号。"""
    fix = open(os.path.join(BASE, "源码", "FixToggles.cs"), encoding="utf-8-sig").read()
    ids = re.findall(r'new Entry\("([^"]+)"', fix)
    try:
        pos = ids.index("deepProfilerQuiet")
    except ValueError:
        print("  ✗ 找不到 deepProfilerQuiet")
        return False
    print("  deepProfilerQuiet 位于第 %d 项（0 基）；前 6 项：%s" % (pos, ids[:6]))
    ok = pos == 5
    print("  是第二组（紧跟 4 个诊断之后）：%s" % ("✅" if ok else "✗"))
    # 它的标签里不该再有【N】
    m = re.search(r'new Entry\("deepProfilerQuiet".*?"([^"]*)"', fix, re.S)
    if m:
        has = re.search(r"【\d+】", m.group(1))
        print("  标签：%s" % m.group(1))
        print("  已无编号：%s" % ("✅" if not has else "✗ 仍有 " + has.group(0)))
        ok = ok and not has
    return ok


ROUNDS = {
    "R9": ("About 与界面的编号集合一致", r9_numbering_consistency),
    "R10": ("三语言文件的键集合对齐", r10_lang_keys),
    "R11": ("文档里过时的「三个诊断」表述", r11_doc_says_three),
    "R12": ("性能剖析已成为第二组且无编号", r12_profiler_group),
}


def main():
    pick = sys.argv[1].upper() if len(sys.argv) > 1 else None
    allok = True
    for k, (name, fn) in ROUNDS.items():
        if pick and pick != k:
            continue
        print("=" * 78)
        print("%s  %s" % (k, name))
        print("=" * 78)
        try:
            ok = fn()
        except Exception as e:
            print("  异常：%s" % e)
            ok = False
        print("  → %s" % ("通过" if ok else "**有问题**"))
        print()
        allok = allok and ok
    print("=" * 78)
    print("总体：%s" % ("全部通过 ✅" if allok else "**存在待处理项**"))


if __name__ == "__main__":
    main()
