# -*- coding: utf-8 -*-
r"""闸门：XML 补丁里 `defName="X"` 引用的目标，在 1.7 与 1.6 两个环境里是否存在。

## 为什么

`verify-patches.py` 只查 XML 结构（注释里的 `--`、`MayRequire` 守卫、`success` 等），
**不查「这个 defName 到底有没有」**。而写在 XPath 里的 `defName="X"` 如果目标不存在，
补丁会**静默不生效**（`PatchOperation` 的 XPath 匹配到 0 个节点不是错误）。

这类问题：
- 不报红字（除非补丁带 `success="Normal"`）
- 不进 `loadErrors`
- 只有人拿着清单一条条核才发现

## 两级判据（这是本脚本的关键设计）

| 情况 | 判定 | 理由 |
|---|---|---|
| **1.7 下找不到** | ❌ 缺陷 | 1.7 是主环境，目标模组装在那儿 |
| **1.6 下找不到** | ⚠ 提示 | 1.6 副本只有 339 个模组目录，缺的多数是「1.6 副本里根本没装那个模组」 |

把 1.6 的缺失一律当缺陷会产出大量假阳性；一律不管又会漏掉真的跨版本改名。

## 用法

    python tools/check-patch-targets.py            # 只报缺陷 + 1.6 提示汇总
    python tools/check-patch-targets.py -v         # 逐条列出

退出码：0 = 1.7 下全部可达；1 = 1.7 下有缺失
"""
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATCHES = os.path.join(REPO, 'Patches')

ENVS = {
    '1.7': [r'D:\steam\steamapps\common\RimWorld\Data',
            r'D:\steam\steamapps\common\RimWorld\Mods',
            r'D:\steam\steamapps\workshop\content\294100'],
    '1.6': [r'D:\RimWorld-1.6\Data',
            r'D:\RimWorld-1.6\Mods'],
}

DEFNAME = re.compile(r'<(?:defName|Name)>\s*([^<\s]+)\s*</(?:defName|Name)>')
REF = re.compile(r'defName\s*=\s*"([^"]+)"')


def index(roots):
    names = set()
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dp, dn, fns in os.walk(root):
            # 版本段过滤：只认当前版本目录（模板 §24）
            low = dp.replace('/', '\\').lower()
            if any(('\\%s\\' % v) in low + '\\'
                   for v in ('1.3', '1.4', '1.5')) and '\\1.6\\' not in low and '\\1.7\\' not in low:
                continue
            for fn in fns:
                if not fn.lower().endswith('.xml'):
                    continue
                try:
                    t = open(os.path.join(dp, fn), 'rb').read().decode('utf-8-sig', errors='ignore')
                except Exception:
                    continue
                for m in DEFNAME.finditer(t):
                    names.add(m.group(1))
    return names


def main():
    verbose = '-v' in sys.argv
    # 2026-11-17 加：环境缺失按 skip 处理（exit 0），不按失败处理。
    # 判据依赖本机装没装 RimWorld。按失败处理会让换台机器 / 别人的 CI 永远红 ——
    # 那不是代码问题。但必须明确打印「本项未核」，不能静默通过。
    if not os.path.isdir(ENVS['1.7'][0]):
        print('⏭ 跳过：找不到 1.7 游戏目录')
        print('   %s' % ENVS['1.7'][0])
        print('   本项**未核**（不是通过）。')
        return 0
    files = sorted(f for f in os.listdir(PATCHES) if f.lower().endswith('.xml'))
    refs = {}
    for f in files:
        t = open(os.path.join(PATCHES, f), 'rb').read().decode('utf-8-sig')
        for m in REF.finditer(t):
            refs.setdefault(m.group(1), set()).add(f)

    print('Patches：%d 个文件 / %d 个唯一 defName 引用' % (len(files), len(refs)))
    idx = {}
    for env, roots in ENVS.items():
        idx[env] = index(roots)
        print('  %s 环境 defName 索引：%d' % (env, len(idx[env])))

    bad7, miss6 = [], []
    for name in sorted(refs):
        if name not in idx['1.7']:
            bad7.append(name)
        if name not in idx['1.6']:
            miss6.append(name)

    print('\n=== 1.7 下找不到（缺陷）===')
    if bad7:
        for n in bad7:
            print('  ❌ %-46s ← %s' % (n, ', '.join(sorted(refs[n]))))
    else:
        print('  ✅ 全部 %d 个都能在 1.7 环境里找到' % len(refs))

    print('\n=== 1.6 下找不到（提示 —— 多半是 1.6 副本没装那个模组）===')
    print('  共 %d 个' % len(miss6))
    if verbose:
        for n in miss6:
            print('  · %-46s ← %s' % (n, ', '.join(sorted(refs[n]))))
    elif miss6:
        print('  ' + ', '.join(miss6[:12]) + ('…' if len(miss6) > 12 else ''))

    print('\n' + '=' * 74)
    if bad7:
        print('❌ 1.7 下有 %d 个 defName 引用不可达 —— 那些补丁会静默不生效' % len(bad7))
        return 1
    print('✅ 1.7 下全部可达')
    return 0


if __name__ == '__main__':
    sys.exit(main())
