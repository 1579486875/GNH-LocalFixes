# -*- coding: utf-8 -*-
"""
修复编号：把【40】（DeepProfiler，已降级为工具、不该占修复编号）删掉，
并把 41~61 → 40~60、64~68 → 61~65，让编号变成 **1~65 连续无空号**。

映射表（唯一真相）：
    1..39   不变
    40      → 删除（DeepProfiler 是工具，不占修复编号）
    41..61  → 40..60
    62,63   → 删除（原是「跨模组保留位」，现在由 61..65 填上）
    64..68  → 61..65

同步三个文件：
    About/About.xml                  发布说明里的编号
    README.md                        对外文档里的编号（只改出现的那些）
    src/FixToggles.cs                界面标题里的编号
    Languages/*/Keyed/*.xml          语言键里的编号（如果描述里带编号）

用法：python tools/renumber.py [--write]
"""
import os
import re
import sys
import glob

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))


def mapping(n):
    """旧编号 → 新编号；返回 None 表示该编号要删除。"""
    if n <= 39:
        return n
    if n == 40:
        return None                    # DeepProfiler 降级为工具，不占修复编号
    if 41 <= n <= 61:
        return n - 1
    if n in (62, 63):
        return None
    if 64 <= n <= 68:
        return n - 3
    if n == 69:
        return None                    # 历史遗留错号：About 与界面都没有第 69 项
    return None


TARGETS = ["About/About.xml", "README.md"]   # src/FixToggles.cs 已单独处理，不再重复减
TARGETS += [os.path.relpath(p, BASE) for p in
            glob.glob(os.path.join(BASE, "Languages", "*", "Keyed", "*.xml"))]


def process(path, write):
    p = os.path.join(BASE, path)
    if not os.path.exists(p):
        return None
    raw = open(p, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    txt = raw.decode("utf-8-sig")
    hits = []

    def repl(m):
        n = int(m.group(1))
        new = mapping(n)
        if new is None:
            hits.append((n, None))
            return ""                  # 整个【N】去掉（DeepProfiler / 历史错号）
        if new != n:
            hits.append((n, new))
            return "【%d】" % new
        return m.group(0)

    out = re.sub(r"【(\d+)】", repl, txt)

    # 删掉编号后可能留下「（，默认关闭）」「（）」这类残迹 —— 清理掉
    out = out.replace("（，", "（").replace("，）", "）").replace("（）", "")
    out = re.sub(r"\(\s*,\s*", "(", out)
    out = re.sub(r"\s*,\s*\)", ")", out)
    changed = out != txt
    if changed and write:
        open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + out.encode("utf-8"))
    if hits:
        print("%-52s %d 处改动（%s）%s"
              % (path, len(hits),
                 ", ".join("%d→%s" % (a, b if b else "删") for a, b in hits[:6]),
                 " ✓已写" if (changed and write) else ("（只读）" if not write else "")))
    return len(hits) if hits else 0


def main():
    write = "--write" in sys.argv
    total = 0
    for t in TARGETS:
        n = process(t, write)
        if n:
            total += n
    print()
    print("合计 %d 处%s" % (total, "（已写盘）" if write else "（dry-run，加 --write 才改）"))


if __name__ == "__main__":
    main()
