# -*- coding: utf-8 -*-
"""闸门：译文里不许出现英文半角双引号（中文排版惯例，见《边缘世界本地化翻译提示词》第七步）。

## 判据出处

> 第七步：把译文内容里的英文半角双引号 `"`，全部替换为中文全角双引号 `“` 和 `”`，
> 不管是单个的 `"` 还是两个连续的 `""`，都要替换。这是 RimWorld 汉化的通行惯例。

## 为什么要做成闸门

424,072 个文本节点里漏一个，肉眼绝对查不出来；而它又不报错、不影响加载，
只有玩家在游戏里读到那一句时才看得出别扭 —— 属于「一旦退化就再也回不去」的一类。

## 只扫「我们产出的」语言目录

`Languages/`、`OwnInjections/`、`v1.7/Languages/`。
**不扫**英文源文件（英文原文里的 `"` 是对的）。

## 已知合理例外（写进白名单，必须逐条给理由）

`ALLOW_SNIPPET` 里列的是「值里包含代码/XML 片段」的情形 —— 那些片段里的半角引号
是语法的一部分，改成全角会破坏可读性甚至语义。

## 用法

    python 工具/check-quote-style.py            # 报告
    python 工具/check-quote-style.py -v         # 连通过的文件也列

退出码：0 = 干净；1 = 有半角引号
"""
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOTS = ['Languages', 'OwnInjections', os.path.join('v1.7', 'Languages')]

# 只看中文目录：`Languages/English/` 之类是源语言原文，那里的半角引号是对的。
# 判据：路径里出现 Chinese 才扫。
ONLY_ZH = True

# 值里出现这些片段时，允许保留半角引号（代码 / XML 属性 / 正则）
ALLOW_SNIPPET = re.compile(
    r'(Scribe_\w+\.Look|Class="|<\w+[^>]*="|\bref\s+\w+\s*,|'
    r'\bnew\s+\w+\(|\bstring\.\w+\(|\.\w+\(\s*")')

TAGVAL = re.compile(r'<([A-Za-z_][\w.\-]*)>([^<]*)</\1>')


def main():
    verbose = '-v' in sys.argv
    files = []
    for r in ROOTS:
        base = os.path.join(REPO, r)
        if not os.path.isdir(base):
            continue
        for b, _, fs in os.walk(base):
            if ONLY_ZH and 'chinese' not in b.lower():
                continue
            for f in fs:
                if f.lower().endswith('.xml'):
                    files.append(os.path.join(b, f))
    files.sort()

    bad = 0
    nodes = 0
    allowed = 0
    for p in files:
        rel = os.path.relpath(p, REPO).replace('\\', '/')
        t = open(p, 'rb').read().decode('utf-8-sig', errors='ignore')
        hits = []
        for m in TAGVAL.finditer(t):
            nodes += 1
            val = m.group(2)
            if '"' not in val:
                continue
            if ALLOW_SNIPPET.search(val):
                allowed += 1
                continue
            ln = t[:m.start()].count('\n') + 1
            hits.append((ln, m.group(1), val[:70]))
        if hits:
            bad += 1
            print('  ❌ %s  半角引号 ×%d' % (rel, len(hits)))
            for ln, k, v in hits[:6]:
                print('       line %d  <%s>  %s' % (ln, k, v))
        elif verbose:
            print('  ✅ %s' % rel)

    print('\n' + '=' * 74)
    print('扫描 %d 个文件 / %d 个文本节点' % (len(files), nodes))
    print('白名单放行（代码或 XML 片段）：%d' % allowed)
    if bad:
        print('❌ %d 个文件里还有半角双引号 —— 按翻译提示词第七步改成 “ ”' % bad)
        return 1
    print('✅ 全部为全角引号（或属于已登记例外）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
