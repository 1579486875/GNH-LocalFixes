# -*- coding: utf-8 -*-
"""开关完整性交叉校验 —— 加/改开关后必跑。

它回答三个问题（全部不依赖游戏）：
  ① FixToggles.All 里声明的每一项，安装侧是否真的接了线？
  ② 安装侧有没有「接了线但没声明」的孤儿？
  ③ 中英语言文件里，每一项的标题与说明键是否齐全？代码引用的界面键、分组标题键是否都在？

「界面上有开关、实际没接线」这种假开关光看代码很难发现（两处离得远），
所以把这张表与安装点做成自动比对。

用法（在工程根目录）：
    python tools\verify-toggles.py
退出码 0 = 全部对上。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, 'src')


def read(p):
    return open(p, 'rb').read().decode('utf-8-sig')


toggles = read(os.path.join(SRC, 'FixToggles.cs'))
mod = read(os.path.join(SRC, 'LocalFixesMod.cs'))

# 开关 Id 里允许数字：xmlTY2ValleyArtillery 这种带版本号的 id 曾经被
# [A-Za-z]+ 漏掉（实测少算一项，还连带把「没接线」的误报盖掉一个）。
ids = re.findall(r'new Entry\("([A-Za-z0-9_]+)"', toggles)
entry_blocks = toggles.split('new Entry("')[1:]
class_ids = [b.split('"', 1)[0] for b in entry_blocks if 'GNH.LocalFixes.Patch_' in b]
try_ids = re.findall(r'TryInstall\("([a-zA-Z]+)",', mod)

# 分组标题键（FixToggles.cs 里 internal const string GroupXxx = "键"）
group_keys = re.findall(r'internal const string Group\w+ = "(GNH_LocalFixes_Group_\w+)";',
                        toggles)

scc_ids = []
for fn in ('CcoeReflectionFix.cs', 'VehicleFrameworkDebugFix.cs',
           'CumpilationSettingsRestoreFix.cs'):
    scc_ids += re.findall(r'IsFixEnabled\("([a-zA-Z]+)"\)', read(os.path.join(SRC, fn)))

# XML 侧开关不走 TryInstall —— 它们由 XmlPatchGate 在运行时按
# PatchOperation.sourceFile 的文件名拦截（见 XmlPatchGate.cs 的 BuildMap）。
# 判据：Entry 里声明了 xmlPatchFile，且那个文件真的躺在 Patches\ 下；
# 文件没了就等于这个开关在空转。
xml_ids = []
for m in re.finditer(r'new Entry\("([A-Za-z0-9_]+)"(.*?)(?=new Entry\("|\};)',
                     toggles, re.S):
    xf = re.search(r'xmlPatchFile:\s*"([^"]+)"', m.group(2))
    if not xf:
        continue
    if os.path.exists(os.path.join(REPO, 'Patches', xf.group(1))):
        xml_ids.append(m.group(1))
    else:
        print('★ %s 声明的 XML 文件不存在：%s' % (m.group(1), xf.group(1)))

wired = set(try_ids) | set(class_ids) | set(scc_ids) | set(xml_ids)
declared = set(ids)
missing_wire = sorted(declared - wired)
extra_wire = sorted(wired - declared)
dup = sorted(set(x for x in ids if ids.count(x) > 1))

print('声明 %d 项 / 接线 %d 项（TryInstall %d + 补丁类 %d + SCC %d + XML %d）'
      % (len(ids), len(wired), len(try_ids), len(class_ids), len(scc_ids), len(xml_ids)))
print('声明了但没接线 : %s' % (missing_wire or '无'))
print('接线了但没声明 : %s' % (extra_wire or '无'))
print('重复 Id        : %s' % (dup or '无'))

# 三份语言文件必须键集合一致 —— 2026-10-18 补繁中后把语言表抽成常量，
# 免得以后再增语种时又漏改某一处循环（本文件历史上就有两处各自硬编码的列表）。
LANGS = (('简中', 'ChineseSimplified'),
         ('繁中', 'ChineseTraditional'),
         ('英文', 'English'))

bad = 0
for lang, lang_dir in LANGS:
    path = os.path.join(REPO, 'Languages', lang_dir, 'Keyed', 'GNH_LocalFixes.xml')
    keys = set(el.tag for el in ET.fromstring(open(path, 'rb').read()))
    miss_l = [i for i in ids if ('GNH_LocalFixes_Toggle_%s' % i) not in keys]
    miss_d = [i for i in ids if ('GNH_LocalFixes_Toggle_%s_Desc' % i) not in keys]
    miss_g = [k for k in group_keys if k not in keys]
    print('%s语言文件：%d 键；缺标题 %s；缺说明 %s；缺分组键 %s'
          % (lang, len(keys), miss_l or '无', miss_d or '无', miss_g or '无'))
    bad += len(miss_l) + len(miss_d) + len(miss_g)

code_ui = set(re.findall(r'Translated\("(GNH_LocalFixes_\w+)"', mod))
miss_ui = []
for lang, lang_dir in LANGS:
    p_ui = os.path.join(REPO, 'Languages', lang_dir, 'Keyed', 'GNH_LocalFixes.xml')
    ks = set(el.tag for el in ET.fromstring(open(p_ui, 'rb').read()))
    lack = sorted(code_ui - ks)
    if lack:
        miss_ui.append('%s:%s' % (lang, lack))
print('代码引用的界面键 %d 个，语言文件缺 %s' % (len(code_ui), miss_ui or '无'))
print('分组标题键 %d 个：%s' % (len(group_keys), group_keys or '（无）'))

# 值里混进键名（历史脚本替换留下的残渣）—— 玩家能在界面上直接看到
garbage = []
for lang, lang_dir in LANGS:
    path = os.path.join(REPO, 'Languages', lang_dir, 'Keyed', 'GNH_LocalFixes.xml')
    for el in ET.fromstring(open(path, 'rb').read()):
        if el.text and 'GNH_LocalFixes_' in el.text:
            garbage.append('%s:%s' % (lang, el.tag))
if garbage:
    print('★ 值里混进键名（会显示在界面上）：%s' % garbage)
else:
    print('值里混进键名：无')

ok = not (missing_wire or extra_wire or dup or bad or miss_ui or garbage)
print('\n结论：%s' % ('全部对上' if ok else '★ 有问题'))
sys.exit(0 if ok else 1)
