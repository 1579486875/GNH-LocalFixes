#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""R346-R6：核查「界面/文档说的默认状态」与 `IsDefaultOff` 的实现是否一致。

界面与 About 都把「诊断工具」整组宣传成「默认关闭」，性能剖析也写着「默认关闭」。
只要 `IsDefaultOff` 漏掉其中任何一个，那一项出厂就是**开着**的 —— 文档在骗人。

本轮就是靠这个查出 `rjwOrgasmProbe` 漏登记（2026-11-16）。
"""
import re, os, sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(BASE, r"源码\FixToggles.cs")
text = open(CS, 'rb').read().decode('utf-8-sig')
lines = text.split('\n')

# All 数组里的 (id, group)
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

ENTRIES = []
for s0 in starts:
    e0 = _end(s0)
    blk = '\n'.join(lines[s0:e0+1])
    strs = re.findall(r'"((?:[^"\\]|\\.)*)"', blk)
    iid, lab = strs[0], (strs[1] if len(strs) > 1 else '')
    grp = re.search(r'(Group[A-Z][A-Za-z]*)\s*,', blk).group(1)
    ENTRIES.append({'id': iid, 'group': grp, 'label': lab})

# IsDefaultOff 的返回表达式
i0 = next(k for k, ln in enumerate(lines) if 'internal static bool IsDefaultOff' in ln)
i1 = next(k for k in range(i0, len(lines)) if lines[k].strip() == '}')
body = '\n'.join(lines[i0:i1 + 1])
# 去掉注释行
code = '\n'.join(ln for ln in body.split('\n') if not ln.strip().startswith('//'))
ids_default_off = set(re.findall(r'string\.Equals\(id,\s*"([^"]+)"', code))

print(f"IsDefaultOff 里登记了 {len(ids_default_off)} 项：{sorted(ids_default_off)}\n")

EXPECT_OFF = set()
problems = []

# ① 诊断工具组：整组都必须是默认关闭
for e in ENTRIES:
    if e['group'] == 'GroupDiagnostics':
        EXPECT_OFF.add(e['id'])

# ② 标签里自称「默认关闭」的，也必须默认关闭
for e in ENTRIES:
    if '默认关闭' in e['label']:
        EXPECT_OFF.add(e['id'])

print(f"按「诊断工具组 + 标签自称默认关闭」应默认关闭的：{len(EXPECT_OFF)} 项")
for i in sorted(EXPECT_OFF):
    ok = i in ids_default_off
    lbl = next((x['label'] for x in ENTRIES if x['id'] == i), '')
    print(f"  {'✅' if ok else '❌'} {i:<24} {lbl}")
    if not ok:
        problems.append(i)

# ③ 反向：IsDefaultOff 里登记了、但标签没写「默认关闭」的（可能是漏写说明）
print("\n反向核查（IsDefaultOff 里登记、标签却没写「默认关闭」）：")
for i in sorted(ids_default_off):
    lbl = next((x['label'] for x in ENTRIES if x['id'] == i), '')
    if '默认关闭' not in lbl:
        print(f"  ⚠ {i:<24} 标签={lbl or '（不存在的 id！）'}")

print(f"\n==== 不一致：{len(problems)} 项 {problems} ====")
sys.exit(1 if problems else 0)
