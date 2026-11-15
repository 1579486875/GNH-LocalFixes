#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""R346-R7：按当前实现重写 About.xml 的「修复速览」清单。

为什么必须重写（本轮实测）：
  About.xml 的速览是玩家在模组管理器里读到的第一手说明，编号必须与设置界面一一对应。
  逐条核对发现 8 行**内容与同号实现完全不是一回事**（旧编号体系遗留）：
    【1】About 写「ElToro「观看」任务缺类」，实际是「坏程序集不再拖垮整批补丁」
    【4】About 写「载具面板拿错 Def 名去查」，实际是「XML 补丁按名字判定 DLC 失效」
    【12】About 写「中文下 Royalty 判定失效」，实际是「音乐淡出每帧报错」
    【22】About 写「近战动画补丁整批失败」，实际是「Steam 工坊信息崩溃」
    【23】About 写「三处开发者模式自检噪音」，实际是「Vehicle Framework 调试菜单报错」
    【33】【34】【35】About 仍在写 ConfigErrorCleanup.xml 内部的三个小节，而那三节早已合并成【33】一项
  另有 8 行措辞不同但语义一致（CeleTech / 植入体 / NMM / Onahole / SRCELib / PE / RI / CrossPromotion），
  一并按实现的措辞统一，避免以后再次漂移。

做法：**从 FixToggles.All 的 Label 直接生成**（去掉尾部「（【N】）」再放到行首），
以后编号或措辞一变，重跑本脚本即可，不再有第二份手写清单可以漂移。
"""
import re, os, sys
import xml.etree.ElementTree as ET

BASE = r"E:\TAML\modfix\GNH-LocalFixes"
CS = os.path.join(BASE, r"src\FixToggles.cs")
AB = os.path.join(BASE, r"About\About.xml")

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

rows = []      # (num, 纯标题)
unnum = []     # 无编号的修复项 id/label
for s0 in starts:
    e0 = _end(s0)
    blk = '\n'.join(lines[s0:e0+1])
    strs = re.findall(r'"((?:[^"\\]|\\.)*)"', blk)
    iid, lab = strs[0], (strs[1] if len(strs) > 1 else '')
    grp = re.search(r'(Group[A-Z][A-Za-z]*)\s*,', blk).group(1)
    m = re.search(r'【(\d+)】', lab)
    if not m:
        if grp != 'GroupDiagnostics' and iid != 'deepProfilerQuiet':
            unnum.append((iid, lab))
        continue
    n = int(m.group(1))
    # 【只删编号本身，不碰括号】—— 编号可能长在括号里（「（第二道保险【22】）」），
    # 早先一版把「（源头拦截【11】）」整段吃掉，生成了「（源头拦截」这种未闭合的半截括号。
    clean = re.sub(r'\s*【\d+】\s*', '', lab).strip()
    # 只剩一对空括号的（「…（【44】）」→「…（）」）整对删掉
    clean = re.sub(r'[（(]\s*[）)]', '', clean).strip()
    rows.append((n, clean))

rows.sort(key=lambda x: x[0])
print(f"生成 {len(rows)} 条有编号的速览行；无编号的修复项 {len(unnum)} 个：{[u[0] for u in unnum]}")

body = []
body.append('■ 修复速览（66 条，编号与设置界面一一对应）')
body.append('')
for n, clean in rows:
    body.append(f'【{n}】{clean}')
for iid, lab in unnum:
    body.append(f'{lab}（总控，不占编号）')
body.append('')
body.append('⚠ 这份清单是**从代码里的开关表直接生成的**（tools 下的脚本），不是手写维护的第二份 ——')
body.append('   编号或措辞一改，重跑一次就同步，不会再出现「文档说的和实际装的不是一条」。')
body.append('')

newseg = '\n'.join(body)

ab_raw = open(AB, 'rb').read()
bom = ab_raw.startswith(b'\xef\xbb\xbf')
ab = ab_raw.decode('utf-8-sig')
nl = '\r\n' if '\r\n' in ab else '\n'

# 定位「■ 修复速览」到「■ 怎么用」之间
m = re.search(r'(■ 修复速览.*?)(?=■ 怎么用)', ab, re.S)
assert m, "找不到速览段"
old = m.group(1)
# 统一换行
newseg_nl = newseg.replace('\n', nl) + nl
ab2 = ab[:m.start(1)] + newseg_nl + ab[m.end(1):]

# 顺便修那句过时的「唯一没有开关的是【1】」
old_line = [ln for ln in ab2.split(nl) if '唯一没有开关的是' in ln]
assert len(old_line) == 1, old_line
ab2 = ab2.replace(old_line[0],
    '★ 每一份修复在设置界面里都有独立开关（XML 补丁那 22 个由组末的总控统一放行）。'
    '没有「只能靠删文件才能关掉」的项。')

with open(AB, 'wb') as f:
    f.write((b'\xef\xbb\xbf' if bom else b'') + ab2.encode('utf-8'))

after = open(AB, 'rb').read()
assert after.startswith(b'\xef\xbb\xbf') == bom
ET.fromstring(after.decode('utf-8-sig').encode('utf-8'))
print(f"✅ About.xml 已重写：速览段 {len(old)} 字符 → {len(newseg_nl)} 字符；BOM={bom}；ET 可解析")
