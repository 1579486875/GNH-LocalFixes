# -*- coding: utf-8 -*-
"""同步修复补丁 README 里过时的版本号与开关/条目数字（实测：66 条、72 个开关）。"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
P = os.path.join(REPO, "README.md")

SUBS = [
    ("**当前版本：1.4.48** · 共 67 项修复",
     "**当前版本：1.4.56** · 共 66 项修复"),
    ("最上面一组是**诊断工具**（4 个开关：一个总控 + 三个只读诊断，**默认关闭**",
     "最上面一组是**诊断工具**（5 个开关：一个总控 + 四个只读诊断，**默认关闭**"),
    ("接着四组是 dll 侧的安装动作（43 个开关，默认全开）",
     "接着四组是 dll 侧的安装动作（44 个开关，默认全开）"),
    ("最后一组是 22 项 XML 开关（21 个补丁文件各一项 + 一个总控）",
     "最后一组是 22 项 XML 开关（21 个补丁文件各一项 + 一个总控）"),
    ("（v1.4.10 起 XML 补丁也能；共 70 个开关",
     "（v1.4.10 起 XML 补丁也能；共 72 个开关"),
    ("（现共 70 个开关）", "（现共 72 个开关）"),
    ("**代码侧 48 个开关 + XML 补丁 22 个开关**（诊断 4 + 崩溃 19 + 刷屏 13 + 兼容 9 + 行为 2 + 剖析 1 + XML 22 = 70）",
     "**代码侧 50 个开关 + XML 补丁 22 个开关**（诊断 5 + 崩溃 19 + 刷屏 13 + 兼容 9 + 行为 3 + 剖析 1 + XML 22 = 72）"),
    ("（**70 个开关覆盖 66 条编号里的 65 条**",
     "（**72 个开关覆盖 66 条编号里的 65 条**"),
    ("> ✅ **70 个开关覆盖全部 66 条编号**",
     "> ✅ **72 个开关覆盖全部 66 条编号**"),
]


def main():
    write = "--write" in sys.argv
    txt = open(P, encoding="utf-8", newline="").read()
    eol = "\r\n" if "\r\n" in txt else "\n"
    orig = txt
    for old, new in SUBS:
        o, n = old.replace("\n", eol), new.replace("\n", eol)
        if o in txt:
            txt = txt.replace(o, n, 1)
            print("[改] %s" % old[:46])
        else:
            print("[跳过] %s" % old[:46])
    # 兜底：剩余 70 个开关 / 67 项
    for bad in ("共 67 项修复", "70 个开关", "代码侧 48 个开关"):
        if bad in txt:
            line = txt[:txt.find(bad)].count("\n") + 1
            print("  [残留] %s 第 %d 行" % (bad, line))
    if write and txt != orig:
        open(P, "w", encoding="utf-8", newline="").write(txt)
        print("已写入")


if __name__ == "__main__":
    main()
