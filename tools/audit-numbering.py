# -*- coding: utf-8 -*-
"""列出 FixToggles.All 里各修复项与它的编号，找出「无号」与「重复号」。"""
import os
import re
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
P = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "FixToggles.cs"))

t = open(P, encoding="utf-8-sig").read()

# 逐条 Entry 解析（用括号配平切块，避免正则被转义坑）
rows = []
i = 0
while True:
    m = re.search(r'new Entry\("([^"]+)"', t[i:])
    if not m:
        break
    st = i + m.start()
    eid = m.group(1)
    # 找到这一条 Entry( 的括号范围
    p = st + t[st:].index("(")
    depth = 0
    j = p
    while j < len(t):
        if t[j] == "(":
            depth += 1
        elif t[j] == ")":
            depth -= 1
            if depth == 0:
                break
        j += 1
    block = t[st:j + 1]
    gm = re.search(r",\s*(Group\w+)", block)
    lm = re.search(r',\s*\n?\s*"((?:[^"\\]|\\.)*)"', block)
    label = lm.group(1) if lm else ""
    rows.append((eid, gm.group(1) if gm else "?", label))
    i = j + 1

TOOL = {"allDiagnostics", "rjwOrgasmProbe", "parsedIntLocator",
        "ew2TranspilerProbe", "pawnGenDeathProbe", "deepProfilerQuiet"}

print("Entry 总数: %d" % len(rows))
fix = [r for r in rows if r[0] not in TOOL]
print("修复项: %d（工具 %d）" % (len(fix), len(rows) - len(fix)))
print()

noid = []
nums = []
for eid, grp, label in fix:
    m = re.search(r"【(\d+)】", label)
    if m:
        nums.append(int(m.group(1)))
    else:
        noid.append((eid, grp, label[:40]))

print("== 无编号的修复项 %d 个 ==" % len(noid))
for eid, grp, lb in noid:
    print("  %-32s %-12s %s" % (eid, grp.replace("Group", ""), lb))

print()
print("== 编号统计 ==")
c = Counter(nums)
print("  有号 %d / 无号 %d" % (len(nums), len(noid)))
print("  范围 %s ~ %s" % (min(nums) if nums else "-", max(nums) if nums else "-"))
dup = {k: v for k, v in c.items() if v > 1}
print("  重复号:", dup if dup else "无")
missing = [n for n in range(1, max(nums) + 1) if n not in c] if nums else []
print("  空缺号:", missing)

# 2026-11-16 补：原先只 print。重复号或空缺号都算失败。
sys.exit(1 if (dup or missing) else 0)
