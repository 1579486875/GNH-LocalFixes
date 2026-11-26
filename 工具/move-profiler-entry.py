# -*- coding: utf-8 -*-
"""
把 FixToggles.All 里的 `deepProfilerQuiet` 整块 Entry 从「Advanced（性能剖析）」组
移到「诊断工具」组**之后、Crash 之前**，并去掉标题里的【40】编号。

用户 2026-11-15 要求：
  ① 性能剖析工具「去掉编号」
  ② 移到排名第二的分组位置（诊断工具分组的下面）

All 数组的顺序就是界面顺序（文件里第一句注释就写着「顺序即界面顺序」），
所以「移到第二组」= 把这个 Entry 挪到数组第 5 个位置（4 个诊断之后）。
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
P = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "源码", "FixToggles.cs")
P = os.path.abspath(P)

txt = open(P, encoding="utf-8-sig").read()
bom = open(P, "rb").read().startswith(b"\xef\xbb\xbf")
orig_len = len(txt)

# ── 1. 抓出 deepProfilerQuiet 那一条（从 new Entry("deepProfilerQuiet" 到 "),"，按括号配平）
anchor = 'new Entry("deepProfilerQuiet"'
i = txt.find(anchor)
if i < 0:
    print("找不到 deepProfilerQuiet")
    sys.exit(1)

# 往前吃注释块（从上一个非注释空行开始）
start = i
while True:
    prev_nl = txt.rfind("\n", 0, start - 1)
    if prev_nl < 0:
        break
    line = txt[prev_nl + 1:start - 1] if start > 0 else ""
    if line.strip().startswith("//"):
        start = prev_nl + 1
    else:
        break

# 从 new Entry( 起做括号配平，找到匹配的 "),"
depth = 0
j = i
while j < len(txt):
    ch = txt[j]
    if ch == "(":
        depth += 1
    elif ch == ")":
        depth -= 1
        if depth == 0:
            # 吃掉紧跟的逗号与换行
            k = j + 1
            while k < len(txt) and txt[k] in "\r\n":
                k += 1
            if k < len(txt) and txt[k] == ",":
                k += 1
            end = k
            break
    j += 1
else:
    print("括号配平失败")
    sys.exit(1)

block = txt[start:end]
print("抓到 Entry 块 %d 字符" % len(block))
print("  首行:", block.strip().split("\n")[0][:70])

# ── 2. 去掉标题里的【40】
block2 = block.replace("DeepProfiler 拖日志与内存（【40】，默认关闭）",
                       "DeepProfiler 拖日志与内存（默认关闭）")
if block2 == block:
    print("  ⚠ 标题里没找到【40】，请人工确认")
else:
    print("  ✓ 已去掉标题里的【40】")

# ── 3. 从原位置删除
txt2 = txt[:start] + txt[end:]

# ── 4. 插到 pawnGenDeathProbe 那条的 ")," 之后
anchor2 = 'new Entry("pawnGenDeathProbe"'
i2 = txt2.find(anchor2)
if i2 < 0:
    print("找不到 pawnGenDeathProbe")
    sys.exit(1)
depth = 0
j = i2
while j < len(txt2):
    ch = txt2[j]
    if ch == "(":
        depth += 1
    elif ch == ")":
        depth -= 1
        if depth == 0:
            k = j + 1
            while k < len(txt2) and txt2[k] in "\r\n":
                k += 1
            if k < len(txt2) and txt2[k] == ",":
                k += 1
            ins = k
            break
    j += 1
else:
    print("第二处括号配平失败")
    sys.exit(1)

# 保持缩进风格：块内每行前面已经有 12 空格（new Entry 那行）
insert = "\n\n" + block2.rstrip("\n").rstrip(",") + ",\n"
txt3 = txt2[:ins] + insert + txt2[ins:]

# ── 5. 写回（保持 BOM / 行尾）
eol = "\r\n" if "\r\n" in txt3 else "\n"
txt3 = txt3.replace("\r\n", "\n").replace("\n", eol)
open(P, "wb").write((b"\xef\xbb\xbf" if bom else b"") + txt3.encode("utf-8"))
print("\n已写回：%d → %d 字节" % (orig_len, len(txt3)))

# ── 6. 复验：Entry 顺序
t = open(P, encoding="utf-8-sig").read()
ids = re.findall(r'new Entry\("([^"]+)"', t)
print("\n新的 Entry 顺序（前 8 个）：")
for n, x in enumerate(ids[:8]):
    print("  %2d %s" % (n, x))
print("总 Entry 数:", len(ids))
