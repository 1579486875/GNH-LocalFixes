#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""C# 方法注释覆盖审计（按模板 §3.2.4 的正确口径，替代会误报的 find-uncommented.py）。

为什么要重写（模板 §3.2.4 第五件事「六种常见误报」，这里踩了第 6 条）：
  旧脚本只认 `///`（XML 文档注释），而且只往上看 140 个字符。
  本项目的注释习惯是 `//`（普通行注释，往往还是一整块多行），于是大量**明明写了注释**的方法
  被判成「缺注释」—— 实测 154 条，几乎全是假阳性。
  按模板的口径：**`//` 与 `///` 都算注释**，向上要一直扫到空行（不截断行数）。

角色分级（模板 §3.2.4 第六件事「最低合格线」）：
  修 bug 的补丁类        → 四段式文件头（不逐方法要求）
  Harmony Prefix/Postfix/Transpiler/Finalizer → 必须有注释，且说清「返回 true/false（或 null/原异常）意味着什么」
  Install()             → 一句「由哪个开关控制、具体修什么见文件头」；**文件头已说明即可放行**
  辅助方法              → 必须有注释，说清做什么、失败时返回什么哨兵值
  基础设施类            → 文件头说明即可，方法可略

排除控制流关键字 —— 旧脚本的另一个坑：`else if (x)` 会被当成「有个叫 if 的方法」。
"""
import glob, os, re, sys, json

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
SRCDIR = os.path.join(BASE, 'src')

SKIP_KW = ('if', 'else', 'for', 'foreach', 'while', 'switch', 'catch', 'using',
           'lock', 'return', 'new', 'do', 'try', 'fixed', 'unsafe', 'checked', 'unchecked')

# 方法声明：可访问修饰符 + 不是控制流关键字 + 有形参表 + 紧随 { 或 =>
DECL = re.compile(
    r'(?m)^(?P<ind>[ \t]*)(?P<mods>(?:(?:public|private|internal|protected|static|virtual|override|sealed|abstract|async|extern|unsafe|partial|new)\s+)+)'
    r'(?P<ret>[\w<>\[\],\.\?]+)\s+(?P<name>[A-Za-z_]\w*)\s*\((?P<args>[^)]*)\)\s*'
    r'(?=\{|=>|$)'
)

# 文件头的小标题：**同义词也算**（模板 §3.2.4 误报第 1 条：只认方括号会把「症状：」判错；
# 本项目更进一步，用的是「问题出在哪」「我们怎么修」这类中文小标题，信息量与四段式等价）
HDR_KEYS = {
    '症状':   ('症状', '问题出在哪', '现象', '出什么事'),
    '根因':   ('根因', '为什么', '原因', '根子'),
    '修法':   ('修法', '怎么修', '怎么改', '做法', '方案'),
    '自动让路': ('自动让路', '让路', '安装时机', '不适用', '兜底'),
}


def has_comment_above(lines, idx):
    """从声明行往上扫，直到空行；途中出现 // 或 /// 就算有注释。"""
    k = idx - 1
    while k >= 0:
        s = lines[k].strip()
        if s == '':
            return False
        if s.startswith('//'):
            return True
        if s.startswith('/*') or '* /' in s or s.endswith('*/'):
            return True
        # 继续往上（同一个注释块的多行属性也算）
        if s.startswith('[') or s.startswith('}') or s.startswith('{') is False and s:
            k -= 1
            continue
        return False
    return False


def file_header_ok(text):
    """四段式的实质：症状 / 根因 / 修法 / 自动让路 四类信息，每类命中一个同义词即可。"""
    head = text[:8000]
    return all(any(w in head for w in words) for words in HDR_KEYS.values())


rows = []
for p in sorted(glob.glob(os.path.join(SRCDIR, '*.cs'))):
    t = open(p, encoding='utf-8-sig').read()
    lines = t.split('\n')
    base = os.path.basename(p)
    hdr = file_header_ok(t)
    for m in DECL.finditer(t):
        name = m.group('name')
        if name in SKIP_KW:
            continue
        line_no = t[:m.start()].count('\n') + 1
        idx = line_no - 1
        ok = has_comment_above(lines, idx)
        role = 'other'
        if name.startswith(('Install', 'TryInstall', 'Uninstall')):
            role = 'install'
        elif name in ('Prefix', 'Postfix', 'Transpiler', 'Finalizer'):
            role = 'harmony'
        elif name.startswith(('Find', 'Resolve', 'Safe', 'Describe', 'Ensure', 'Read', 'Write',
                              'Matches', 'Has', 'Is', 'Get', 'Try', 'Note', 'Report', 'Add', 'Patch')):
            role = 'helper'
        rows.append({'file': base, 'line': line_no, 'name': name, 'role': role,
                     'commented': ok, 'file_header_ok': hdr})

total = len(rows)
commented = sum(1 for r in rows if r['commented'])
print(f'方法总数（已排除控制流关键字）: {total}')
print(f'有注释（// 或 ///）           : {commented}  ({commented*100//max(total,1)}%)')
print(f'文件头四段式齐全              : {sum(1 for f in set(r["file"] for r in rows) if file_header_ok(open(os.path.join(SRCDIR,f),encoding="utf-8-sig").read()))}'
      f' / {len(set(r["file"] for r in rows))} 个文件')

# 按角色看
import collections
byrole = collections.defaultdict(lambda: [0, 0])
for r in rows:
    byrole[r['role']][1] += 1
    if r['commented']:
        byrole[r['role']][0] += 1
print('\n按角色：')
for role, (a, b) in sorted(byrole.items()):
    print(f'  {role:<8} {a}/{b} 有注释')

# 真正需要补的：harmony / helper / other 里没注释的；install 只要文件头有四段式就放行
need = [r for r in rows if not r['commented']
        and not (r['role'] == 'install' and r['file_header_ok'])]
print(f'\n真正需要补注释的：{len(need)} 个')
for r in need:
    print(f"  {r['file']:<38} L{r['line']:<5} [{r['role']}] {r['name']}")

with open(os.path.join(BASE, '_scratch-r346', 'comment-audit.json'), 'w', encoding='utf-8') as f:
    json.dump(rows, f, ensure_ascii=False, indent=1)

sys.exit(0 if not need else 1)
