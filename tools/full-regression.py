#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""full-regression.py · 一键跑完本仓库的所有闸门（收工前必跑）。

## 为什么

到目前为止各个检查是**散着跑**的：`final-check` / `tool-health` / `verify_patch` /
`count-numbers` / `docstats` / `static-audit` …… 每次都得记住「跑了哪些、漏了哪个」。
而「漏跑一个」这件事本轮已经发生过 —— 这就是为什么要一键化。

## 设计

脚本**自适应**：按自己所在仓库有哪些工具，跑哪些闸门。同一份代码放进两个仓库都能用。

判据只有一条：**任何一个闸门 exit 非 0 → 整体 exit 1**。
"""
import os, sys, glob, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
PY = sys.executable

# (显示名, 脚本相对路径, 额外参数, 是否收工必跑)
GATES = {
    # ── NMM 汉化 ──
    'NudityMattersMoreOpinions-CN': [
        ('版本/结构/部署一致性', 'tools/final-check.py', [], True),
        ('工具自身健康度', 'tools/tool-health.py', [], True),
        ('汉化补丁完好性', 'tools/verify_patch.py', [], True),
        ('数字权威统计', 'tools/count-numbers.py', [], False),
        ('部署目录同步（dry-run）', 'tools/sync-deploy.py', [], True),
    ],
    # ── LocalFixes ──
    'GNH-LocalFixes': [
        ('开关表一致性', 'tools/verify-toggles.py', [], True),
        ('编号审计', 'tools/audit-numbering.py', [], True),
        ('分组排序', 'tools/check-group-order.py', [], True),
        ('默认关闭表', 'tools/check-default-off.py', [], True),
        ('语言文件编号', 'tools/check-lang-numbers.py', [], True),
        ('XML 分组', 'tools/check-xml-group.py', [], True),
        ('About 速览', 'tools/check-about-list.py', [], True),
        ('注释覆盖', 'tools/audit-comments.py', [], True),
        ('补丁执行结果', 'tools/verify-patches.py', [], True),
        ('success=Always 覆盖', 'tools/audit_success_always.py', [], True),
        ('类型引用', 'tools/audit-type-refs.py', [], True),
        ('新修项', 'tools/check-newfix.py', [], True),
        ('语言键（R22）', 'tools/verify-r22-langkeys.py', [], True),
        ('迁移（R5）', 'tools/verify-r5-migration.py', [], True),
        ('静态审计', 'tools/static-audit.py', [], True),
        ('工具自身健康度', 'tools/tool-health.py', [], True),
        ('文档数字对账', 'tools/docstats.py', ['--check'], True),
    ],
}

name = os.path.basename(REPO)
gates = GATES.get(name)
if gates is None:
    print(f"⚠ 不认识这个仓库：{name}")
    print(f"   已知：{list(GATES)}")
    sys.exit(2)

print(f"{'='*78}")
print(f"全量回归：{name}   共 {len(gates)} 个闸门")
print(f"{'='*78}\n")

passed, failed, skipped = [], [], []
for label, rel, args, must in gates:
    p = os.path.join(REPO, rel)
    if not os.path.exists(p):
        print(f"  ⏭  {label:<24} 缺脚本 {rel}")
        skipped.append(label)
        continue
    r = subprocess.run([PY, p] + args, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', cwd=REPO)
    ok = r.returncode == 0
    mark = '✅' if ok else '❌'
    tail = ''
    if not ok:
        lines = [l for l in (r.stdout or '').split('\n') + (r.stderr or '').split('\n') if l.strip()]
        tail = ('  ← ' + lines[-1][:56]) if lines else ''
    print(f"  {mark} {label:<24} exit={r.returncode}{tail}")
    (passed if ok else failed).append(label)

print(f"\n{'='*78}")
print(f"通过 {len(passed)} / 失败 {len(failed)} / 跳过 {len(skipped)}")
if failed:
    print("\n失败清单：")
    for x in failed:
        print(f"   - {x}")
    sys.exit(1)
if skipped:
    print("\n跳过清单（缺脚本）：")
    for x in skipped:
        print(f"   - {x}")
print("\n✅ 全量回归通过")
sys.exit(0)
