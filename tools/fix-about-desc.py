# -*- coding: utf-8 -*-
"""
重排 About.xml 的 <description>：
  ① 修好三处「被吃掉换行导致粘连」的地方（【64】+怎么用、【66】+装进 Mods、【68】+本模组由两部分组成）；
  ② 「设置界面」那段 500 字的巨型段落拆成清单；
  ③ 加小标题与分节空行，并更新过时的数字（67 条 → 66 条、70 个开关 → 72 个、七组各自条数）。
内容一个字都不删，只调整版式与数字。

用法：python tools/fix-about-desc.py [--write]
"""
import os
import re
import sys
import argparse

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
ABOUT = os.path.join(REPO, "About", "About.xml")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()

    raw = open(ABOUT, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    txt = raw.decode("utf-8-sig")
    m = re.search(r"<description>(.*?)</description>", txt, re.S)
    if not m:
        print("找不到 <description>")
        return 1
    d = m.group(1)
    orig = d

    subs = []

    # ① 开头数字
    subs.append(("现在 67 条，条条独立。", "现在 66 条，条条独立、条条可关。"))

    # ② 【64】末尾粘连的「怎么用」
    subs.append((
        "不由本模组负责）怎么用\n",
        "不由本模组负责）\n"))

    # ③ 【66】后面粘上来的「怎么用」正文
    subs.append((
        "【66】读档时「仪式义务」的 ID 撞车（RitualObligation_0）装进 Mods 目录、"
        "在游戏里启用即可，没有必填设置。位置建议靠下：排在 Harmony 之后，"
        "最好也排在它要修的那些模组之后。\n",
        "【66】读档时「仪式义务」的 ID 撞车（RitualObligation_0）\n"))

    # ④ 【68】后面粘上来的「本模组由两部分组成」
    subs.append((
        "【68】读档时鼠族基因扩展刷的 Debug:________ 调试输出两部分组成："
        "Patches 目录下是 22 个 XML 补丁，Assemblies 目录里是一个 dll，"
        "互不依赖 —— 删掉哪一半，就只有那一半不生效。\n",
        "【68】读档时鼠族基因扩展刷的 Debug:________ 调试输出\n"))

    for old, new in subs:
        if old not in d:
            print("[跳过] 没找到锚点：%s" % old[:40].replace("\n", "\\n"))
            continue
        d = d.replace(old, new, 1)
        print("[替换] %s" % old[:40].replace("\n", "\\n"))

    # ⑤ 在【68】之后插入「怎么用」小节
    anchor = "【68】读档时鼠族基因扩展刷的 Debug:________ 调试输出\n"
    if anchor in d:
        block = (
            "\n"
            "■ 怎么用\n"
            "\n"
            "装进 Mods 目录、在游戏里启用即可，没有必填设置。\n"
            "\n"
            "位置建议靠下：排在 Harmony 之后，最好也排在它要修的那些模组之后。\n"
            "\n"
            "本模组由两部分组成，互不依赖 ——\n"
            "· Patches 目录下是 22 个 XML 补丁\n"
            "· Assemblies 目录里是一个 dll\n"
            "删掉哪一半，就只有那一半不生效。\n")
        d = d.replace(anchor, anchor + block, 1)
        print("[插入] 怎么用 小节")

    # ⑥ 「设置界面」巨型段落 → 清单
    old_set = ("设置界面共有 70 个开关，分七组：诊断工具（最上面一组，默认关闭）、崩溃与卡死、"
               "日志刷屏、兼容与判定、行为与设置、性能剖析（默认关闭）、XML 补丁（可单独关闭）。"
               "最上面那组现在有三个只读取证工具：「详细错误勘探」（查 transpiler 撞车）、"
               "「世界生成期死亡取证」（查某派系领袖生不出来）、"
               "「定位 Parsed X as int」（查哪个 XML 把小数值写进整数字段，需要排查时才打开），"
               "外加一个「全部诊断一起开」的总控。"
               "它们都只读、不修任何东西，也不算在上面那些修复里；没有排查需求就让它一直关着。"
               "每条右侧会标出异常状态（灰色「已关闭」／暗黄「目标模组未启用」），"
               "顶部有一行汇总，告诉你这次实际装上了多少条、被关了几条、目标模组没装几条。"
               "★ 唯一没有开关的是【1】（ElToro「观看」任务缺类）—— 它是补一个任务类型，"
               "类型必须编进 dll 才能被解析，没法运行时开关；删掉 Assemblies 目录它就一起没了。\n")
    new_set = (
        "\n"
        "■ 设置界面（72 个开关，分七组）\n"
        "\n"
        "· 诊断工具（最上面一组，默认关闭）　5 个\n"
        "· 崩溃与卡死　　　　　　　　　　　19 个\n"
        "· 日志刷屏　　　　　　　　　　　　13 个\n"
        "· 兼容与判定　　　　　　　　　　　9 个\n"
        "· 行为与设置　　　　　　　　　　　3 个\n"
        "· 性能剖析（默认关闭）　　　　　　1 个\n"
        "· XML 补丁（可单独关闭）　　　　　22 个\n"
        "\n"
        "诊断工具那组是五个只读取证工具：\n"
        "\n"
        "· 「详细错误勘探」—— 查 transpiler 撞车\n"
        "· 「世界生成期死亡取证」—— 查某派系领袖生不出来\n"
        "· 「定位 Parsed X as int」—— 查哪个 XML 把小数值写进整数字段\n"
        "· 「RJW 高潮取证」—— 查高潮节奏不对时到底算出了什么\n"
        "· 「全部诊断一起开」—— 上面四个的总控\n"
        "\n"
        "它们都只读、不修任何东西，也不算在那些修复里；没有排查需求就让它一直关着。\n"
        "\n"
        "每条右侧会标出异常状态（灰色「已关闭」／暗黄「目标模组未启用」），"
        "顶部有一行汇总，告诉你这次实际装上了多少条、被关了几条、目标模组没装几条。\n"
        "\n"
        "★ 唯一没有开关的是【1】（ElToro「观看」任务缺类）—— 它是补一个任务类型，"
        "类型必须编进 dll 才能被解析，没法运行时开关；删掉 Assemblies 目录它就一起没了。\n")
    if old_set in d:
        d = d.replace(old_set, new_set, 1)
        print("[替换] 设置界面段落 → 清单")
    else:
        print("[警告] 设置界面段落锚点没匹配上（可能已改过）")

    # ⑦ 其它说明分节
    subs2 = [
        ("\n改开关要重启游戏才生效 —— ", "\n■ 其它说明\n\n改开关要重启游戏才生效 —— "),
        ("\n模组信息\n", "\n■ 模组信息\n"),
        ("\n修复速览\n", "\n■ 修复速览（66 条，编号与设置界面一一对应）\n"),
    ]
    for old, new in subs2:
        if old in d:
            d = d.replace(old, new, 1)
            print("[替换] 小节标题：%s" % old.strip())

    if not args.write:
        print()
        print("（dry-run，未写盘）")
        print("新 description 长度 %d（原 %d），行数 %d（原 %d）"
              % (len(d), len(orig), d.count("\n") + 1, orig.count("\n") + 1))
        return 0

    if d == orig:
        print("没有变化，未写盘")
        return 0

    newtxt = txt[:m.start(1)] + d + txt[m.end(1):]
    out = (b"\xef\xbb\xbf" if bom else b"") + newtxt.encode("utf-8")
    with open(ABOUT, "wb") as fh:
        fh.write(out)
    print()
    print("已写入 %s（%d 字节）" % (ABOUT, len(out)))
    print("行数 %d → %d" % (orig.count("\n") + 1, d.count("\n") + 1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
