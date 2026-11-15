# -*- coding: utf-8 -*-
"""
R5：把 C# 的迁移逻辑与界面联动**逐行翻译成 Python 跑一遍**，验证 8 种真实配置下的行为。

这样做的好处：不用开游戏就能覆盖「旧配置 / 新配置 / 手工编辑 / 空配置」这些
靠实机很难逐个复现的组合。

对照的 C# 代码：
  GNHLocalFixesSettings.MigrateAllDiagnosticsOnce()   （迁移）
  GNHLocalFixesSettings.IsOn(id)                      （读）
  LocalFixesMod.IsFixEnabled(id)                      （运行时语义，抬升已删）
  LocalFixesMod.AllDiagnosticsOn(settings)            （总控显示值）
  LocalFixesMod.DrawToggleList 里的点击分支            （界面联动）
"""

DIAG = ["ew2TranspilerProbe", "pawnGenDeathProbe", "parsedIntLocator", "rjwOrgasmProbe"]
DEFAULT_OFF = {"deepProfilerQuiet", "allDiagnostics"} | set(DIAG)


def is_on(toggles, id):
    """对应 C# GNHLocalFixesSettings.IsOn"""
    if id in toggles:
        return toggles[id]
    return id not in DEFAULT_OFF


def migrate(toggles):
    """对应 C# MigrateAllDiagnosticsOnce（原地修改）"""
    if not toggles:
        return toggles
    if "allDiagnostics" not in toggles or not is_on(toggles, "allDiagnostics"):
        return toggles
    if any(d in toggles for d in DIAG):
        return toggles
    for d in DIAG:
        toggles[d] = True
    return toggles


def all_diag_on(toggles):
    """对应 C# AllDiagnosticsOn —— 总控的显示值"""
    return all(is_on(toggles, d) for d in DIAG)


def click(toggles, entry_id, new_value):
    """对应 C# DrawToggleList 的点击分支（全选语义）"""
    if entry_id == "allDiagnostics":
        for d in DIAG:
            toggles[d] = new_value
        toggles["allDiagnostics"] = new_value
    elif entry_id in DIAG:
        toggles[entry_id] = new_value
        toggles["allDiagnostics"] = all_diag_on(toggles)
    else:
        toggles[entry_id] = new_value
    return toggles


def show(name, toggles):
    master = "☑" if all_diag_on(toggles) else "☐"
    subs = " ".join("☑" if is_on(toggles, d) else "☐" for d in DIAG)
    print("  %-34s 总控 %s   子项 %s" % (name, master, subs))


CASES = [
    ("① 空配置（首次安装）", {}),
    ("② 旧配置：只有总控=true", {"allDiagnostics": True}),
    ("③ 新配置：总控+四子都 true", {"allDiagnostics": True, **{d: True for d in DIAG}}),
    ("④ 玩家手动全关", {"allDiagnostics": False, **{d: False for d in DIAG}}),
    ("⑤ 只勾了总控、四子 false（异常态）", {"allDiagnostics": True, **{d: False for d in DIAG}}),
    ("⑥ 只开一条诊断", {**{d: False for d in DIAG}, "parsedIntLocator": True}),
    ("⑦ 只有别的设置项", {"keepLeaderTitle": False}),
    ("⑧ 旧配置 + 其他设置", {"allDiagnostics": True, "keepLeaderTitle": False}),
]

print("=" * 92)
print("R5：迁移 + 界面联动的状态机推演")
print("=" * 92)
ok = True
for name, cfg in CASES:
    t = dict(cfg)                       # 读盘
    migrate(t)                          # PostLoadInit 迁移
    show(name + "（迁移后）", t)
    # 关键动作：点一次总控想「全关」
    for on in (True, False):
        # 先把状态摆成「总控显示为 on」再点
        if all_diag_on(t) == on:
            click(t, "allDiagnostics", not on)
            want = not on
            actual = all(is_on(t, d) == want for d in DIAG) and is_on(t, 'allDiagnostics') == want
            mark = "✓" if actual else "✗"
            if not actual:
                ok = False
            print("      点总控 → %s：子项%s   %s" % (
                "全开" if want else "全关",
                "全对" if actual else "不对", mark))
            break
print()
print("=" * 92)
print("总体：%s" % ("全部符合预期 ✅" if ok else "**有场景不符合**"))
print("=" * 92)

# 专项：迁移不该误伤
print("\n迁移的边界：")
for name, cfg, should in [
    ("空配置", {}, False),
    ("只有总控=true", {"allDiagnostics": True}, True),
    ("总控=true 且已有子项键", {"allDiagnostics": True, "ew2TranspilerProbe": True}, False),
    ("总控=false", {"allDiagnostics": False}, False),
    ("总控没记录", {"keepLeaderTitle": False}, False),
]:
    t = dict(cfg)
    before = dict(t)
    migrate(t)
    changed = t != before
    ok2 = changed == should
    print("  %-30s 改动=%-5s 应为=%-5s %s" % (name, changed, should, "✓" if ok2 else "✗"))
