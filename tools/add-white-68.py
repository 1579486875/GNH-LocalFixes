# -*- coding: utf-8 -*-
"""把【68】descriptionWhitespaceTrim 登记进 check-newfix.py 的白名单。

文本级插入，保持原文件的 CRLF + 1 空格缩进风格不变。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PATH = os.path.join(HERE, "no-single-target.json")

KEY = "descriptionWhitespaceTrim"
REASON = (
    "【68】全局修补：目标是「所有 Def 的 description 字段」，不是某个模组提供的类型或 Def —— "
    "任何模组的 DefInjected 翻译都可能带入首尾空白，所以没有单一目标模组。"
    "受害最明显的是 LDR（工坊 3695591404）的 LDR_ISBM_silo，"
    "但只修它一家就违背了本项的用意（全库扫描出 20 条同类翻译，横跨 6 个模组）。"
)


def main():
    write = "--write" in sys.argv
    raw = open(PATH, "rb").read()
    text = raw.decode("utf-8-sig")

    if '"%s"' % KEY in text:
        print("已存在，跳过")
        return 0

    # 确认现有文件确实是 1 空格缩进 + CRLF
    nl = "\r\n" if "\r\n" in text else "\n"

    anchor_key = '"tsindGarageDoorsQuiet"'
    pos = text.find(anchor_key)
    if pos < 0:
        print("找不到锚点 %s" % anchor_key)
        return 1

    # 从 "tsindGarageDoorsQuiet" 后面那个 '{' 开始做花括号平衡
    brace = text.find("{", pos)
    if brace < 0:
        print("锚点后没有 {")
        return 1
    depth = 0
    end = -1
    for i in range(brace, len(text)):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                end = i
                break
    if end < 0:
        print("花括号不平衡")
        return 1

    # 该块后面紧跟着的应该是 items 的 '}' 或一个逗号
    after = text[end + 1:end + 40]
    addition = (
        "," + nl
        + '  "%s": {' % KEY + nl
        + '   "reason": %s' % json.dumps(REASON, ensure_ascii=False) + nl
        + "  }"
    )
    new_text = text[:end + 1] + addition + text[end + 1:]

    # 校验：仍是合法 JSON，且键数 +1
    old = json.loads(text)
    new = json.loads(new_text)
    if len(new["items"]) != len(old["items"]) + 1:
        print("键数不对：%d -> %d" % (len(old["items"]), len(new["items"])))
        return 1
    if new["items"][KEY]["reason"] != REASON:
        print("reason 写入不一致")
        return 1
    if list(new["items"].keys())[-1] != KEY:
        print("插入位置不是末尾：%s" % list(new["items"].keys())[-1])
        return 1

    print("OK：items %d -> %d，新键在末尾" % (len(old["items"]), len(new["items"])))
    print("原有键顺序不变：%s" % (list(new["items"].keys())[:-1] == list(old["items"].keys())))

    if write:
        # 注意：原文件**没有 BOM**，而 check-newfix.py 用 json.load(open(...)) 读它
        # （默认 utf-8，遇 BOM 会抛 JSONDecodeError）。所以这里也绝不能写 BOM。
        with open(PATH, "wb") as f:
            f.write(new_text.encode("utf-8"))
        print("已写入 %s" % PATH)
    else:
        print("[dry] 未写入")
    return 0


if __name__ == "__main__":
    sys.exit(main())
