# -*- coding: utf-8 -*-
"""找出 源码/ 下「方法声明前没有 /// 注释」的位置。"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

PAT = re.compile(r"\n(\s*)(public|private|internal|protected)[^\n{;=]*\([^)]*\)\s*\n\s*\{")

for p in sorted(glob.glob(os.path.join(BASE, "源码", "*.cs"))):
    t = open(p, encoding="utf-8-sig").read()
    for m in PAT.finditer(t):
        head = t[max(0, m.start() - 160):m.start()]
        if "///" in head[-140:]:
            continue
        line = t[:m.start()].count("\n") + 2
        sig = m.group(0).strip().split("\n")[0][:74]
        print("  %-36s L%-5d %s" % (os.path.basename(p), line, sig))
