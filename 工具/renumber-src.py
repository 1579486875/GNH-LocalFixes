# -*- coding: utf-8 -*-
"""
R23：把 `源码/*.cs`（除 FixToggles.cs）里日志/注释中的【N】按同一映射重排。

为什么必须做：那些【N】会**打进玩家日志**（如 `[GNH LocalFixes] 【67】patched …`）。
如果只重排 About 与界面、不重排日志字符串，玩家看到日志里的编号就对不上设置界面。

映射与 `renumber.py` 完全一致：
    1..39 不变；40 删；41..61 → 40..60；62,63 删；64..68 → 61..65；69 删。
"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))


def mapping(n):
    if n <= 39:
        return n
    if n == 40:
        return None
    if 41 <= n <= 61:
        return n - 1
    if n in (62, 63):
        return None
    if 64 <= n <= 68:
        return n - 3
    if n == 69:
        return None
    return None


def main():
    write = "--write" in sys.argv
    total = 0
    for p in sorted(glob.glob(os.path.join(BASE, "源码", "*.cs"))):
        if os.path.basename(p) == "FixToggles.cs":
            continue                       # 已单独重排过
        raw = open(p, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        txt = raw.decode("utf-8-sig")
        hits = []

        def repl(m):
            n = int(m.group(1))
            new = mapping(n)
            if new is None:
                hits.append((n, None))
                return ""
            if new != n:
                hits.append((n, new))
                return "【%d】" % new
            return m.group(0)

        out = re.sub(r"【(\d+)】", repl, txt)
        # 【67b】这种带字母后缀的也处理一下（67→65）
        def repl2(m):
            n = int(m.group(1))
            new = mapping(n)
            if new is None or new == n:
                return m.group(0)
            hits.append((n, new))
            return "【%d%s】" % (new, m.group(2))

        out = re.sub(r"【(\d+)([a-zA-Z])】", repl2, out)

        if hits:
            total += len(hits)
            if write:
                open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + out.encode("utf-8"))
            print("%-46s %d 处（%s）%s"
                  % (os.path.basename(p), len(hits),
                     ", ".join("%d→%s" % (a, b if b else "删") for a, b in hits[:5]),
                     " ✓" if write else "（dry）"))
    print()
    print("合计 %d 处%s" % (total, "（已写盘）" if write else "（dry-run，加 --write）"))


if __name__ == "__main__":
    main()
