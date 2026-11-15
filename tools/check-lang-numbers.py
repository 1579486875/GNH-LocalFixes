#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""核查「三份语言文件里每个 Toggle Title 的编号」与「FixToggles.cs 的 Label 编号」是否一致。

为什么必须查（模板 §1.1「文档里那些行为断言也要核」）：
  `LocalFixesMod.DrawToggleList` 绘制标题走 `Translated(LabelKeyOf(id), e.Label)` ——
  **语言键能翻译就用语言值**，C# 的 Label 只是兜底。
  所以界面上真正显示的标题来自语言文件。语言文件里的编号一旦缺失或写错，
  玩家看到的编号就与 README / About / 日志里的不是同一套，表现就是「编号在跳号」。

判据：
  · C# 里没有编号的项（诊断工具、deepProfilerQuiet、xmlPatchGate）→ 语言标题**必须没有**编号；
  · C# 里有编号的项 → 语言标题**必须且只能有**同一个编号。
  中文两份统一写「（【N】）」或「（…【N】）」；英文统一写「(#N)」。

退出码：0 全对；1 有偏差。
"""
import re, os, sys
import xml.etree.ElementTree as ET

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(BASE, 'src', 'FixToggles.cs')
LANGS = [
    ('简中', os.path.join(BASE, 'Languages', 'ChineseSimplified', 'Keyed', 'GNH_LocalFixes.xml')),
    ('繁中', os.path.join(BASE, 'Languages', 'ChineseTraditional', 'Keyed', 'GNH_LocalFixes.xml')),
    ('英文', os.path.join(BASE, 'Languages', 'English', 'Keyed', 'GNH_LocalFixes.xml')),
]

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
    blk = '\n'.join(lines[s0:e0 + 1])
    strs = re.findall(r'"((?:[^"\\]|\\.)*)"', blk)
    iid, lab = strs[0], (strs[1] if len(strs) > 1 else '')
    m = re.search(r'【(\d+)】', lab)
    CSNUM[iid] = (int(m.group(1)) if m else None, lab)

# 任何写法的编号 token（中文/英文/历史遗留）
TOK = re.compile(r'【(\d+)】|\(#(\d+)\)|\[(\d+)\]|\(§(\d+)\)|\((\d{1,2})\)\s*$')


def nums_in(v):
    out = []
    for m in TOK.finditer(v):
        g = next(x for x in m.groups() if x is not None)
        out.append(int(g))
    return out


bad = []
total = 0
for name, path in LANGS:
    root = ET.fromstring(open(path, 'rb').read().decode('utf-8-sig'))
    kv = {el.tag: (el.text or '') for el in root}
    print(f"[{name}] {len(kv)} 个键")
    for iid, (cs, lab) in CSNUM.items():
        key = f'GNH_LocalFixes_Toggle_{iid}'
        if key not in kv:
            bad.append((name, iid, '缺标题键', ''))
            continue
        total += 1
        got = nums_in(kv[key])
        if cs is None:
            if got:
                bad.append((name, iid, f'不该有编号，却写了 {got}', kv[key]))
        else:
            if got != [cs]:
                bad.append((name, iid, f'应为 {cs}，实际 {got}', kv[key]))

print(f"\n共核对 {total} 个标题键")
print(f"==== 编号偏差：{len(bad)} 处 ====")
for b in bad:
    print(f"  [{b[0]}] {b[1]}: {b[2]}")
    print(f"        {b[3]}")
sys.exit(1 if bad else 0)
