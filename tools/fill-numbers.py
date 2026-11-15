# -*- coding: utf-8 -*-
"""
给 26 个「界面无编号」的修复项补上 About.xml 里对得上的编号，并修掉【18】的重复。

映射依据：逐项比对 **About.xml 的标题** 与 **FixToggles 的标签+说明**，
只补**语义确定**的；确定不了的留在列表里人工确认（宁可不编，也不瞎编）。

18 的重复：`rjwOrgasmPacing` 写了【18】，但 About 的 18 是「金鸢尾兰沙皇变智人」
（= `xmlOasfcTsarXenotype`，它也是【18】）。`rjwOrgasmPacing` 在 About 里
**没有对应条目**（后加的项），所以去掉它的编号。

用法：python tools/fill-numbers.py [--write]
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
P = os.path.join(BASE, "src", "FixToggles.cs")

# Entry Id → 编号（语义确定的那批）
ASSIGN = {
    # 2  CeleTech 射程计算不判空
    "patchVerbPropertiesAdjustedRange": 2,
    # 4  载具面板拿错 Def 名去查  ← 「按名字判定 DLC 失效」是同一类根因
    "patchModListerHasActiveModWithName": 4,
    # 5  VFE 乐器空间空引用
    "fixVfeeInstrumentSpace": 5,
    # 6  CrossPromotion 调废弃接口
    "crossPromotionUnpatch": 6,
    # 9  CCOE 反射抛异常
    "ccoeReflection": 9,
    # 10 植入体零权重崩溃
    "techHediffsZeroBudget": 10,
    # 11 非乐器混进乐器分组
    "patchListerThingsRejectMislabeledInstrument": 11,
    # 14 XmlExtensions FindMod 静默跳过
    "xmlExtensionsFindMod": 14,
    # 15 领袖头衔自己变回去
    "keepLeaderTitle": 15,
    # 16 NMM 跨局残留撞号
    "nmmIdeoTrackerNull": 16,
    # 17 Onahole mimic 刷警告
    "baseGenMimicSpawnerQuiet": 17,
    # 20 Allies are Helpful 空引用
    "alliesAreHelpfulCurJobNull": 20,
    # 21 RJW 基因调试刷屏
    "rjwGenesSpamQuiet": 21,
    # 23 三处开发者模式自检噪音
    "vehicleFrameworkDebug": 23,
    # 24 两个动物遗传模组抢 defName
    "patchLoadModXMLFilterTelandaGenes": 24,
    # 25 SRCELib 并发写坏集合
    "srceLibConcurrency": 25,
    # 26 Guards For Me 转错类型
    "gfmIdleCombatDeepCopy": 26,
    # 27 ELT 强关 Cumpilation 设置
    "cumpilationSettingsRestore": 27,
    # 28 RJW PE 废弃类型定义
    "filterRjwpeStaleDefs": 28,
    # 29 RI 房间要求补丁刷屏
    "riFurnitureRoyalCompatQuiet": 29,
}

# 去掉编号的项（About 里没有它，或本来就该无号）
# 第二批：把剩下的 6 个空缺号补给它俩（语义最接近的配对）
ASSIGN.update({
    "assemblyGetTypesFallback": 1,      # 1  ElToro「观看」任务缺类（程序集类型解析）
    "musicManagerFadeout": 12,          # 12 中文下 Royalty 判定失效（按显示名判定）
    "steamWorkshopHook": 22,            # 22 近战动画补丁整批失败（补丁批次整体失败）
    "xmlConfigErrorCleanup": 33,        # 33 贞操装备缺耐久与可燃字段（配置字段清理）
    "patchGenTypesPreferTelanda": 34,   # 34 基因分类排序撞号（类型名解析）
    "rjwOrgasmPacing": 35,              # 35 三条指向不存在 def 的交叉引用
})

STRIP = {
    # rjwOrgasmPacing 的错号【18】已被上面的 ASSIGN 覆盖成【35】，这里不再 STRIP
}


def split_entries(t):
    """把 All 数组切成 (id, 起, 止) 三元组，按括号配平。"""
    out = []
    for m in re.finditer(r'new Entry\("([^"]+)"', t):
        st = m.start()
        p = st + t[st:].index("(")
        depth = 0
        j = p
        while j < len(t):
            if t[j] == "(":
                depth += 1
            elif t[j] == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        out.append((m.group(1), st, j + 1))
    return out


def main():
    write = "--write" in sys.argv
    t = open(P, encoding="utf-8-sig").read()
    bom = open(P, "rb").read().startswith(b"\xef\xbb\xbf")
    entries = split_entries(t)
    print("解析到 %d 条 Entry" % len(entries))

    edits = []          # (start, end, newtext)
    for eid, st, en in entries:
        block = t[st:en]
        if eid in STRIP:
            n = STRIP[eid]
            new = block.replace("【%s】" % n, "")
            new = new.replace("（，", "（").replace("，）", "）").replace("（）", "")
            if new != block:
                edits.append((st, en, new))
                print("  去掉 %-30s 的【%s】" % (eid, n))
            continue
        if eid in ASSIGN:
            n = ASSIGN[eid]
            if "【%d】" % n in block:
                continue                       # 已经有了
            # ⚠ 必须匹配**第二个**带引号的字符串 —— 第一个是 Id（"techHediffsZeroBudget"）。
            # 2026-11-15 踩过：上版用 `re.search(r'("(?:[^"\\]|\\.)*?"')` 直接取第一个，
            # 结果把编号写进了 Id 里（`new Entry("techHediffsZeroBudget（【10】）", …)`），
            # 那会直接让 Id 对不上设置键、开关全废。
            strs = list(re.finditer(r'"(?:[^"\\]|\\.)*"', block))
            if len(strs) < 2:
                print("  ⚠ %s 找不到标签字符串" % eid)
                continue
            m2 = strs[1]                       # 第二个 = Label
            label = m2.group(0)
            if label.rstrip('"').endswith("）"):
                nl = label.rstrip('"')[:-1] + "【%d】" % n + '"'
            else:
                nl = label.rstrip('"') + "（【%d】）" % n + '"'
            nb = block[:m2.start()] + nl + block[m2.end():]
            edits.append((st, en, nb))
            print("  补 %-30s → 【%d】" % (eid, n))

    if not edits:
        print("没有改动")
        return
    out = t
    for st, en, nb in sorted(edits, reverse=True):
        out = out[:st] + nb + out[en:]
    if write:
        open(P, "wb").write((b"\xef\xbb\xbf" if bom else b"") + out.encode("utf-8"))
        print("\n已写盘（%d 处）" % len(edits))
    else:
        print("\ndry-run：%d 处（加 --write 才写）" % len(edits))


if __name__ == "__main__":
    main()
