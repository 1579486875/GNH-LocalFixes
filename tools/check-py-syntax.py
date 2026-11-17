# -*- coding: utf-8 -*-
"""闸门：全仓库 .py 的语法与编译告警。

## 为什么单独做一道闸门

`tool-health.py` 只扫 `tools/*.py`（它是「闸门脚本的健康度」，有意限定在那个范围）。
但仓库里还有别处的 Python —— 一次性修复脚本、`_scratch` 下的审计脚本等，
它们出错同样会污染输出、同样会在未来 Python 收紧转义规则后变成 SyntaxError。

## 判据

1. `compile()` 抛 `SyntaxError` → 缺陷（脚本根本跑不了）
2. `compile()` 产生 `SyntaxWarning` → 缺陷（能跑，但输出脏、未来会坏）
3. `DeprecationWarning` → 只提示，不算缺陷

典型来源：docstring 里写 Windows 路径时用单个反斜杠 —— 比如 `Patches` 正上方紧邻反斜杠、`bin<配置>` 前后各一个，只要反斜杠后面不是 `n`/`t`/`\\` 这类合法转义，就是无效转义。
还有一种是反斜杠紧跟中文反引号（Markdown 行内代码）—— 同样非法。

## 用法

    python tools/check-py-syntax.py           # 只报问题
    python tools/check-py-syntax.py -v        # 连通过的一起列

退出码：0 = 无缺陷；1 = 有
"""
import os
import sys
import warnings

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules', '.vs', '_backup', '__pycache__',
             '.venv', 'venv', 'dist'}
SELF = os.path.basename(__file__)


def main():
    verbose = '-v' in sys.argv
    files = []
    for base, dirs, fs in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in fs:
            if f.lower().endswith('.py') and f != SELF:
                files.append(os.path.join(base, f))
    files.sort()

    bad = 0
    warn = 0
    for p in files:
        rel = os.path.relpath(p, ROOT).replace('\\', '/')
        try:
            src = open(p, 'rb').read().decode('utf-8-sig')
        except Exception as e:
            print('  ❌ %-58s 读不了：%s' % (rel, e))
            bad += 1
            continue
        try:
            with warnings.catch_warnings(record=True) as wl:
                warnings.simplefilter('always')
                compile(src, p, 'exec')
        except SyntaxError as e:
            print('  ❌ %-58s 语法错误 line %s: %s' % (rel, e.lineno, e.msg))
            bad += 1
            continue
        sw = [x for x in wl if issubclass(x.category, SyntaxWarning)]
        dw = [x for x in wl if issubclass(x.category, DeprecationWarning)]
        if sw:
            bad += 1
            print('  ❌ %-58s 编译告警 ×%d' % (rel, len(sw)))
            for x in sw[:3]:
                print('       line %s: %s' % (x.lineno, str(x.message)[:88]))
        elif dw:
            warn += 1
            if verbose:
                print('  ⚠ %-58s 弃用告警 ×%d（不算缺陷）' % (rel, len(dw)))
        elif verbose:
            print('  ✅ %-58s' % rel)

    print('\n' + '=' * 74)
    print('扫描 %d 个 .py ／ 缺陷 %d ／ 弃用提示 %d' % (len(files), bad, warn))
    if bad:
        print('❌ 有缺陷 —— 修掉再提交（编译告警虽不影响运行，但 Python 3.12+ 会逐步收紧）'),
        return 1
    print('✅ 全部通过')
    return 0


if __name__ == '__main__':
    sys.exit(main())
