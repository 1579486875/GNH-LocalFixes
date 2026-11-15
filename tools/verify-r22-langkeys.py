# -*- coding: utf-8 -*-
"""R22：每个 Entry 的 Label / Desc 语言键是否都存在于语言文件里。"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

t = open(os.path.join(BASE, "src", "FixToggles.cs"), encoding="utf-8-sig").read()
ids = re.findall(r'new Entry\("([^"]+)"', t)
groups = sorted(set(re.findall(r'new Entry\("[^"]+",\s*(Group\w+)', t)))

print("Entry %d 个、分组 %d 个" % (len(ids), len(groups)))
need = set()
for i in ids:
    need.add("GNH_LocalFixes_Toggle_" + i)
    need.add("GNH_LocalFixes_Toggle_" + i + "_Desc")
for g in groups:
    need.add("GNH_LocalFixes_Group_" + g.replace("Group", ""))

for lang in ("ChineseSimplified", "ChineseTraditional", "English"):
    d = os.path.join(BASE, "Languages", lang, "Keyed")
    keys = set()
    for p in glob.glob(os.path.join(d, "*.xml")):
        try:
            r = ET.parse(p).getroot()
        except Exception:
            continue
        for el in r:
            if isinstance(el.tag, str):
                keys.add(el.tag)
    missing = sorted(need - keys)
    extra = len(keys - need)
    print("  %-20s 应有 %d / 实际 %d / 缺 %d%s / 富余 %d"
          % (lang, len(need), len(keys), len(missing),
             ("（" + ", ".join(missing[:3]) + "…）") if missing else "", extra))
