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
        # 2026-11-16 加（审查 D-02）：模板 §1.1 要求「版本号三处一致」。
        # 实测 About 1.4.63 / csproj 1.4.57 / DLL 1.4.57.0 —— 最近 6 次改版只改了 About。
        ('版本号一致性', 'tools/check-version-consistency.py', [], True),
        ('开关表一致性', 'tools/verify-toggles.py', [], True),
        # 2026-11-16 加（审查 §1.1「开关存在 ≠ 开关有效」）：
        # 逐个核对 76 个开关是否真的连到实现 —— patchClassName / xmlPatchFile / TryInstall 三条路。
        # 第一版拿 id 字面量 grep，误报 30 个；正确口径见脚本头部注释。
        ('开关接线', 'tools/audit-toggle-wiring.py', [], True),
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
        # 2026-11-17 本轮加：模板 §B.17.4「两份同名语言目录只同步了一半」。
        # 反编译 1.7.5011.24 的 Verse.LoadedLanguage 实证：AllDirectories 先试
        # folderName（= Prefs.langFolderName = "ChineseSimplified (简体中文)"），
        # 命中就 continue，永不回退 legacyFolderName。
        # 本仓库目前只有 `Languages/ChineseSimplified` 一套，靠 legacy 回退命中 ——
        # 能用，但这是**唯一**的加载路径；将来若补一份带括号的，两套必须一致。
        ('语言目录对一致性（B.17.4）', 'tools/check-dup-langdirs.py', [], True),
        # 2026-11-17 本轮加：requiresModById 指向的 packageId 必须真的存在。
        # 写成死 id 的开关永远不会激活，界面上也不会提示「目标未安装」——
        # 等于一条静默消失的修复。这类错不报红字、不崩，只能靠清单核对。
        ('前置模组存在性', 'tools/check-requires-mods.py', [], True),
        # 2026-11-17 本轮加：全仓库 .py 的语法与编译告警。
        # 起因：`verify-patches.py` / `audit-toggle-wiring.py` / `update-template-1818.py`
        # 三个脚本的 docstring 里写了单个反斜杠 + 非法转义字符（Windows 路径、Markdown 反引号），
        # 每次运行先打一条 SyntaxWarning。不影响功能，但污染闸门输出，且 Python 3.12+ 逐步收紧。
        # 2026-11-17 本轮加：XML 补丁里 defName="X" 的引用目标在 1.7 / 1.6 两个环境是否可达。
        # verify-patches.py 只查 XML 结构，**不查「这个 defName 到底有没有」** ——
        # XPath 匹配到 0 个节点不是错误，补丁会静默不生效，日志里一个字都没有。
        # 两级判据：1.7 下缺 = 缺陷；1.6 下缺 = 提示（1.6 副本只有 339 个模组目录）。
        ('补丁目标可达性', 'tools/check-patch-targets.py', [], True),
        ('Python 语法（全仓库）', 'tools/check-py-syntax.py', [], True),
        # 2026-11-17 本轮加：《边缘世界本地化翻译提示词》第七步 ——
        # 译文里的英文半角双引号必须换成全角 “ ”。424k 个文本节点里漏一个肉眼查不出来，
        # 它不报错、不影响加载，只有玩家读到那句时才看得出别扭。
        # 只扫中文目录；值里含代码 / XML 属性片段的按白名单放行（半角在那里是语法的一部分）。
        ('译文引号风格（全角）', 'tools/check-quote-style.py', [], True),
        # 2026-11-17 本轮加：仓库 ↔ 两个部署目录（1.7 主安装 / 1.6 引擎副本）逐文件比对。
        # 起因：本轮改了 README 却没部署，全量回归却全绿 —— 「改了仓库忘同步」是最容易漏的一步。
        ('部署目录一致性（--check）', 'tools/deploy-dll.py', ['--check'], True),
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
