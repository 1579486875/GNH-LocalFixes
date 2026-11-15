# -*- coding: utf-8 -*-
"""
R24：README 里两处过时的分组描述。

① 第 147 行那句「再下一组只有「性能剖析」一项」——现在性能剖析已经是**第二组**，
   紧跟诊断工具之后，不再是「再下一组」。
② 第 168 行的诊断工具表格说「只有一项：详细错误勘探」——实际已经是 5 个（1 总控 + 4 诊断）。
"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
BASE = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
P = os.path.join(BASE, "README.md")

RULES = [
    # ① 分组顺序：把「再下一组只有性能剖析」改成「紧接着就是性能剖析」，并调整后面的顺序
    (
        "；接着四组是 dll 侧的安装动作（44 个开关，默认全开）；再下一组只有「性能剖析」一项"
        "（**默认关闭**，它关的是游戏自带的功能，值不值得关由你决定）；"
        "最后一组是 22 项 XML 开关（21 个补丁文件各一项 + 一个总控）。",
        "；**紧接着第二组就是「性能剖析」**（1 个开关，**默认关闭** —— 它关的是游戏自带的功能，"
        "值不值得关由你决定）；之后是 dll 侧的安装动作（四类合计 44 个开关，默认全开）；"
        "最后一组是 22 项 XML 开关（21 个补丁文件各一项 + 一个总控）。",
    ),
    # ② 诊断工具表格那一行
    (
        "| **诊断工具（默认关闭）** | 1 | 只有一项：**详细错误勘探**。它不修任何东西，只在界面第一帧扫一遍 "
        "ExtraWeapon2 改过的那 8 个方法，把每个方法上挂着几个补丁、分别来自哪个 Harmony ID 写进日志；"
        "同一方法上有 2 个以上 transpiler 就标星点名。只读、不改指令、不卸别人的补丁 |",
        "| **诊断工具（默认关闭）** | 5 | 一个总控 + 四个只读诊断：**详细错误勘探**（扫 ExtraWeapon2 改过的 "
        "8 个方法，列出每个方法挂着几个补丁、分别来自哪个 Harmony ID）、**世界生成期死亡取证**、"
        "**定位 Parsed X as int**、**RJW 高潮取证**。**总控是「全选」语义** —— 勾上它四条一起勾上，"
        "取消任意一条它自动取消。它们都只读、不改指令、不卸别人的补丁 |",
    ),
]


def main():
    write = "--write" in sys.argv
    raw = open(P, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    txt = raw.decode("utf-8-sig")
    n = 0
    for old, new in RULES:
        if old in txt:
            txt = txt.replace(old, new, 1)
            n += 1
            print("✓ 命中 %d 字 → %d 字" % (len(old), len(new)))
        else:
            print("✗ 未命中，片段：%s…" % old[:40])
    if n and write:
        open(P, "wb").write((b"\xef\xbb\xbf" if bom else b"") + txt.encode("utf-8"))
        print("已写盘（改动 %d 处）" % n)
    elif n:
        print("dry-run（加 --write 才改）")


if __name__ == "__main__":
    main()
