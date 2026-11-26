#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""R346-R5：核对 About.xml 的「修复速览」清单与 FixToggles.All 是否逐条对得上。

About.xml 里的这份速览是玩家在模组管理器里能读到的第一手说明。
编号一旦与实现错位，玩家照着编号去找开关就会找错 —— 这类错不报错、不崩，只有人去对才发现。
"""
import re, os
import sys
import xml.etree.ElementTree as ET

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(BASE, r"源码\FixToggles.cs")
AB = os.path.join(BASE, r"About\About.xml")

# C# 编号表
text = open(CS, 'rb').read().decode('utf-8-sig')
lines = text.split('\n')
mline = next(k for k, ln in enumerate(lines) if 'internal static readonly Entry[] All = new Entry[]' in ln)
endline = next(k for k in range(mline, len(lines)) if lines[k].strip() == '};')
starts = [k for k in range(mline, endline) if lines[k].lstrip().startswith('new Entry(')]

def _end(s0):
    depth, instr, esc = 0, False, False
    for k in range(s0, endline):
        for ch in lines[k]:
            if esc: esc = False; continue
            if ch == '\\' and instr: esc = True; continue
            if ch == '"': instr = not instr; continue
            if instr: continue
            if ch in '([': depth += 1
            elif ch in ')]':
                depth -= 1
                if depth == 0 and ch == ')':
                    if lines[k].rstrip().endswith(('),', ')')):
                        return k
    return endline - 1

CSNUM = {}
for s0 in starts:
    e0 = _end(s0)
    blk = '\n'.join(lines[s0:e0+1])
    strs = re.findall(r'"((?:[^"\\]|\\.)*)"', blk)
    iid, lab = strs[0], (strs[1] if len(strs) > 1 else '')
    m = re.search(r'【(\d+)】', lab)
    if m:
        CSNUM[int(m.group(1))] = (iid, lab)

# About 的速览段
ab = open(AB, 'rb').read().decode('utf-8-sig')
m = re.search(r'■ 修复速览.*?(?=■ 怎么用)', ab, re.S)
assert m, "找不到「修复速览」段"
seg = m.group(0)

rows = re.findall(r'^【(\d+)】(.+)$', seg, re.M)
print(f"About 速览段：{len(rows)} 行带编号\n")
print(f"{'号':>3}  {'About.xml 里的说法':<44} {'C# 里同号的项':<40} 判定")
print("-" * 130)
bad = 0
for num_s, desc in rows:
    n = int(num_s)
    cs = CSNUM.get(n)
    d = desc.strip()[:42]
    if not cs:
        print(f"{n:>3}  {d:<44} {'（C# 里没有这个编号）':<40} ❌")
        bad += 1
        continue
    # 粗判：把两边都去掉标点与空格，看有没有共同的 4 字以上片段
    def norm(s):
        return re.sub(r'[（）()【】、，,：:·\s]', '', s)
    a, b = norm(desc), norm(cs[1])
    common = any(a[i:i+4] in b for i in range(max(0, len(a)-3))) if len(a) >= 4 else (a in b)
    flag = '✅' if common else '❌'
    if not common:
        bad += 1
    print(f"{n:>3}  {d:<44} {cs[1][:38]:<40} {flag}")

print(f"\n对不上：{bad} / {len(rows)} 行")

# About 里有编号但 C# 没有的号段
about_nums = set(int(x) for x, _ in rows)
cs_nums = set(CSNUM)
print(f"\nAbout 有、C# 没有的号：{sorted(about_nums - cs_nums)}")
print(f"C# 有、About 没有的号：{sorted(cs_nums - about_nums)}")

# 速览段里声称的条数
mt = re.search(r'修复速览（(\d+)\s*条', seg)
print(f"\nAbout 声称条数：{mt.group(1) if mt else '?'}；C# 实际有编号项：{len(CSNUM)}")

# 2026-11-16 补：原先只 print。这里把「号集合不一致」或「声称条数≠实际条数」判为失败。
# 2026-11-16 订正：**只依据号集合差集**判失败。
# 不拿「About 声称 66 条」跟「C# 有编号 65 项」比 —— 那个 66 是含无编号项的口径
# （72 项 = 66 条修复 + 6 个工具；66 条里有 1 条是 xmlPatchGate，按规矩不编号）。
# 把这种口径写死在脚本里，只会随改动漂移。
sys.exit(1 if ((about_nums - cs_nums) or (cs_nums - about_nums)) else 0)
