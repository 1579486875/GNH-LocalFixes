#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
docstats.py —— 把「修复项数 / 开关数 / XML 补丁数」从源码里数出来，自动改写文档。

【为什么需要这个脚本】
  About.xml 与 README 里的「现在 N 条」「N 项」「N 个开关」是静态文本，
  而修复项是活数据（FixToggles.All）。两边唯一的联系是「有人记得手改」——
  2026-10-28 就忘了一次：加了 3 项修复，界面显示 35、实际 38。

  运行时自检（src/DocStatsSelfCheck.cs）负责**发现**不一致并打 WARNING；
  本脚本负责**修好**它。

【口径】
  总项数     = src/FixToggles.cs 里 `new Entry(` 的个数（= FixToggles.All.Length）
  XML 补丁数 = Patches/*.xml 的个数（且必须与 FixToggles 里 xmlPatchFile: 的个数一致）
  开关数     = 总项数（v1.4.15 起 38 项每条都有独立开关）

【用法】
  python tools/docstats.py            # 报告当前数字，不改文件
  python tools/docstats.py --write    # 改写 About.xml 与 README.md
  python tools/docstats.py --check    # CI 用：不一致时退出码 1
"""
import os, re, sys, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOGGLES = os.path.join(ROOT, "src", "FixToggles.cs")
MOD = os.path.join(ROOT, "src", "LocalFixesMod.cs")
ABOUT = os.path.join(ROOT, "About", "About.xml")
README = os.path.join(ROOT, "README.md")
PATCHES = os.path.join(ROOT, "Patches")

WRITE = "--write" in sys.argv
CHECK = "--check" in sys.argv


def read(p):
    with open(p, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom


def write(p, text, bom):
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    with open(p, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())


# ---------- 数数 ----------
tg, _ = read(TOGGLES)
# 只数 All 数组那一块，避免把注释里的示例也算进去
start = tg.find("internal static readonly Entry[] All")
end = tg.find("};", start)
block = tg[start:end] if start >= 0 else tg
total = len(re.findall(r"new Entry\(", block))
ids = re.findall(r'new Entry\("([^"]+)"', block)

xml_refs = re.findall(r'xmlPatchFile:\s*"([^"]+)"', tg)
xml_files = sorted(os.path.basename(p) for p in glob.glob(os.path.join(PATCHES, "*.xml")))

md, _ = read(MOD)
code_installs = len(re.findall(r"TryInstall\(", md))

print(f"总项数（FixToggles.All）      : {total}")
print(f"独立开关数                    : {total}（每条一项）")
print(f"代码侧 TryInstall 安装点      : {code_installs}")
print(f"XML 补丁文件（Patches/*.xml） : {len(xml_files)}")
print(f"XML 补丁开关（xmlPatchFile:） : {len(xml_refs)}")

problems = []

# 一致性：每个 XML 文件都要有开关，每个开关也要有文件
missing_toggle = [f for f in xml_files if f not in xml_refs]
orphan_toggle = [f for f in xml_refs if f not in xml_files]
if missing_toggle:
    problems.append(f"这些 XML 补丁没有登记开关（会静默执行、界面看不到）：{missing_toggle}")
if orphan_toggle:
    problems.append(f"这些开关指向不存在的 XML 文件：{orphan_toggle}")

# 重复 id
dupes = sorted({i for i in ids if ids.count(i) > 1})
if dupes:
    problems.append(f"重复的开关 id：{dupes}")

# ---------- 文档 ----------
about, about_bom = read(ABOUT)
readme, readme_bom = read(README)

# About：简介里的「现在 N 条」
m = re.search(r"现在\s*(\d+)\s*条", about)
about_declared = int(m.group(1)) if m else None
print(f"\nAbout.xml 简介声明            : {about_declared}")

# README：几处数字
rm_total = re.search(r"共\s*(\d+)\s*项修复", readme)
rm_switch = re.search(r"\*\*(\d+)\s*个独立开关\*\*", readme)
rm_xml = re.search(r"[`]?Patches\\?[`]?\s*下\s*(\d+)\s*个\s*XML\s*补丁", readme)
print(f"README「共 N 项修复」          : {rm_total.group(1) if rm_total else '未找到'}")
print(f"README「N 个独立开关」         : {rm_switch.group(1) if rm_switch else '未找到'}")
print(f"README「N 个 XML 补丁」        : {rm_xml.group(1) if rm_xml else '未找到'}")

if about_declared is not None and about_declared != total:
    problems.append(f"About.xml 写「现在 {about_declared} 条」，实际 {total} 项")
if rm_total and int(rm_total.group(1)) != total:
    problems.append(f"README 写「共 {rm_total.group(1)} 项修复」，实际 {total} 项")
if rm_switch and int(rm_switch.group(1)) != total:
    problems.append(f"README 写「{rm_switch.group(1)} 个独立开关」，实际 {total} 个")
if rm_xml and int(rm_xml.group(1)) != len(xml_files):
    problems.append(f"README 写「{rm_xml.group(1)} 个 XML 补丁」，实际 {len(xml_files)} 个")

print()
if not problems:
    print("✅ 文档与实现一致，不需要改。")
    sys.exit(0)

print("⚠ 发现不一致：")
for p in problems:
    print("   -", p)

if CHECK:
    sys.exit(1)

if not WRITE:
    print("\n（未加 --write，只报告不改。要改请跑：python tools/docstats.py --write）")
    sys.exit(0)

# ---------- 改写 ----------
changed = []

if m and int(m.group(1)) != total:
    about = about[:m.start(1)] + str(total) + about[m.end(1):]
    write(ABOUT, about, about_bom)
    changed.append("About/About.xml")

if rm_total and int(rm_total.group(1)) != total:
    readme = readme[:rm_total.start(1)] + str(total) + readme[rm_total.end(1):]
    write(README, readme, readme_bom)
    changed.append("README.md（共 N 项修复）")
    readme, readme_bom = read(README)

if rm_switch and int(rm_switch.group(1)) != total:
    readme = readme[:rm_switch.start(1)] + str(total) + readme[rm_switch.end(1):]
    write(README, readme, readme_bom)
    changed.append("README.md（N 个独立开关）")
    readme, readme_bom = read(README)

if rm_xml and int(rm_xml.group(1)) != len(xml_files):
    readme = readme[:rm_xml.start(1)] + str(len(xml_files)) + readme[rm_xml.end(1):]
    write(README, readme, readme_bom)
    changed.append("README.md（N 个 XML 补丁）")

print("\n已改写：", ", ".join(changed) if changed else "（无需改写）")
print("改完记得重编译部署，再跑一次本脚本确认 0 不一致。")
