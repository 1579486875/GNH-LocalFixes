# -*- coding: utf-8 -*-
"""开关完整性交叉校验 —— 加/改开关后必跑。

它回答三个问题（全部不依赖游戏）：
  ① FixToggles.All 里声明的每一项，安装侧是否真的接了线？
  ② 安装侧有没有「接了线但没声明」的孤儿？
  ③ 中英语言文件里，每一项的标题与说明键是否齐全？代码引用的界面键是否都在？

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

ids = re.findall(r'new Entry\("([A-Za-z]+)"', toggles)
entry_blocks = toggles.split('new Entry("')[1:]
class_ids = [b.split('"', 1)[0] for b in entry_blocks if 'GNH.LocalFixes.Patch_' in b]
try_ids = re.findall(r'TryInstall\("([a-zA-Z]+)",', mod)

scc_ids = []
for fn in ('CcoeReflectionFix.cs', 'VehicleFrameworkDebugFix.cs',
           'CumpilationSettingsRestoreFix.cs'):
    scc_ids += re.findall(r'IsFixEnabled\("([a-zA-Z]+)"\)', read(os.path.join(SRC, fn)))

wired = set(try_ids) | set(class_ids) | set(scc_ids)
declared = set(ids)
missing_wire = sorted(declared - wired)
extra_wire = sorted(wired - declared)
dup = sorted(set(x for x in ids if ids.count(x) > 1))

print('声明 %d 项 / 接线 %d 项（TryInstall %d + 补丁类 %d + SCC %d）'
      % (len(ids), len(wired), len(try_ids), len(class_ids), len(scc_ids)))
print('声明了但没接线 : %s' % (missing_wire or '无'))
print('接线了但没声明 : %s' % (extra_wire or '无'))
print('重复 Id        : %s' % (dup or '无'))

bad = 0
for lang, path in (('中文', os.path.join(REPO, 'Languages', 'ChineseSimplified', 'Keyed',
                                        'GNH_LocalFixes.xml')),
                   ('英文', os.path.join(REPO, 'Languages', 'English', 'Keyed',
                                        'GNH_LocalFixes.xml'))):
    keys = set(el.tag for el in ET.fromstring(open(path, 'rb').read()))
    miss_l = [i for i in ids if ('GNH_LocalFixes_Toggle_%s' % i) not in keys]
    miss_d = [i for i in ids if ('GNH_LocalFixes_Toggle_%s_Desc' % i) not in keys]
    print('%s语言文件：%d 键；缺标题 %s；缺说明 %s'
          % (lang, len(keys), miss_l or '无', miss_d or '无'))
    bad += len(miss_l) + len(miss_d)

code_ui = set(re.findall(r'Translated\("(GNH_LocalFixes_\w+)"', mod))
zh_keys = set(el.tag for el in ET.fromstring(open(
    os.path.join(REPO, 'Languages', 'ChineseSimplified', 'Keyed', 'GNH_LocalFixes.xml'),
    'rb').read()))
miss_ui = sorted(code_ui - zh_keys)
print('代码引用的界面键 %d 个，语言文件缺 %s' % (len(code_ui), miss_ui or '无'))

ok = not (missing_wire or extra_wire or dup or bad or miss_ui)
print('\n结论：%s' % ('全部对上' if ok else '★ 有问题'))
sys.exit(0 if ok else 1)
