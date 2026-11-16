# -*- coding: utf-8 -*-
"""扫描所有模组的 DefInjected 翻译，找出首尾带空白的 description（会触发
`Config error in X: description has leading/trailing whitespace`）。

为什么只扫 DefInjected：
  普通 Defs/*.xml 里的 <description> 由 DirectXmlToObjectNew 解析，游戏会处理缩进；
  而 DefInjected 是直接 field.SetValue(def, value)，**原样赋值、不 trim**。
  实测证据：1000+ 模组的 Defs 里多行 description 无数，日志却只报 LDR_ISBM_silo 一条
  (也就是唯一在 DefInjected 里带尾随空格的那条)。

用法：
    python tools/scan-desc-whitespace.py            # 只看简中
    python tools/scan-desc-whitespace.py --all-lang # 所有语言目录
"""
import os
import re
import sys
import tarfile

GAME = r"D:\steam\steamapps\common\RimWorld"
WS = r"D:\steam\steamapps\workshop\content\294100"

# 目标语言目录名（模糊匹配）
LANG_HINTS = ["ChineseSimplified", "简体中文", "ChineseTraditional", "繁體中文"]

TAG_RE = re.compile(r"<([A-Za-z0-9_.\-]+\.(?:description|label))>")


def roots():
    yield os.path.join(GAME, "Data")
    yield os.path.join(GAME, "Mods")
    yield WS


def iter_xml(root):
    for dirpath, dirnames, filenames in os.walk(root):
        # 跳过版本目录里非当前版本（1.3/1.4/1.5）以减少噪音
        parts = dirpath.replace("\\", "/").split("/")
        if any(p in ("1.3", "1.4", "1.5") for p in parts):
            continue
        for fn in filenames:
            if fn.lower().endswith(".xml"):
                yield os.path.join(dirpath, fn)


def in_definjected(path):
    p = path.replace("\\", "/").lower()
    return "/definjected/" in p


def lang_ok(path):
    if "--all-lang" in sys.argv:
        return True
    p = path.replace("\\", "/")
    return any(h in p for h in LANG_HINTS)


def kind_of(field):
    """判定这条翻译落在什么字段上，以及它是否会让 Def.ConfigErrors() 报红。

    Def.ConfigErrors() 只检查 **Def 自己的 description 字段**（两段式 `defName.description`）。
    其他形态（stages.*.description / outcomeChances.*.description / *.label）都不在那个检查里，
    只是在 UI 上可能多出一点空白。
    """
    parts = field.split(".")
    if len(parts) == 2 and parts[1] == "description":
        return "DEF_DESC", True
    if parts[-1] == "label":
        return "LABEL", False
    return "OTHER", False


def check_text(field, value, rel, hits):
    if value is None:
        return
    if value and (value[0].isspace() or value[-1].isspace()):
        kind, red = kind_of(field)
        hits.append({
            "field": field,
            "kind": kind,
            "red": red,
            "file": rel,
            "head": value[:30],
            "tail": value[-30:],
            "lead": repr(value[: len(value) - len(value.lstrip())]) if value != value.lstrip() else "",
            "trail": repr(value[len(value.rstrip()):]) if value != value.rstrip() else "",
        })


def scan_file(path, rel, hits):
    try:
        raw = open(path, "rb").read()
    except Exception:
        return
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = raw.decode("utf-8", errors="replace")

    # 逐元素精确取内容（含 CDATA / 内嵌标签时用简单平衡匹配）
    for m in TAG_RE.finditer(text):
        tag = m.group(1)
        close = "</%s>" % tag
        end = text.find(close, m.end())
        if end < 0:
            continue
        value = text[m.end():end]
        # 内嵌标签时退化为纯文本检查（去掉标签再看首尾）
        if "<" in value:
            plain = re.sub(r"<[^>]*>", "", value)
        else:
            plain = value
        check_text(tag, plain, rel, hits)


def scan_tar(path, rel, hits):
    try:
        tf = tarfile.open(path)
    except Exception:
        return
    try:
        for mem in tf.getmembers():
            if not mem.isfile() or not mem.name.lower().endswith(".xml"):
                continue
            inner = rel + "!/" + mem.name
            if not in_definjected(inner):
                continue
            if not lang_ok(inner):
                continue
            f = tf.extractfile(mem)
            if f is None:
                continue
            text = f.read().decode("utf-8-sig", errors="replace")
            for m in TAG_RE.finditer(text):
                tag = m.group(1)
                close = "</%s>" % tag
                end = text.find(close, m.end())
                if end < 0:
                    continue
                value = text[m.end():end]
                plain = re.sub(r"<[^>]*>", "", value) if "<" in value else value
                check_text(tag, plain, inner, hits)
    finally:
        tf.close()


def main():
    hits = []
    tar_count = 0
    seen = set()

    for root in roots():
        if not os.path.isdir(root):
            continue
        # 显式扫 .tar（DLC 语言包）
        for dirpath, _dirnames, filenames in os.walk(root):
            parts = dirpath.replace("\\", "/").split("/")
            if any(p in ("1.3", "1.4", "1.5") for p in parts):
                continue
            for fn in filenames:
                if fn.lower().endswith(".tar"):
                    p = os.path.join(dirpath, fn)
                    if not lang_ok(p):
                        continue
                    tar_count += 1
                    scan_tar(p, os.path.relpath(p, root), hits)

        for path in iter_xml(root):
            if not in_definjected(path):
                continue
            if not lang_ok(path):
                continue
            rp = os.path.abspath(path)
            if rp in seen:
                continue
            seen.add(rp)
            scan_file(path, os.path.relpath(path, root), hits)

    print("扫描 DefInjected XML 文件: %d 个；tar 语言包: %d 个" % (len(seen), tar_count))
    red = [h for h in hits if h["red"]]
    print("命中首尾空白: %d 条（其中会让 Def.ConfigErrors() 报红的两段式 defName.description: %d 条）\n"
          % (len(hits), len(red)))

    if "--red-only" in sys.argv:
        hits = red

    by_file = {}
    for h in hits:
        by_file.setdefault(h["file"], []).append(h)

    for f in sorted(by_file):
        print("─" * 70)
        print(f)
        for h in by_file[f]:
            kinds = []
            if h["lead"]:
                kinds.append("前导 %s" % h["lead"])
            if h["trail"]:
                kinds.append("尾随 %s" % h["trail"])
            mark = "🔴" if h["red"] else ("·" if h["kind"] == "LABEL" else " ")
            print(" %s %-52s %s" % (mark, h["field"], " / ".join(kinds)))
            print("       …%s" % h["tail"].replace("\n", "\\n"))

    if not hits:
        print("未发现首尾空白。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
