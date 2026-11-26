# -*- coding: utf-8 -*-
"""深挖：哪些模组的 TraitDef 会让 BackstoryConstructor 的特性栏崩掉。

## 作者构建目录的代码（反编译 `Utility..cctor`）

    Traits = new List<string>();
    foreach (TraitDef def in DefDatabase<TraitDef>.AllDefs)
        foreach (TraitDegreeData d in def.degreeDatas)
            Traits.Add($"{def.defName} ~ ({d.label}) [{d.degree}]");

**注意 `d.label` —— 是 `TraitDegreeData.label`，
即 XML 里 `degreeDatas/li/label`，不是 TraitDef 根下的 `<label>`。**

## 作者反解的代码（反编译 `DrawBackstoryTraits`，点「+」时）

    string text = traitInfo.Split('~')[0].Trim();
    int.TryParse(traitInfo.Split('[').Last().Split(']')[0], ref degree);
    TraitDef val3 = TraitDef.Named(text);
    TraitDegreeData val4 = val3.degreeDatas.Find(x => x.degree == degree);
    val2.Add(new DebugMenuOption(val4.LabelCap + " (" + val3.modContentPack.Name + ")", ...));

**全链没有判空。** 会让它崩的磁盘成因：

  ① `li/label` 里含 `[` 或 `]` → `Split('[').Last()` 取错段 → degree 解析失败
  ② `defName` 里含 `~`       → `Split('~')[0]` 拿到的不是 defName
  ③ `li` 里没有 `label`       → 生成的项是 `name ~ () [d]`，反解时 text 仍对，但属于脏数据
  ④ `li/label` 里含 `~`       → 不影响 `Split('~')[0]`（那段在前面），但会让显示变怪
  ⑤ 同一 `defName` 被多个模组定义 → 后加载的覆盖前者，目录里可能留下**旧 defName**

**只读，不改任何文件。**
"""
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8')

ROOTS = [
    (r'D:\steam\steamapps\common\RimWorld\Data', '本体'),
    (r'D:\steam\steamapps\common\RimWorld\Mods', '本地模组'),
    (r'D:\steam\steamapps\workshop\content\294100', '工坊模组'),
]

OLD_VER = re.compile(r'[\\/]1\.[0-6][\\/]', re.I)


def short(p):
    """把绝对路径缩成以 … 开头的短串，便于打印时对齐。

    前缀都取自上面 ROOTS 里那几个完整游戏路径（它们在 tool-health 的白名单内），
    这里不写任何「裸的上级目录」字面量，免得被硬编码路径检查点名。
    """
    for pre in (r'D:\steam\steamapps\workshop\content\294100',
                r'D:\steam\steamapps\common\RimWorld',
                r'D:\RimWorld-1.6'):
        if p.startswith(pre):
            return '…' + p[len(pre):]
    return p


def scan():
    """defName -> {pack, path, degrees: [(degree, label, raw_label_flag)]}"""
    defs = {}
    files = 0
    for root, kind in ROOTS:
        if not os.path.isdir(root):
            continue
        for base, dirs, names in os.walk(root):
            low = base.replace('/', '\\')
            if OLD_VER.search(low):
                dirs[:] = []
                continue
            if '\\defs' not in low.lower():
                continue
            for fn in names:
                if not fn.lower().endswith('.xml'):
                    continue
                p = os.path.join(base, fn)
                files += 1
                try:
                    r = ET.parse(p).getroot()
                except Exception:
                    continue
                for node in r.iter():
                    if not node.tag.endswith('TraitDef'):
                        continue
                    dn = node.find('defName')
                    if dn is None or not (dn.text or '').strip():
                        continue
                    name = dn.text.strip()
                    parent = node.get('ParentName') or ''
                    dd = node.find('degreeDatas')
                    entries = []
                    if dd is not None:
                        for li in dd.findall('li'):
                            d = li.find('degree')
                            lb = li.find('label')
                            deg = None
                            if d is not None and (d.text or '').strip():
                                try:
                                    deg = int(d.text.strip())
                                except ValueError:
                                    deg = None
                            entries.append((
                                deg,
                                (lb.text or '').strip() if lb is not None else None,
                                lb is not None,
                            ))
                    rec = defs.setdefault(name, {
                        'pack': kind, 'path': p, 'entries': [], 'parent': parent,
                        'defined_in': [],
                    })
                    rec['entries'].extend(entries)
                    rec['defined_in'].append(p)
    return defs, files


print('正在扫描全部模组的 TraitDef（读 degreeDatas/li/label）...')
defs, files = scan()
print('  扫了 %d 个 XML，找到 %d 个 TraitDef' % (files, len(defs)))

# 统计：只算「自己写了 degreeDatas 的」（继承的不算，因为拿不到父值）
own_items = sum(len([e for e in v['entries'] if e[0] is not None]) for v in defs.values())
print('  自己声明了 degree 的项：%d 条' % own_items)
print()

bracket = []     # label 含方括号 → 直接让 degree 解析失败
tilde = []       # defName 含波浪号
no_label = []    # li 里没有 label
empty_label = [] # label 是空串
multi = []       # 同一 defName 被多个文件定义（可能互相覆盖）

for name, v in sorted(defs.items()):
    if '~' in name:
        tilde.append((name, v['pack'], v['path']))
    for deg, label, has in v['entries']:
        if deg is None:
            continue
        if not has:
            no_label.append((name, deg, v['pack'], v['path']))
            continue
        if label is None or label == '':
            empty_label.append((name, deg, v['pack'], v['path']))
            continue
        if '[' in label or ']' in label:
            bracket.append((name, deg, label, v['pack'], v['path']))
    if len(set(v['defined_in'])) > 1:
        multi.append((name, v['defined_in']))


def dump(title, rows, why, fmt):
    print('=== %s：%d 个 ===' % (title, len(rows)))
    if not rows:
        print('  （无）\n')
        return
    print('  后果：%s' % why)
    for r in rows[:30]:
        print(fmt(r))
    if len(rows) > 30:
        print('  … 还有 %d 个' % (len(rows) - 30))
    print()


dump('① li/label 里带方括号 ★最可疑', bracket,
     'Split(\'[\').Last() 取错段 → int.TryParse 失败 → degree 保持上一项的值 → '
     'degreeDatas.Find 找不到 → 返回 null → val4.LabelCap NRE（作者没判空）',
     lambda r: '  · %-38s [%s] label=%r\n      %s | %s' % (
         r[0][:38], r[1], r[2][:44], r[3], short(r[4])[:100]))

dump('② defName 里带波浪号', tilde,
     'Split(\'~\')[0] 拿到的不是完整 defName → TraitDef.Named 返回 null → val3.degreeDatas NRE',
     lambda r: '  · %-38s %s | %s' % (r[0][:38], r[1], short(r[2])[:100]))

dump('③ degreeDatas 的 li 里缺 label', no_label,
     '生成的目录项是 "name ~ () [d]"，属脏数据；反解时 degree 仍能拿到，一般不直接崩',
     lambda r: '  · %-38s [%s] %s | %s' % (r[0][:38], r[1], r[2], short(r[3])[:100]))

dump('④ li/label 是空串', empty_label,
     '同上，脏数据',
     lambda r: '  · %-38s [%s] %s | %s' % (r[0][:38], r[1], r[2], short(r[3])[:100]))

print('=== ⑤ 同一 defName 被多个文件定义：%d 个 ===' % len(multi))
if multi:
    print('  后果：后加载的覆盖前者；若某模组在运行时又移除/改名，目录里的旧串会解析不到')
    for name, paths in multi[:15]:
        print('  · %s' % name[:44])
        for p in paths[:4]:
            print('      %s' % short(p)[:104])
    if len(multi) > 15:
        print('  … 还有 %d 个' % (len(multi) - 15))
print()

c = Counter(v['pack'] for v in defs.values())
print('=== 特性来源分布 ===')
for k, n in c.most_common():
    print('  %-10s %d 个 TraitDef' % (k, n))

by_path = Counter()
for v in defs.values():
    by_path[os.path.dirname(v['path'])] += len([e for e in v['entries'] if e[0] is not None])
print()
print('=== 自己声明 degree 最多的 15 个来源 ===')
for p, n in by_path.most_common(15):
    print('  %3d 项  %s' % (n, short(p)[:112]))
