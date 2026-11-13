# -*- coding: utf-8 -*-
"""检查 FixToggles 里每个分组内的 Entry 顺序是否「按编号从小到大」。

判据（用户 2026-11-13 要求）：
    · 组内条目按修复编号【N】从小到大、从上到下排列；
    · 没有编号的条目（诊断工具、纯开关等）放在该组的**最前面**或**最后面**，
      不要穿插在有编号的条目之间 —— 否则视觉上就是乱序。
"""
import io
import re
import os

P = r"E:\TAML\modfix\GNH-LocalFixes\src\FixToggles.cs"
s = io.open(P, encoding="utf-8-sig").read()

# 抓所有 Entry：id + group + label
pat = re.compile(
    r'new Entry\("([^"]+)",\s*(Group\w+),\s*\n\s*"([^"]*)"',
    re.S)
entries = []
for m in pat.finditer(s):
    eid, grp, label = m.group(1), m.group(2), m.group(3)
    # 从 label 里抠编号
    num = None
    mm = re.search(r'【(\d+)】', label)
    if mm:
        num = int(mm.group(1))
    entries.append((grp, eid, label, num))

from collections import OrderedDict
by_group = OrderedDict()
for grp, eid, label, num in entries:
    by_group.setdefault(grp, []).append((eid, label, num))

print("共 %d 个 Entry，%d 个分组\n" % (len(entries), len(by_group)))

problems = 0
for grp, items in by_group.items():
    nums = [(i, l, n) for (i, l, n) in items]
    numbered = [x for x in nums if x[2] is not None]
    unnumbered = [x for x in nums if x[2] is None]

    print("=== %s（%d 项：有编号 %d / 无编号 %d）" % (grp, len(items), len(numbered), len(unnumbered)))

    # ① 有编号的必须严格递增
    seq = [x[2] for x in numbered]
    ok_num = all(seq[i] < seq[i + 1] for i in range(len(seq) - 1))
    if not ok_num:
        problems += 1
        print("   ❌ 有编号的项没有从小到大：", seq)
    else:
        if seq:
            print("   ✅ 有编号的项递增：", seq)

    # ② 无编号的不应夹在有编号的中间
    idx_nums = [k for k, x in enumerate(nums) if x[2] is not None]
    idx_unn = [k for k, x in enumerate(nums) if x[2] is None]
    if idx_nums and idx_unn:
        first_num, last_num = min(idx_nums), max(idx_nums)
        stuck = [k for k in idx_unn if first_num < k < last_num]
        if stuck:
            problems += 1
            print("   ❌ 有 %d 个无编号项夹在有编号的中间（位置 %s）" % (len(stuck), stuck))
            for k in stuck:
                print("        %s" % nums[k][1][:50])
        else:
            print("   ✅ 无编号项都在两端")

    # 列出实际顺序
    for i, l, n in nums:
        mark = ("%3d" % n) if n is not None else "  -"
        print("      [%s] %s" % (mark, l[:62]))
    print()

print("=" * 60)
print("有问题的分组：%d 个" % problems)
