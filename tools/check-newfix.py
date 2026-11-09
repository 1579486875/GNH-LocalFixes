# -*- coding: utf-8 -*-
r"""新增一项修复时，把「必须同步的地方」全部自动检查一遍。

## 为什么有这个东西

2026-11-09 用户报：【44】在测试环境里明明没装 NMM，设置界面却不标「目标模组未启用」。

根因是 `requiresModById` 那张表**是手工登记的**，而【44】从没登记过 ——
界面不知道该去查哪个 packageId，那个状态对它**永远不会出现**。
代码注释里 2026-10-29 已经因为同一原因补过三项，**这是第二次**。

用户的结论是：「以后新加的修复都要适配好，不要再出现这种问题了。」
所以把「新增一项修复要同步的每一处」做成检查，而不是继续靠人记。

## 新增一项修复要同步的 6 处（本脚本查其中 4 处能自动查的）

| # | 位置 | 漏了会怎样 | 本脚本查不查 |
|---|---|---|---|
| 1 | `FixToggles.cs` 的 `new Entry(...)` | 界面上没这一项 | 是（作为基准） |
| 2 | 三语 Keyed（Label + Desc） | 界面显示成键名 | ✅ |
| 3 | **`requiresModById`** | **永远不显示「目标模组未启用」**（本次事故） | ✅ |
| 4 | **补丁类登记表**（`LocalFixesMod.cs`） | `GetTypes()` 整体失败时该补丁静默漏装 | ✅ |
| 5 | `xmlPatchFile:` ↔ `Patches/*.xml` | 开关指向不存在的文件 / 文件没有开关 | ✅ |
| 6 | `About`/`README`/工坊简介/清单图的条数 | 数字对不上 | 交给 `docstats.py` |

## 关于「有没有单一目标模组」这个判断

它是**人工**的 —— 脚本判不出来。所以用一张白名单
`tools/no-single-target.json`：**确认过「确实没有单一目标」的项写进去，带理由**。

规则：
- 在 `requiresModById` 里 → 通过
- 不在表里、但在白名单里 → 通过（白名单里有 reason，看一眼就知道为什么）
- **两边都没有 → 报错**

这样新增修复项时**忘了登记就会被抓出来**，而「刻意不登记」的项有据可查。
"""
import os
import re
import sys
import json

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')
TOGGLES = os.path.join(SRC, 'FixToggles.cs')
MOD = os.path.join(SRC, 'LocalFixesMod.cs')
PATCHES = os.path.join(ROOT, 'Patches')
LANGS = {
    'zh': ('ChineseSimplified',),
    'tr': ('ChineseTraditional',),
    'en': ('English',),
}
WHITELIST = os.path.join(ROOT, 'tools', 'no-single-target.json')


def read(p):
    return open(p, encoding='utf-8-sig').read()


def parse_entries(t):
    """返回 [(id, label)]，按出现顺序。"""
    out = []
    for m in re.finditer(r'new Entry\(\s*"([^"]+)"\s*,\s*(\w+)\s*,\s*"([^"]*)"', t):
        out.append((m.group(1), m.group(3)))
    return out


def parse_requires(t):
    i = t.find('requiresModById')
    j = t.find('};', i)
    return dict(re.findall(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', t[i:j]))


def parse_patch_files(t):
    """xmlPatchFile: "X.xml" → id 列表。Entry 的 xmlPatchFile 在参数里，按块扫。"""
    out = {}
    for m in re.finditer(r'new Entry\(\s*"([^"]+)"[\s\S]*?xmlPatchFile:\s*"([^"]+)"', t):
        out.setdefault(m.group(2), []).append(m.group(1))
    return out


def parse_class_registry(t):
    """LocalFixesMod.cs 里那张补丁类登记表。"""
    m = re.search(r'(?:PatchClass\w*|RegisteredPatch\w*|patchClasses)\s*=\s*(?:new\s+\w+[^{]*)?\{([\s\S]*?)\n\s*\};', t)
    body = m.group(1) if m else ''
    reg = set(re.findall(r'"(GNH\.LocalFixes\.Patch_\w+)"', body))
    # 兜底：整份文件里所有 "GNH.LocalFixes.Patch_Xxx" 字面量
    if len(reg) < 5:
        reg = set(re.findall(r'"(GNH\.LocalFixes\.Patch_\w+)"', t))
    return reg


def main():
    tg = read(TOGGLES)
    md = read(MOD)
    entries = parse_entries(tg)
    requires = parse_requires(tg)
    patchfiles = parse_patch_files(tg)
    clsreg = parse_class_registry(md)

    wl = {}
    if os.path.exists(WHITELIST):
        wl = json.load(open(WHITELIST, encoding='utf-8')).get('items', {})

    print('Entry 总数        : %d' % len(entries))
    print('requiresModById   : %d' % len(requires))
    print('白名单（无单一目标）: %d' % len(wl))
    print('补丁类登记表       : %d' % len(clsreg))
    print('xmlPatchFile 声明  : %d' % len(patchfiles))
    print()

    bad = []

    # ① 三语 Keyed
    keys = {}
    for tag, dirs in LANGS.items():
        s = set()
        for d in dirs:
            p = os.path.join(ROOT, 'Languages', d, 'Keyed', 'GNH_LocalFixes.xml')
            if os.path.exists(p):
                s |= set(re.findall(r'<([A-Za-z][\w.]*)>', read(p)))
        keys[tag] = s
    miss = []
    for eid, _ in entries:
        for tag in LANGS:
            a = 'GNH_LocalFixes_Toggle_' + eid
            b = a + '_Desc'
            if a not in keys[tag]:
                miss.append('%s 缺 %s' % (tag, a))
            if b not in keys[tag]:
                miss.append('%s 缺 %s' % (tag, b))
    if miss:
        bad.append(('三语 Keyed', miss))

    # ② requiresModById / 白名单
    unwrapped = [eid for eid, _ in entries if eid not in requires and eid not in wl]
    if unwrapped:
        bad.append(('既没登记 requiresModById、也不在白名单', unwrapped))

    # ③ xmlPatchFile ↔ Patches
    realfiles = set(os.path.basename(x) for x in os.listdir(PATCHES) if x.endswith('.xml')) \
        if os.path.isdir(PATCHES) else set()
    declared = set(patchfiles)
    if declared - realfiles:
        bad.append(('开关指向不存在的补丁文件', sorted(declared - realfiles)))
    if realfiles - declared:
        bad.append(('补丁文件没有对应开关', sorted(realfiles - declared)))

    # ④ 补丁类登记表 vs 源码里真实存在的 Patch_ 类
    src_classes = set()
    for f in os.listdir(SRC):
        if not f.endswith('.cs'):
            continue
        for m in re.finditer(r'class\s+(Patch_\w+)', read(os.path.join(SRC, f))):
            src_classes.add('GNH.LocalFixes.' + m.group(1))
    if src_classes - clsreg:
        bad.append(('补丁类没登记进登记表（退化路径会漏装）', sorted(src_classes - clsreg)))

    # ── 输出 ──
    if not bad:
        print('✅ 全部通过：新增修复项需要同步的 4 处都齐')
        return 0

    for name, items in bad:
        print('★ %s（%d）' % (name, len(items)))
        for x in items[:20]:
            print('     %s' % x)
        print()
    print('⇒ 修法见本脚本顶部那张表。')
    return 1


if __name__ == '__main__':
    sys.exit(main())
