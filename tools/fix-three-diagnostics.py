# -*- coding: utf-8 -*-
"""
R11：把文档里过时的「三个诊断 / 那三个」改成「四个」。

背景：诊断组 2026-11-13 加了 `rjwOrgasmProbe` 变成 4 个，
但说明文字还停在「三个」，与界面对不上。
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

TARGETS = ["About/About.xml", "README.md", "技术说明.md"]
import glob
TARGETS += [os.path.relpath(p, BASE) for p in glob.glob(os.path.join(BASE, "Languages", "*", "Keyed", "*.xml"))]

# (旧, 新)
RULES = [
    ("本组下面那三个都是", "本组下面那四个都是"),
    ("管那三个", "管那四个"),
    ("那三个回到「各看各的」", "那四个回到「各看各的」"),
    ("对三个诊断全部命中", "对四个诊断全部命中"),
    ("把三个诊断一律视为开", "把四个诊断一律视为开"),
    ("三个诊断一律视为开", "四个诊断一律视为开"),
    ("三个诊断各自仍可单控", "四个诊断各自仍可单控"),
    ("三个取证", "四个取证"),
    ("三个都是**只读取证**工具", "四个都是**只读取证**工具"),
    ("本组下面那三个", "本组下面那四个"),
]


def main():
    write = "--write" in sys.argv
    total = 0
    for rel in TARGETS:
        p = os.path.join(BASE, rel)
        if not os.path.exists(p):
            continue
        raw = open(p, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        txt = raw.decode("utf-8-sig")
        n = 0
        for old, new in RULES:
            c = txt.count(old)
            if c:
                txt = txt.replace(old, new)
                n += c
        # 兜底：还有孤立的「三个诊断」
        c2 = len(re.findall(r"三个诊断", txt))
        if c2:
            txt = txt.replace("三个诊断", "四个诊断")
            n += c2
        if n:
            if write:
                open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + txt.encode("utf-8"))
            print("%-56s %d 处%s" % (rel, n, " ✓" if write else "（dry-run）"))
            total += n
    print()
    print("合计 %d 处%s" % (total, "（已写）" if write else "（dry-run，加 --write）"))


if __name__ == "__main__":
    main()
