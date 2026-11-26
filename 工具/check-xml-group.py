#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""核查「带 xmlPatchFile: 的项」是否都在 `GroupXmlPatches` 组里。

为什么要有这条（模板 §3.4「一组的主题必须单一」）：
  `xmlPatchFile:` 意味着这一项**通过 Patches\\ 下的 XML 补丁执行**，
  而「XML 补丁（可单独关闭）」这组的定义就是「每个补丁文件各一项 + 一个总控」。
  一旦有一项带着 xmlPatchFile 却待在别的组：
    · 那个组的主题就不单一了（混进一个走 XML 的项）；
    · 玩家在 XML 组里数开关会比 Patches\\ 里的文件数少；
    · 「让 XML 补丁也能单独关闭」那个总控的说明（「下面每个文件各有一个开关」）就变成假的。

反向也要查：GroupXmlPatches 里除了总控，每一项都必须真的带 xmlPatchFile。

本轮（2026-11-16）就是靠这条查出 `xmlCumworksBadCompRef`（【48】）放错组 —— 它在「兼容与判定」里。
"""
import re, os, sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(BASE, '源码', 'FixToggles.cs')
BS = chr(92)
GATE_ID = 'xmlPatchGate'

text = open(CS, 'rb').read().decode('utf-8-sig')
lines = text.split('\n')
mline = next(k for k, l in enumerate(lines) if 'internal static readonly Entry[] All = new Entry[]' in l)
endline = next(k for k in range(mline, len(lines)) if lines[k].strip() == '};')
starts = [k for k in range(mline, endline) if lines[k].lstrip().startswith('new Entry(')]


def _end(s0):
    d, ins, esc = 0, False, False
    for k in range(s0, endline):
        for ch in lines[k]:
            if esc:
                esc = False
                continue
            if ch == BS and ins:
                esc = True
                continue
            if ch == '"':
                ins = not ins
                continue
            if ins:
                continue
            if ch in '([':
                d += 1
            elif ch in ')]':
                d -= 1
                if d == 0 and ch == ')':
                    if lines[k].rstrip().endswith(('),', ')')):
                        return k
    return endline - 1


bad = []
n_with = 0
n_xmlgroup = 0
for s0 in starts:
    e0 = _end(s0)
    blk = '\n'.join(lines[s0:e0 + 1])
    iid = re.search(r'new Entry\("([^"]+)"', blk).group(1)
    g = re.search(r'(Group[A-Z][A-Za-z]*)\s*,', blk).group(1)
    has_file = re.search(r'xmlPatchFile:\s*"([^"]+)"', blk)
    if has_file:
        n_with += 1
        if g != 'GroupXmlPatches':
            bad.append(f'带 xmlPatchFile 但不在 XML 组：{iid}（{has_file.group(1)}）→ {g}')
    if g == 'GroupXmlPatches':
        n_xmlgroup += 1
        if not has_file and iid != GATE_ID:
            bad.append(f'在 XML 组但没有 xmlPatchFile：{iid}')

files = sorted(f for f in os.listdir(os.path.join(BASE, 'Patches')) if f.endswith('.xml'))
print(f'带 xmlPatchFile 的项 : {n_with}')
print(f'GroupXmlPatches 项数 : {n_xmlgroup}（含总控 {GATE_ID}）')
print(f'Patches/*.xml 文件数 : {len(files)}')
print(f'\n==== 分组不一致：{len(bad)} 处 ====')
for b in bad:
    print('  ❌', b)
sys.exit(1 if bad else 0)
