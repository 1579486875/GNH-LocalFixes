#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
verify-patches.py —— Patches\\*.xml 的静态门禁（改完必须跑）

【为什么要有这个脚本 —— 2026-11-12 事故】
  我在 Patches/MiliraApparelPathsDedupe.xml 的注释里写了
      dictionary.Add((K)obj, (V)obj2);   // <-- 重复 key 在这里抛 ArgumentException
  而 **XML 规范不允许注释体里出现 `--`**（`<!--` / `-->` 之外）。
  后果不是「一条红字」，而是整条链：
      ① Exception reading MiliraApparelPathsDedupe.xml as XML
      ② Caught exception while loading play data … Resetting mods config and trying again.
      ③ **Config\\ModsConfig.xml 被重写成只剩官方 DLC**（实测 1188 → 11 条）
      ④ Could not recover from errors loading play data. Giving up.
  换句话说：**一个注释字符，把玩家的整个启用列表清空了。**

  模板 §2 早就写着「改完 XML 必须 ET.fromstring 能解析」——
  这一条不是形式主义，就是拦这种事的。所以把它做成脚本，改完必跑。

【检查项】
  ① 每个 .xml 都能被 ElementTree 解析（含 About.xml）
  ② 注释体里不含 `--`（这是 XML 规范的硬限制）
  ③ 注释体里不以 `-` 结尾（同样非法）
  ④ 文件是「无 BOM + 纯 LF」（本仓库 Patches/ 与 About/ 的统一约定）
  ⑤ 每个 Patches/*.xml 都在 FixToggles.cs 里登记了 xmlPatchFile（0 缺 0 多）
  ⑥ 定位型操作（Remove/Replace/Add/Insert/AttributeSet）如果既没有
     `<success>Always</success>`、又不在 Conditional/Sequence 里包着，就列出来
     —— 这不是错，但需要人确认「目标消失时会不会刷红字」

【用法】
  python tools/verify-patches.py            # 只报告（检查仓库里的 Patches/）
  python tools/verify-patches.py --strict   # 有任何问题时退出码 1（CI 用）
  python tools/verify-patches.py --deployed # 额外检查**部署到 Mods 目录的那一份**

【为什么要有 --deployed —— 2026-11-12 又踩了一次】
  tools/deploy.ps1 的 Patches\ 是**从 bin\<配置>\ 复制**的（那是构建产物），
  不是从工程根。所以「改完 XML 直接 deploy」会把**上一次构建时的那份旧 XML** 装进游戏 ——
  文件大小可能一模一样（本次就是 7419 字节 vs 7419 字节），肉眼完全看不出来。

  实测后果：仓库里的 XML 已经修好、ET 解析通过，游戏仍然报
      An XML comment cannot contain '--' … Line 28, position 66
  于是 play data 加载失败 → ModsConfig 被重写成只剩官方 DLC。

  ⇒ **固定顺序：改 XML → dotnet build → deploy → verify-patches.py --deployed → 再启动。**
"""
import glob
import io
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATCHES = os.path.join(ROOT, "Patches")
ABOUT = os.path.join(ROOT, "About", "About.xml")
TOGGLES = os.path.join(ROOT, "src", "FixToggles.cs")

STRICT = "--strict" in sys.argv
DEPLOYED = "--deployed" in sys.argv
DEPLOYED_DIR = r"D:\steam\steamapps\common\RimWorld\Mods\模组兼容修复补丁"
problems = []
notes = []

# ── ① ② ③ ④：逐个文件 ─────────────────────────────────────────────────────
files = sorted(glob.glob(os.path.join(PATCHES, "*.xml")))
print(f"检查 {len(files)} 个 Patches 文件 + About.xml\n")

targets = files + [ABOUT]
if DEPLOYED:
    import glob as _g
    dep_files = sorted(_g.glob(os.path.join(DEPLOYED_DIR, "Patches", "*.xml")))
    if not dep_files:
        problems.append(f"--deployed 指定了 {DEPLOYED_DIR}，但那里没有 Patches/*.xml（路径对不对？）")
    else:
        print(f"[--deployed] 另加 {len(dep_files)} 个部署版文件")
        targets = targets + dep_files

for path in targets:
    try:
        name = os.path.relpath(path, ROOT)
    except ValueError:
        name = path  # 跨盘符（部署版在 D:，仓库在 E:）
    raw = open(path, "rb").read()

    # ④ BOM / 行尾
    if raw.startswith(b"\xef\xbb\xbf"):
        problems.append(f"{name}: 带 BOM（本仓库 Patches/ 与 About/ 约定无 BOM）")
    crlf = raw.count(b"\r\n")
    if crlf:
        problems.append(f"{name}: 有 {crlf} 个 CRLF（约定纯 LF）")
    if path is ABOUT and raw.count(b"\r\n"):
        pass  # About 也按 LF

    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        problems.append(f"{name}: 不是合法 UTF-8 → {e}")
        continue

    # ① 解析
    try:
        ET.fromstring(text)
    except ET.ParseError as e:
        problems.append(f"{name}: **XML 解析失败** → {e}")
        # 解析失败时仍然继续做注释检查，帮人定位
    # ② ③ 注释体
    for m in re.finditer(r"<!--(.*?)-->", text, re.S):
        body = m.group(1)
        if "--" in body:
            idx = body.index("--")
            line = text[:m.start(1) + idx].count("\n") + 1
            snippet = body[max(0, idx - 40):idx + 40].replace("\n", " ")
            problems.append(f"{name}:{line}: 注释体内出现 '--'（XML 禁止）→ …{snippet}…")
        if body.rstrip().endswith("-"):
            line = text[:m.end(1)].count("\n") + 1
            problems.append(f"{name}:{line}: 注释体以 '-' 结尾（XML 禁止）")

# ── ⑤ Patches 文件 ↔ FixToggles 的 xmlPatchFile 双向比对 ────────────────
tg = open(TOGGLES, "rb").read().decode("utf-8")
refs = re.findall(r'xmlPatchFile:\s*"([^"]+)"', tg)
base_files = sorted(os.path.basename(f) for f in files)
missing = [f for f in base_files if f not in refs]
orphan = [f for f in refs if f not in base_files]
if missing:
    problems.append(f"这些 Patches 文件没有登记开关（会静默执行、界面看不到）：{missing}")
if orphan:
    problems.append(f"这些开关指向了不存在的 Patches 文件：{orphan}")

# ── ⑥ 裸露的定位型操作 ──────────────────────────────────────────────────
LOCATING = {"PatchOperationRemove", "PatchOperationReplace", "PatchOperationAdd",
            "PatchOperationInsert", "PatchOperationAttributeSet", "PatchOperationAttributeAdd"}


def walk(el, guarded):
    cls = el.get("Class") or ""
    inner_guarded = guarded or cls in ("PatchOperationConditional", "PatchOperationSequence",
                                       "PatchOperationFindMod", "PatchOperationTest")
    if cls in LOCATING:
        has_success = False
        for ch in el:
            if ch.tag == "success" and (ch.text or "").strip() == "Always":
                has_success = True
        if not has_success and not guards_self(el) and not guarded:
            notes.append(f"{cls}: xpath={(el.findtext('xpath') or '')[:90]}")
    for ch in el:
        walk(ch, inner_guarded)


def guards_self(el):
    """元素内部自己带了 Conditional（即它是 Conditional 的 match/nomatch 子操作）。"""
    return False


for path in files:
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError:
        continue
    walk(root, False)

# ── 输出 ────────────────────────────────────────────────────────────────
print("=== 缺陷 ===")
if problems:
    for p in problems:
        print("  ✗", p)
else:
    print("  无 ✅")

print("\n=== 提示（不是错，需人确认「目标消失时会不会刷红字」）===")
if notes:
    # 去重（同一条 xpath 可能重复出现）
    seen = set()
    for n in notes:
        if n in seen:
            continue
        seen.add(n)
        print("  ·", n)
    print(f"  （共 {len(seen)} 条去重后）")
else:
    print("  无 ✅")

print(f"\n结论：{len(problems)} 个缺陷 / {len(set(notes))} 条提示")
if STRICT and problems:
    sys.exit(1)
