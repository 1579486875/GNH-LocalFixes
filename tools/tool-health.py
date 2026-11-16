#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""tool-health.py · 自检脚本的「健康度」自检（判据坑清单的自动化版）。

## 为什么要有它

`docs/判据坑清单-20261116.md` 记了 22 种「审计工具自己错」的形态，其中 13 种是本次审查真踩过的。
本脚本把其中**能静态检查的**那几类做成一道闸门，随时可跑：

| 检查项 | 对应清单条目 | 判据 |
|---|---|---|
| **失败时能否报错** | D2 / 总纲 | 有 `sys.exit(<条件>)` / `sys.exit(1)` / `raise SystemExit` / `return 1` + `sys.exit(main())` |
| 硬编码绝对路径 | C1 | 出现 `X:\\...` 且**不在**已知「游戏目录」白名单里 |
| 裸 `except:` | — | `except:` 会吞掉 KeyboardInterrupt / SystemExit |
| `except: pass` | — | 静默失效 |
| 文件末尾无 exit 且无 return | D2 | 双重保险 |

## 用法

    python tools/tool-health.py            # 只报问题，有问题 exit 1
    python tools/tool-health.py -v         # 连「健康」的也列出来

## 已知白名单

游戏目录（`D:\\steam\\...\\RimWorld`）是**应该**硬编码的 —— 它不随仓库位置变。
本机路径白名单写在 `ALLOW` 里，新增时**必须写清理由**。
"""
import os, re, sys, glob, argparse

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# 允许硬编码的路径前缀（游戏目录 / 工坊目录）—— 它们不随仓库位置变
ALLOW = (
    r'D:\steam\steamapps\common\RimWorld',
    r'D:\Steam\steamapps\common\RimWorld',
    r'D:\steam\steamapps\workshop\content\294100',
    r'D:\RimWorld-1.6',
)

ANY_EXIT = re.compile(r'sys\.exit\s*\(')
COND_EXIT = re.compile(r'sys\.exit\s*\([^)]*\bif\b')
LIT_NONZERO = re.compile(r'sys\.exit\s*\(\s*[1-9]\d*\s*\)')
RAISE_SE = re.compile(r'raise\s+SystemExit')
# 任何「可能返回非 0」的 return：return 1 / return 2 / return rc / return result ...
# 只排除 return 0 / return None / return（无值）
RET_ANY = re.compile(r'^\s*return\s+(?!0\s*$)(?!None\s*$)(?!\s*$)\S+', re.M)
SYS_EXIT_MAIN = re.compile(r'sys\.exit\s*\(\s*main\s*\(')
BARE = re.compile(r'except\s*:')                      # 裸 except：真错误（吞 KeyboardInterrupt/SystemExit）
SWALLOW = re.compile(r'except\s+\w[^:]*:\s*(pass|continue)\s*$', re.M)  # 带类型的：仅提示
ABS = re.compile(r'["\']([A-Za-z]:[\\/][^"\']*)["\']')


# 白名单：每条都必须写清「为什么不改」，否则半年后没人知道为什么豁免。
# 键 = 脚本名；值 = 豁免理由。`-v` 模式会把理由打出来。
EXEMPT = {
    'audit-rounds.py':
        '输出的是审查轮次的时间线报告，不是错误检查 —— 没有「失败」这个概念',
    'audit-type-refs.py':
        '硬编码的是扫描根目录 E:\\TAML\\_scratch\\…，属于外部输入',
    'audit_pkgid_case.py':
        '硬编码的是系统固定路径 %LocalLow%\\…\\ModsConfig.xml 与 RimModManager 的 profile 目录',
    'audit_status_sim.py':
        '它是模拟器（喂假数据看输出），不是检查器',
    'static-audit.py':
        '硬编码里那个正则 E:\\\\TAML|E:/TAML 是它自己的扫描模式（查别人硬编码用），不能改',
}


def has_exit_verdict(t):
    """能不能因为「检查不通过」而非 0 退出。"""
    if COND_EXIT.search(t) or LIT_NONZERO.search(t) or RAISE_SE.search(t):
        return True, '有条件 exit'
    if SYS_EXIT_MAIN.search(t) and RET_ANY.search(t):
        return True, 'main() 有非 0 返回 + sys.exit(main())'
    if ANY_EXIT.search(t):
        return False, '有 exit 但不接条件（永远成功）'
    return False, '完全没有 exit'


def allowed(p):
    p = p.replace('/', '\\')
    return any(p.lower().startswith(a.replace('/', '\\').lower()) for a in ALLOW)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('-v', '--verbose', action='store_true')
    a = ap.parse_args()

    files = sorted(glob.glob(os.path.join(HERE, '*.py')))
    # 自己不算
    files = [f for f in files if os.path.basename(f) != 'tool-health.py']

    bad = []
    warned = []
    print(f"检查 {len(files)} 个脚本（{REPO}）\n")
    for p in files:
        name = os.path.basename(p)
        if name in EXEMPT:
            if a.verbose:
                print(f"  ➖ {name}  豁免：{EXEMPT[name]}")
            continue
        t = open(p, encoding='utf-8-sig', errors='replace').read()
        problems = []

        ok, how = has_exit_verdict(t)
        # 只对「检查类」脚本要求 exit 判据；fix-/add-/renumber- 这类一次性脚本不要求
        checker = re.match(r'(check-|verify-|audit|static-|docstats|tool-)', name) is not None
        if checker and not ok:
            problems.append(f'检查类脚本但没有 exit 判据（{how}）')

        if BARE.search(t):
            problems.append(f'**裸 except** ×{len(BARE.findall(t))}（会吞 KeyboardInterrupt/SystemExit）')
        if SWALLOW.search(t):
            warned.append(f'except Exception: pass/continue ×{len(SWALLOW.findall(t))}（建议加跳过计数）')

        abs_hits = [x for x in ABS.findall(t) if not allowed(x)]
        if abs_hits:
            problems.append('硬编码路径（非游戏目录）：' + '；'.join(abs_hits[:2]))

        if problems:
            bad.append(name)
            print(f"  ❌ {name}")
            for x in problems:
                print(f"       - {x}")
        elif a.verbose:
            print(f"  ✅ {name}  ({how})")
        if warned and a.verbose:
            for w in warned:
                print(f"  ⚠ {name}: {w}")
            warned = []

    print(f"\n{'='*60}")
    print(f"有问题的脚本：{len(bad)} / {len(files)}")
    if bad:
        for x in bad:
            print(f"   - {x}")
        sys.exit(1)
    print("全部通过")
    sys.exit(0)


if __name__ == '__main__':
    main()
