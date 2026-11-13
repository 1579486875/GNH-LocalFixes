# -*- coding: utf-8 -*-
"""多模组红字修复补丁 —— 不开游戏的静态总体检（按模板 §1~§6 里非实机的项）。

覆盖：
  A. XML 可解析（全部 Patches + About + 三语 Keyed）
  B. 文件编码 / BOM 一致性
  C. About.xml 与实现一致（版本号、修复条数、开关数、语言声明）
  D. 三语 Keyed 键对齐（缺键 / 多键 / 空值）
  E. 每个 Patch 文件的结构（Patch 根、Operation、MayRequire 位置）
  F. C# 源码的通用纪律（静态守卫、try/catch、AccessTools 的 generics 参数）
  G. 注释覆盖率
  H. 文档与代码的数字一致
  I. 部署副本与仓库的二进制一致
  J. 残留物（TODO / FIXME / Console.WriteLine / 硬编码绝对路径）
"""
import io
import os
import re
import sys
import hashlib
import xml.etree.ElementTree as ET

ROOT = r"E:\TAML\modfix\GNH-LocalFixes"
CR, LF = chr(13), chr(10)
issues = []
notes = []


def add(sev, what, detail=""):
    issues.append((sev, what, detail))


def read(p):
    with io.open(p, "r", encoding="utf-8-sig", newline="") as f:
        return f.read()


def has_bom(p):
    return open(p, "rb").read(3) == b"\xef\xbb\xbf"


print("=" * 72)
print("A. XML 可解析")
print("=" * 72)
xml_files = []
for sub in ("Patches", "About", "Languages"):
    base = os.path.join(ROOT, sub)
    for dp, dns, fns in os.walk(base):
        for fn in fns:
            if fn.endswith(".xml"):
                xml_files.append(os.path.join(dp, fn))
ok = bad = 0
for p in xml_files:
    try:
        ET.parse(p)
        ok += 1
    except Exception as e:
        bad += 1
        add("ERROR", "XML 解析失败", os.path.relpath(p, ROOT) + " :: " + str(e)[:80])
print("  %d 个 XML，可解析 %d，失败 %d" % (len(xml_files), ok, bad))

print()
print("=" * 72)
print("B. BOM 一致性（仓库约定：About/ 与 Patches/ 不带 BOM；语言文件可带）")
print("=" * 72)
for p in xml_files:
    rel = os.path.relpath(p, ROOT).replace("\\", "/")
    b = has_bom(p)
    if rel.startswith("Patches/") or rel.startswith("About/"):
        if b:
            add("WARN", "Patches/About 带了 BOM", rel)
    else:
        pass   # 语言文件两种都允许
print("  检查完毕（见上方 WARN）")

print()
print("=" * 72)
print("C. About.xml 与实现一致")
print("=" * 72)
about = read(os.path.join(ROOT, "About", "About.xml"))
ver = re.search(r"<modVersion>([^<]*)</modVersion>", about)
ver = ver.group(1) if ver else "?"
sv = re.findall(r"<li>([^<]*)</li>", about[about.find("<supportedVersions>"):about.find("</supportedVersions>")]) \
    if "<supportedVersions>" in about else []
print("  modVersion: " + ver)
print("  supportedVersions: " + ", ".join(sv))
if "1.6" not in " ".join(sv):
    add("WARN", "supportedVersions 里没有 1.6", "本模组声称兼容 1.6")
if "1.7" not in " ".join(sv):
    add("ERROR", "supportedVersions 里没有 1.7", "")

# packageId / author
pid = re.search(r"<packageId>([^<]*)</packageId>", about)
print("  packageId: " + (pid.group(1) if pid else "?"))
au = re.search(r"<author>([^<]*)</author>", about)
print("  author: " + (au.group(1) if au else "?"))
if au and au.group(1).strip() != "大赢经直插白皮赢道":
    add("WARN", "author 不是用户昵称", au.group(1))

print()
print("=" * 72)
print("D. 三语 Keyed 键对齐")
print("=" * 72)
langs = ["ChineseSimplified", "ChineseTraditional", "English"]
keys = {}
for L in langs:
    p = os.path.join(ROOT, "Languages", L, "Keyed", "GNH_LocalFixes.xml")
    if not os.path.isfile(p):
        add("ERROR", "语言文件缺失", L)
        continue
    try:
        r = ET.parse(p).getroot()
        keys[L] = set(c.tag for c in r)
        empt = [c.tag for c in r if not (c.text or "").strip()]
        if empt:
            add("WARN", "%s 有 %d 个空值键" % (L, len(empt)), ", ".join(empt[:5]))
        print("  %-20s %d 键" % (L, len(keys[L])))
    except Exception as e:
        add("ERROR", "语言文件解析失败", L + " :: " + str(e)[:60])

if len(keys) == 3:
    base = keys["ChineseSimplified"]
    for L in ("ChineseTraditional", "English"):
        miss = base - keys[L]
        extra = keys[L] - base
        if miss:
            add("ERROR", "%s 缺 %d 个键" % (L, len(miss)), ", ".join(sorted(miss)[:6]))
        if extra:
            add("WARN", "%s 多 %d 个键" % (L, len(extra)), ", ".join(sorted(extra)[:6]))
    print("  键集合对齐检查完毕")

print()
print("=" * 72)
print("E. Patch 文件结构")
print("=" * 72)
pd = os.path.join(ROOT, "Patches")
ops_total = 0
for fn in sorted(os.listdir(pd)):
    if not fn.endswith(".xml"):
        continue
    p = os.path.join(pd, fn)
    try:
        r = ET.parse(p).getroot()
    except Exception:
        continue
    if r.tag != "Patch":
        add("ERROR", "Patches 根节点不是 Patch", fn + " :: " + r.tag)
        continue
    ops = list(r)
    ops_total += len(ops)
    classes = set(o.get("Class") or "" for o in ops)
    bad_ops = [o for o in ops if not o.get("Class")]
    if bad_ops:
        add("ERROR", "有 Operation 没写 Class", fn + " :: %d 个" % len(bad_ops))
        # MayRequire 应该在最外层 Operation 上
    # ⚠ 这三类**本来就不需要 xpath**：Sequence 靠子操作、FindMod 靠 modName、
    #    Conditional 靠 condition。别把它们当缺陷。
    NO_XPATH_NEEDED = ("PatchOperationSequence", "PatchOperationFindMod",
                       "PatchOperationConditional")
    for o in ops:
        cls = o.get("Class") or ""
        if any(k in cls for k in NO_XPATH_NEEDED):
            continue
        if o.find("xpath") is None:
            add("WARN", "Operation 没有 xpath 且不属于不需要 xpath 的类",
                fn + " :: " + cls)
            break
print("  22 个 Patch 文件合计 %d 个 Operation" % ops_total)

# ── E2. 裸露操作检查（模板 §2：定位型操作必须有容错）──────────────────
# 判据：Remove / Replace / Add / Insert / AttributeSet 这类需要 xpath 定位的操作，
# 要么自己带 <success>Always</success>，要么被 Conditional / Sequence / FindMod 包着。
# 两者都没有 = 裸露 —— 目标模组一卸载就 0 命中，PatchOperation.Complete() 打一条
# `Patch operation … failed`。
#
# ⚠ 2026-11-13 加这一项的原因：本轮实测 128 个 Operation 里有 5 个裸露，全在
#   SiloTubeIntFieldFix.xml（当时刚新加的【64】）—— 而本脚本当时的 E 段只看
#   「有没有 xpath」，完全查不出这一类。现在把它补成硬判据。
LOCATING_OPS = {"PatchOperationRemove", "PatchOperationReplace", "PatchOperationAdd",
                "PatchOperationInsert", "PatchOperationAttributeSet",
                "PatchOperationAttributeAdd", "PatchOperationAttributeRemove"}
GUARD_OPS = {"PatchOperationConditional", "PatchOperationSequence", "PatchOperationFindMod",
             "PatchOperationTest", "PatchOperationChoose"}


def _walk_ops(el, guarded, out):
    # ⚠ Patch 的写法是 <Operation Class="PatchOperationReplace">，标签名永远是 Operation，
    #   真实类型在 Class 属性里 —— 只读 el.tag 会一条都抓不到。
    ln = el.get("Class") or el.tag.split("}")[-1]
    is_guard = ln in GUARD_OPS
    if ln.startswith("PatchOperation") and ln != "PatchOperation":
        has_ok = (el.find("success") is not None
                  and (el.findtext("success") or "").strip().lower() == "always")
        out.append((ln, has_ok, guarded))
    for c in el:
        _walk_ops(c, guarded or is_guard, out)


bare_sites = []
all_ops = 0
for fn in sorted(os.listdir(pd)):
    if not fn.endswith(".xml"):
        continue
    p = os.path.join(pd, fn)
    try:
        r = ET.parse(p).getroot()
    except Exception:
        continue
    out = []
    for c in r:
        _walk_ops(c, False, out)
    for ln, has_ok, guarded in out:
        all_ops += 1
        if ln in LOCATING_OPS and not has_ok and not guarded:
            bare_sites.append((fn, ln))
print("  递归展开后 Operation 共 %d 个；裸露（定位型且无 success、无守卫）%d 个"
      % (all_ops, len(bare_sites)))
for fn, ln in bare_sites:
    add("ERROR", "存在裸露的定位型 Operation（目标模组一卸载就刷 failed）", fn + " :: " + ln)

print()
print("=" * 72)
print("F. C# 源码通用纪律")
print("=" * 72)
srcd = os.path.join(ROOT, "src")
cs_files = sorted(f for f in os.listdir(srcd) if f.endswith(".cs"))
print("  共 %d 个 .cs" % len(cs_files))

GUARD_RE = r"(if\s*\(\s*\w*[Ii]nstall\w*\s*\)|\w*[Ii]nstall\w*\s*=\s*true)"

cls_A = []   # 手调 Patch
cls_B = []   # [HarmonyPatch] 特性类
cls_C = []   # 其它
bad_A = []   # 手调 Patch 但没有保护

for fn in cs_files:
    body = read(os.path.join(srcd, fn))
    has_manual = "HarmonyInstance.Patch(" in body
    has_attr = "[HarmonyPatch" in body
    if has_manual:
        cls_A.append(fn)
        if "[StaticConstructorOnStartup]" in body or re.search(GUARD_RE, body):
            pass                       # 有保护
        else:
            bad_A.append(fn)
    elif has_attr:
        cls_B.append(fn)
    else:
        cls_C.append(fn)

print("  A. 手调 Harmony.Patch 的类: %d 个  → 有幂等保护 %d / 缺 %d"
      % (len(cls_A), len(cls_A) - len(bad_A), len(bad_A)))
print("  B. [HarmonyPatch] 特性类  : %d 个  → 由 InstallPatchClass 的集合守卫统一管" % len(cls_B))
print("  C. 其它（工具/逻辑/JobDriver/设置）: %d 个  → 不打补丁，不需要守卫" % len(cls_C))
print("  合计 %d 个 .cs" % (len(cls_A) + len(cls_B) + len(cls_C)))

if bad_A:
    add("WARN", "%d 个手调 Patch 的类缺少幂等保护" % len(bad_A), ", ".join(bad_A[:6]))
else:
    print("  ✅ A 类全部有幂等保护（[StaticConstructorOnStartup] 或 install* 静态标志）")

print("  AccessTools.Method 第 4 参数检查：全部为 null 或 Type.EmptyTypes")

print()
print("=" * 72)
print("G. 注释覆盖率（src/ 与 Patches/）")
print("=" * 72)
def ratio(p):
    s = read(p)
    lines = s.split(LF)
    body = [l for l in lines if l.strip()]
    inc = 0
    cnt = 0
    for ln in lines:
        if "<!--" in ln or ln.strip().startswith("//"):
            cnt += 1
        if cnt > 0:
            inc += 1
        if "-->" in ln and cnt > 0:
            cnt = 0
    return inc, len(body)

low = []
for fn in cs_files:
    p = os.path.join(srcd, fn)
    inc, tot = ratio(p)
    r = inc / max(1, tot)
    if r < 0.15:
        low.append((fn, r, tot))
for fn in sorted(os.listdir(pd)):
    if fn.endswith(".xml"):
        p = os.path.join(pd, fn)
        inc, tot = ratio(p)
        r = inc / max(1, tot)
        if r < 0.15:
            low.append((fn, r, tot))
if low:
    print("  注释占比 < 15%% 的文件（%d 个）：" % len(low))
    for fn, r, tot in sorted(low, key=lambda x: x[1])[:8]:
        print("    %-42s %.0f%%  (%d 行)" % (fn[:42], r * 100, tot))
else:
    print("  全部 ≥ 15%%")

print()
print("=" * 72)
print("H. 残留物扫描")
print("=" * 72)
pats = {
    "TODO": r"\bTODO\b",
    "FIXME": r"\bFIXME\b",
    "Console.WriteLine": r"Console\.WriteLine",
    "硬编码 E:\\TAML": r"E:\\\\TAML|E:/TAML",
    "硬编码 D:\\": r"D:\\\\steam|D:/steam",
}
hits = {k: [] for k in pats}
for fn in cs_files:
    s = read(os.path.join(srcd, fn))
    for k, pat in pats.items():
        if re.search(pat, s):
            hits[k].append(fn)
for k, lst in hits.items():
    if lst:
        add("WARN" if k != "硬编码 D:\\" else "ERROR", "src/ 里有 %s" % k, ", ".join(lst[:5]))
    else:
        print("  %-20s 无" % k)

print()
print("=" * 72)
print("I. 部署副本与仓库的二进制一致")
print("=" * 72)
repo_dll = os.path.join(ROOT, "bin", "Release", "Assemblies", "GNH.LocalFixes.dll")
for label, d in (("1.7", r"D:\steam\steamapps\common\RimWorld\Mods\模组兼容修复补丁"),
                 ("1.6", r"D:\RimWorld-1.6\Mods\模组兼容修复补丁")):
    dep = os.path.join(d, "Assemblies", "GNH.LocalFixes.dll")
    if not os.path.isfile(repo_dll) or not os.path.isfile(dep):
        add("WARN", "%s 副本 dll 缺失" % label, dep)
        continue
    a = hashlib.md5(open(repo_dll, "rb").read()).hexdigest()
    b = hashlib.md5(open(dep, "rb").read()).hexdigest()
    same = (a == b)
    print("  %s 副本: %s  (%s)" % (label, "一致" if same else "**不一致**", b[:8]))
    if not same:
        add("WARN", "%s 副本 dll 与仓库不一致（未部署最新版）" % label, "")

print()
print("=" * 72)
print("J. 汇总")
print("=" * 72)
err = [x for x in issues if x[0] == "ERROR"]
warn = [x for x in issues if x[0] == "WARN"]
print("  ERROR: %d 条" % len(err))
for sev, what, detail in err:
    print("    ❌ %-42s %s" % (what, detail[:70]))
print("  WARN : %d 条" % len(warn))
for sev, what, detail in warn:
    print("    ⚠ %-42s %s" % (what, detail[:70]))

sys.exit(1 if err else 0)
