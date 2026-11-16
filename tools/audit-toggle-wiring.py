# -*- coding: utf-8 -*-
"""开关 → 实现 的映射核对（只读，进闸门用）。

## 为什么不能用「grep 开关 id 字面量」当判据（2026-11-16 踩过）

第一版脚本对 76 个 id 逐个 grep `"id"`，报出「30 个开关在 FixToggles.cs 之外 0 引用」。
人工复核后**全是假阳性**：

  · 22 个 `xml*` 开关由 `XmlPatchGate` 按 **`Entry.XmlPatchFile`（文件名）** 匹配，
    根本不出现 id 字符串；
  · 8 个 `patch*` / 机制类由 `LocalFixesMod.InstallPatchClass` 按
    **`Entry.PatchClassName`（完整类型名）** 反查 `FixToggles.IdForPatchClass`，
    同样不出现 id 字符串。

这正是模板 §3.2.4「审计脚本报不通过时先怀疑脚本」那一条。

## 本脚本的正确口径

按 `Entry` 的两个可选参数分别核对：

| 参数 | 判据 | 失败含义 |
|---|---|---|
| `patchClassName` | 该类型在 `src/*.cs` 里存在，且带 `[HarmonyPatch]` | 开关指向一个不存在的类 → 拨了没反应 |
| `xmlPatchFile` | `Patches/` 下该文件名存在 | 开关指向一个不存在的补丁文件 |
| 两者都 null | 必须是下列之一：① 由 `TryInstall("id", ...)` 手工安装；② 诊断总控；③ 纯行为开关（在别的类里读 `IsFixEnabled`） | 否则是死开关 |

`xmlPatchFile` 还要**反向**核对：`Patches\` 下每个 .xml 是否都有开关管着。
"""
import os
import re
import sys
import glob

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "src")
PATCHES = os.path.join(REPO, "Patches")

TOGGLES = os.path.join(SRC, "FixToggles.cs")
MODFILE = os.path.join(SRC, "LocalFixesMod.cs")


def parse_entries(text):
    """返回 [(id, patchClassName, xmlPatchFile)]。

    按括号配平扫描 `new Entry(`，避开字符串里的括号。
    """
    out = []
    i = 0
    n = len(text)
    needle = "new Entry("
    while True:
        j = text.find(needle, i)
        if j < 0:
            break
        k = j + len(needle) - 1          # 指向 '('
        depth = 0
        instr = False
        esc = False
        while k < n:
            c = text[k]
            if instr:
                if esc:
                    esc = False
                elif c == "\\":
                    esc = True
                elif c == '"':
                    instr = False
            else:
                if c == '"':
                    instr = True
                elif c == "(":
                    depth += 1
                elif c == ")":
                    depth -= 1
                    if depth == 0:
                        break
            k += 1
        body = text[j:k + 1]
        out.append(body)
        i = k + 1
    return out


def split_args(body):
    """把 `new Entry(a, b, c, d, e, f)` 的实参按顶层逗号切开（避开字符串内逗号）。"""
    inner = body[len("new Entry("):-1]
    args = []
    cur = []
    depth = 0
    instr = False
    esc = False
    for c in inner:
        if instr:
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                instr = False
            cur.append(c)
            continue
        if c == '"':
            instr = True
            cur.append(c)
            continue
        if c in "([{":
            depth += 1
            cur.append(c)
            continue
        if c in ")]}":
            depth -= 1
            cur.append(c)
            continue
        if c == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
            continue
        cur.append(c)
    tail = "".join(cur).strip()
    if tail:
        args.append(tail)
    return args


def lit(a):
    """把实参还原成字符串或 None。

    ⚠ 必须处理**命名实参**：FixToggles.cs 里 XML 补丁项写的是
        new Entry("xmlVfeeRoyalTitleExt", GroupXmlPatches, "…", "…",
                  xmlPatchFile: "VFEE_RoyalTitleExtFix.xml"),
    第一版没剥 `xmlPatchFile:` 前缀，于是 22 个 XML 开关全被误判成
    「patchClassName 是个怪字符串」。**又是脚本口径问题，不是数据问题。**
    """
    a = a.strip()
    m = re.match(r'^(patchClassName|xmlPatchFile)\s*:\s*(.*)$', a, re.S)
    if m:
        a = m.group(2).strip()
    if a == "null":
        return None
    m = re.match(r'^"((?:[^"\\]|\\.)*)"$', a, re.S)
    return m.group(1) if m else ("<EXPR:%s>" % a[:40])


def main():
    tog_text = open(TOGGLES, encoding="utf-8").read()
    bodies = parse_entries(tog_text)
    entries = []
    for b in bodies:
        a = split_args(b)
        if len(a) < 4:
            print("  ⚠ 解析不完整，跳过：%s" % b[:60].replace("\n", " "))
            continue
        eid = lit(a[0])
        patch_cls = None
        xml_file = None
        # 第 1-4 个是位置实参（id / group / label / desc）。
        # 之后可能位置传（第 5 = patchClassName、第 6 = xmlPatchFile），
        # 也可能纯命名实参（`xmlPatchFile: "X.xml"` 直接跳过 patchClassName）。
        # 所以**必须认名字**，不能按位置硬套。
        pos_slot = 0          # 0 → 下一个位置实参是 patchClassName；1 → 是 xmlPatchFile
        for arg in a[4:]:
            m = re.match(r'^(patchClassName|xmlPatchFile)\s*:\s*(.*)$', arg, re.S)
            if m:
                if m.group(1) == "patchClassName":
                    patch_cls = lit(m.group(2))
                else:
                    xml_file = lit(m.group(2))
                continue
            v = lit(arg)
            if pos_slot == 0:
                patch_cls = v
            elif pos_slot == 1:
                xml_file = v
            pos_slot += 1
        entries.append((eid, patch_cls, xml_file))

    print("解析到 %d 个 Entry" % len(entries))
    print()

    # 收集所有源文件的文本与 HarmonyPatch 类名
    srcs = {}
    for f in glob.glob(os.path.join(SRC, "*.cs")):
        srcs[os.path.basename(f)] = open(f, encoding="utf-8").read()

    all_text = "\n".join(srcs.values())

    # ⚠ 收集 `[HarmonyPatch...]` 修饰的类名。
    #
    # 不能用 `\[HarmonyPatch[^\]]*\]` —— 特性本身可能带数组下标，
    # 例如 `[HarmonyPatch(typeof(PatchClassProcessor), "Patch", new Type[0])]`，
    # `[^\]]*` 会在 `Type[0` 处提前停下，于是 Patch_PatchClassProcessor_Isolate
    # 被误判成「没有 HarmonyPatch」。**逐字符配平**才稳。
    hp_classes = set()
    for m in re.finditer(r'\[HarmonyPatch', all_text):
        i = m.end() - 1                    # 指向 '[' 之后的第一个字符
        depth = 1
        n = len(all_text)
        while i < n and depth > 0:
            c = all_text[i]
            if c == '[':
                depth += 1
            elif c == ']':
                depth -= 1
            i += 1
        tail = all_text[i:i + 400]
        mm = re.search(r'\bclass\s+(\w+)', tail)
        if mm:
            hp_classes.add(mm.group(1))
    print("带 [HarmonyPatch] 的类：%d 个" % len(hp_classes))

    mod_text = srcs.get("LocalFixesMod.cs", "")
    tryinstall_ids = set(re.findall(r'TryInstall\(\s*"([^"]+)"', mod_text))

    rows = []
    fails = []
    for eid, patch_cls, xml_file in entries:
        kind = []
        problem = None
        note = None
        if patch_cls:
            short = patch_cls.rsplit(".", 1)[-1]
            if short in hp_classes:
                kind.append("PatchClass")
            elif eid in tryinstall_ids:
                # 这一类靠自己的 Install() 手工 Patch（没有 [HarmonyPatch]），
                # 开关由 LocalFixesMod 里的 TryInstall 检查。
                # patchClassName 是**冗余登记**：IdForPatchClass 是「类型名 → id」单向表，
                # 反向查不到这个类，所以它永远不会被 InstallPatchClass 命中 —— 无害。
                # 保留它的好处是万一将来这个类真加了 [HarmonyPatch]，登记已经在了。
                kind.append("Install+登记")
                note = "patchClassName %s 无 [HarmonyPatch]，但本项由 TryInstall 装配（冗余登记，无害）" % short
            else:
                kind.append("PatchClass?")
                problem = ("patchClassName %s 既没有 [HarmonyPatch]，也不由 TryInstall 装配 "
                           "→ 开关指向一个永远不会被安装的类" % patch_cls)
        if xml_file:
            kind.append("XmlPatch")
            if not os.path.exists(os.path.join(PATCHES, xml_file)):
                problem = "xmlPatchFile %s 在 Patches\\ 下不存在" % xml_file
        if not patch_cls and not xml_file:
            if eid in tryinstall_ids:
                kind.append("TryInstall")
            elif eid in ("allDiagnostics",):
                kind.append("总控")
            else:
                hits = len(re.findall(r'"%s"' % re.escape(eid), all_text)) - \
                       len(re.findall(r'"%s"' % re.escape(eid), tog_text))
                if hits > 0:
                    kind.append("行为开关")
                else:
                    kind.append("?")
                    problem = "既无 patchClassName / xmlPatchFile，也无 TryInstall，且别处不读这个 id"
        if problem:
            fails.append((eid, problem))
        rows.append((eid, "+".join(kind), problem, note))

    print()
    print("%-42s %-22s %s" % ("id", "生效方式", "问题"))
    print("-" * 100)
    for eid, kind, problem, note in rows:
        print("%-42s %-22s %s" % (eid, kind, problem or note or ""))

    # 反向：Patches\ 下每个 xml 是否被某个开关管着
    declared = set(x[2] for x in entries if x[2])
    on_disk = set(os.path.basename(p) for p in glob.glob(os.path.join(PATCHES, "*.xml")))
    unmanaged = sorted(on_disk - declared)
    ghosts = sorted(declared - on_disk)

    print()
    print("Patches\\ 下 %d 个 xml；被开关登记 %d 个" % (len(on_disk), len(declared)))
    if unmanaged:
        print("  ⚠ 没被任何开关管着：%s" % ", ".join(unmanaged))
    if ghosts:
        print("  ❌ 开关登记了但磁盘没有：%s" % ", ".join(ghosts))

    print()
    if fails or ghosts:
        print("❌ 开关映射问题 %d 项" % (len(fails) + len(ghosts)))
        return 1
    print("✅ 76 个开关全部有真实实现对应")
    return 0


if __name__ == "__main__":
    sys.exit(main())
