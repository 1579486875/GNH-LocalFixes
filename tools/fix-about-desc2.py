# -*- coding: utf-8 -*-
"""About.xml description 第二轮润色：去掉依赖等宽字体的全角空格对齐、拆超长句、补分段落。"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
ABOUT = os.path.join(REPO, "About", "About.xml")

raw = open(ABOUT, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
txt = raw.decode("utf-8-sig")
m = re.search(r"<description>(.*?)</description>", txt, re.S)
d = m.group(1)
orig = d

subs = [
    # 1）组名不再用全角空格对齐（换字体就崩），改成「—— N 个」
    ("· 诊断工具（最上面一组，默认关闭）　5 个\n"
     "· 崩溃与卡死　　　　　　　　　　　19 个\n"
     "· 日志刷屏　　　　　　　　　　　　13 个\n"
     "· 兼容与判定　　　　　　　　　　　9 个\n"
     "· 行为与设置　　　　　　　　　　　3 个\n"
     "· 性能剖析（默认关闭）　　　　　　1 个\n"
     "· XML 补丁（可单独关闭）　　　　　22 个\n",
     "· 诊断工具（最上面一组，默认关闭）—— 5 个\n"
     "· 崩溃与卡死 —— 19 个\n"
     "· 日志刷屏 —— 13 个\n"
     "· 兼容与判定 —— 9 个\n"
     "· 行为与设置 —— 3 个\n"
     "· 性能剖析（默认关闭）—— 1 个\n"
     "· XML 补丁（可单独关闭）—— 22 个\n"),

    # 2）拆掉那条 120 字的长句
    ("每条右侧会标出异常状态（灰色「已关闭」／暗黄「目标模组未启用」），"
     "顶部有一行汇总，告诉你这次实际装上了多少条、被关了几条、目标模组没装几条。\n",
     "每条右侧会标出异常状态（灰色「已关闭」／暗黄「目标模组未启用」）。\n"
     "顶部有一行汇总，告诉你这次实际装上了多少条、被关了几条、目标模组没装几条。\n"),

    # 3）「本模组原名…」另起一段
    ("现在 66 条，条条独立、条条可关。\n本模组原名",
     "现在 66 条，条条独立、条条可关。\n\n本模组原名"),

    # 4）「删掉哪一半」单独成句（原与两行清单贴在一起）
    ("· Assemblies 目录里是一个 dll\n删掉哪一半，就只有那一半不生效。\n",
     "· Assemblies 目录里是一个 dll\n\n删掉哪一半，就只有那一半不生效。\n"),
]

for old, new in subs:
    if old in d:
        d = d.replace(old, new, 1)
        print("[替换] %s" % old.split("\n")[0][:40])
    else:
        print("[跳过] 没找到：%s" % old.split("\n")[0][:40])

if d == orig:
    print("没有变化")
    sys.exit(0)

newtxt = txt[:m.start(1)] + d + txt[m.end(1):]
out = (b"\xef\xbb\xbf" if bom else b"") + newtxt.encode("utf-8")
open(ABOUT, "wb").write(out)
print()
print("已写入 %d 字节，description 行数 %d" % (len(out), d.count("\n") + 1))
