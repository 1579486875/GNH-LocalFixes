#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
扫描「! AutoTranslation AI Pack」里所有**被误译的 defName 引用字段**。

## 为什么要这个

2026-11-27 查明：这个包把 RJW 的 `RacePartDef.hediffName`（一个 **HediffDef 的 defName 引用**）
当成普通文本翻成了中文，导致 700 条
`Could not find a HediffDef named 肛门 referenced by RacePartDef named Anus.`

根因不是「翻译错了一个词」，而是**自动翻译流水线没有排除 defName 引用类字段**。
既然流水线有问题，同类误译很可能不止那一处 —— 这个脚本就是用来把全包扫一遍的。

## 判据

一个 `<DefName>.<fieldPath>` 键，若同时满足：

1. **值是中文**（含 CJK 字符）
2. **fieldPath 的最后一段属于「defName 引用类字段」**

就判定为「疑似误译」，列出来人工确认。

`label` / `description` / `labelShort` / `customLabel` 这些**本来就该是中文**，
不在判据里 —— 这也是为什么不能简单按「值含中文」一刀切。

用法：
    python 工具/扫汉化包defName误译.py                 # 扫默认目录
    python 工具/扫汉化包defName误译.py --write         # 扫并自动修正（按目标 def 的原值）
    python 工具/扫汉化包defName误译.py --dir <路径>    # 指定别的包
"""

import glob
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

DEFAULT_PKG = r'D:/steam/steamapps/common/RimWorld/Mods/!Translation_AI_Pack/Languages/ChineseSimplified/DefInjected'
GAME_ROOT = r'D:/steam/steamapps/common/RimWorld'

# ── defName 引用类字段（判据用；按「最后一段字段名」匹配，大小写不敏感）──
# 收录原则：这个字段的值**必须是某个 Def 的 defName**，翻成中文就一定查不到。
DEFNAME_FIELDS = {
    'defname',
    'hediffname',
    'thingdef',
    'thingdefs',
    'appareldef',
    'appareldefs',
    'buildingdef',
    'weapondef',
    'bodypartdef',
    'bodypartdefs',
    'damagedef',
    'damagedefs',
    'sounddef',
    'effectdef',
    'ingredientdef',
    'fixeddef',
    'texpath',            # 资源路径，翻了必然找不到贴图
    'shaderdef',
    'racepartdef',
    'racepartdefs',
    'filthdef',
    'needdef',
    'jobdef',
    'workgiverdef',
    'thoughtdef',
    'recipeUsers',        # 占位，下面会 lower
    'recipeusers',
    'researchprerequisite',
    'researchprerequisites',
    'requiredresearch',
    'researchdef',
    'statdef',
    'statdefs',
    'capacitydef',
    'genedef',
    'geneclass',
    'xenotypedef',
    'memes',
    'memedeF',
    'preceptdef',
    'ritualpatternDef'.lower(),
    'abilitydef',
    'abilitydefs',
    'verbclass',
    'thingclass',
    'compclass',
    'hediffclass',
    'workerclass',
    'driverclass',
    'relationdef',
    'interactiondef',
    'incidentdef',
    'questscriptdef',
    'rulepackdef',
    'texturables',
    'graphicdata',
    'texpathFemale',
}
DEFNAME_FIELDS = {f.lower() for f in DEFNAME_FIELDS}

# ── 明确「本来就该翻译」的字段，一定不报 ──
SAFE_FIELDS = {
    'label', 'labelshort', 'labelplural', 'description', 'customlabel',
    'jobstring', 'reportstring', 'lettertext', 'text', 'title', 'name',
    'namedef', 'labelnoun', 'verb', 'gerund', 'tip', 'tips', 'rulesstring',
    'descriptionhyperlinks', 'labelfemale', 'labelmale', 'inspectstring',
    'extratooltip', 'fixedname', 'categorylabel',
}


def has_cjk(s):
    return bool(re.search(r'[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]', s))


def field_last_seg(key):
    """`Foo.stages.bar.label` → `label`；`Foo.thingDef` → `thingDef`"""
    return key.rsplit('.', 1)[-1]


def load_target_defs(game_root):
    """从游戏 Data 与 Mods 建 defName 集合（只取 1.6/1.7 或无版本段）。"""
    names = set()
    roots = [
        os.path.join(game_root, 'Data'),
        os.path.join(game_root, 'Mods'),
        os.path.join(game_root, '..', 'workshop', 'content', '294100'),
    ]
    ver_re = re.compile(r'[/\\]1\.[0-6][/\\]')
    for r in roots:
        if not os.path.isdir(r):
            continue
        for f in glob.glob(os.path.join(r, '**', '*.xml'), recursive=True):
            if ver_re.search(f):
                continue
            try:
                t = open(f, encoding='utf-8-sig', errors='ignore').read()
            except Exception:
                continue
            if '<defName>' not in t:
                continue
            for m in re.finditer(r'<defName>([^<]{1,80})</defName>', t):
                names.add(m.group(1).strip())
    return names


def main():
    pkg = DEFAULT_PKG
    write = '--write' in sys.argv
    if '--dir' in sys.argv:
        pkg = sys.argv[sys.argv.index('--dir') + 1]

    print('=' * 78)
    print('扫描：%s' % pkg)
    print('=' * 78)

    files = glob.glob(os.path.join(pkg, '**', '*.xml'), recursive=True)
    print('  文件数：%d' % len(files))

    hits = []      # (file, key, value)
    for f in files:
        try:
            t = open(f, encoding='utf-8-sig', errors='ignore').read()
        except Exception:
            continue
        for m in re.finditer(r'<([A-Za-z0-9_.\-]+)>([^<]+)</\1>', t):
            key, val = m.group(1), m.group(2)
            last = field_last_seg(key).lower()
            if last in SAFE_FIELDS:
                continue
            if last not in DEFNAME_FIELDS:
                continue
            if not has_cjk(val):
                continue
            hits.append((f, key, val))

    print('  疑似误译：%d 条' % len(hits))
    print()
    if not hits:
        print('  ✅ 没有发现 defName 引用类字段被译成中文')
        return

    # 按「字段名」聚合，便于看主要问题在哪
    from collections import Counter
    c = Counter(field_last_seg(k) for _, k, _ in hits)
    print('  按字段聚合：')
    for fld, n in c.most_common():
        print('     %-24s %d 条' % (fld, n))
    print()
    print('  前 20 条明细：')
    for f, k, v in hits[:20]:
        print('     %-44s %-40s %s' % (os.path.basename(f)[:44], k[:40], v[:26]))

    if write:
        print()
        print('════ 自动修正 ════')
        print('  说明：只把「值」改成「键名第一段」（= 目标 Def 的 defName）。')
        print('  若某个字段的原值不是 defName 本身（例如 RacePartDef.hediffName 可能指向别的 def），')
        print('  这个策略会改错 —— 那种情况必须从目标 mod 的 XML 里读原值。')
        ans = input('  确认按此策略修正？(yes/no) ').strip().lower()
        if ans != 'yes':
            print('  已取消')
            return
        by_file = {}
        for f, k, v in hits:
            by_file.setdefault(f, []).append((k, v))
        for f, items in by_file.items():
            raw = open(f, 'rb').read()
            had = raw.startswith(b'\xef\xbb\xbf')
            t = raw.decode('utf-8-sig')
            n = 0
            for k, v in items:
                dn = k.split('.')[0]
                old = '<%s>%s</%s>' % (k, v, k)
                new = '<%s>%s</%s>' % (k, dn, k)
                if old in t:
                    t = t.replace(old, new); n += 1
            data = t.encode('utf-8')
            if had: data = b'\xef\xbb\xbf' + data
            open(f, 'wb').write(data)
            print('  ✅ %-56s %d 条' % (os.path.basename(f)[:56], n))


if __name__ == '__main__':
    main()
