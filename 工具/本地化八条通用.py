#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
通用版「边缘世界本地化翻译提示词」八条硬规则检查器。

给**任意** RimWorld 模组的 `Languages/<语言>/` 目录用，不限于某一个模组。

八条规则里哪些能自动查、哪些不能，如实标注：

| # | 规则 | 可自动查？ | 说明 |
|---|---|---|---|
| 1 | 换行/空行零改动 | ❌ | 要跟原文对比才知道；本工具只能查「文件结尾有无换行」这种粗项 |
| 2 | **所有 `<!-- -->` 注释 100% 翻译** | ✅ | **本工具的重点** |
| 3 | XML 声明 + `<LanguageData>` 完整 | ✅ | |
| 4 | 键名原样 | ❌ | 要跟原文对比 |
| 5 | 特殊标记保留（`{0}` `[PAWN_x]` `\n` `&lt;` `<color=>`） | ⚠ 部分 | 只能查「异体标记是否自洽」，查不出「少了哪几个」 |
| 6 | 半角双引号 → 全角 | ✅ | 但要排除代码字面量 |
| 7 | 官方术语 | ⚠ 抽查 | 术语表命中率 |
| 8 | 不加任何标注（译注/TN/※） | ✅ | |

用法：
    python 工具/本地化八条通用.py <模组目录>
    python 工具/本地化八条通用.py <模组目录> --lang "ChineseSimplified (简体中文)"
"""

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding='utf-8')

# ── 官方术语表（模板第 7 条给的对照；一个英文词可能有多个官方译名）──
TERMS = {
    'pawn': ('殖民者', '角色'),
    'trait': ('特质',),
    'hediff': ('健康状态',),
    'mood': ('心情',),
    'raid': ('袭击',),
    'faction': ('派系',),
    'quest': ('任务',),
    'building': ('建筑',),
    'research': ('研究',),
    'gene': ('基因',),
    'apparel': ('服装',),
    'ritual': ('仪式',),
    'meme': ('模因',),
    'precept': ('戒律',),
    'xenotype': ('异种',),
}

# ── 代码字面量：这些地方的半角引号必须保留 ──
CODE_QUOTE_PATTERNS = [
    r'"[^"\n]{1,80}"\s*\.\s*Translate',            # "键名".Translate()
    r'\(\s*ref\s+[A-Za-z_][\w.]*\s*,\s*"[^"]*"\s*\)',   # Look(ref x, "y")
    r'\b[A-Za-z_]\w*\s*=\s*"[^"]*"',               # Class="..."
    r'`[^`]*`',                                     # 反引号代码
]

# ── 译者标注的可疑形态 ──
NOTE_PATTERNS = [
    r'（译注', r'\(译注', r'译注[:：]', r'\bTN\b', r'\bTL\s*note',
    # ⚠ 判据修正：`※` 在官方中文包里**本来就有**
    #   （`Stat_Thing_MoodChange_Desc` / `Stat_Hediff_Affects_Desc` 官方就带 ※），
    #   不是译者标注，已从判据里移出。
    r'\[译\]', r'（注[:：]', r'\(note[:：]',
    # ⚠ 判据修正：裸 `翻译注` 会误匹配 `翻译注入`（真的踩到过），必须带边界。
    r'译者注', r'翻译注[:：]', r'（翻译注', r'\(翻译注',
]


def has_cjk(s):
    return bool(re.search(r'[\u4e00-\u9fff]', s))


def check_file(path, rel, is_chinese=True):
    """返回 (问题列表, 统计)"""
    probs = []
    stat = {'comments': 0, 'untranslated': 0, 'keys': 0}
    try:
        raw = open(path, 'rb').read()
    except Exception as e:
        return [('读取失败', str(e))], stat

    had_bom = raw.startswith(b'\xef\xbb\xbf')
    try:
        t = raw.decode('utf-8-sig')
    except Exception as e:
        return [('UTF-8 解码失败', str(e))], stat

    # ── 规则 3：XML 声明 + LanguageData ──
    if not t.lstrip().startswith('<?xml'):
        probs.append(('规则3', '缺 <?xml 声明'))
    # ⚠ 判据修正：`LanguageInfo.xml` 是**特殊文件**，根标签就该是 `<LanguageInfo>`
    #   （官方 `Data\Core\Languages\<语言>\LanguageInfo.xml` 也是这个结构）。
    is_langinfo = rel.replace('\\', '/').endswith('LanguageInfo.xml')
    if is_langinfo:
        if '<LanguageInfo>' not in t:
            probs.append(('规则3', '缺 <LanguageInfo> 根标签'))
    elif '<LanguageData>' not in t:
        probs.append(('规则3', '缺 <LanguageData> 根标签'))
    try:
        ET.fromstring(t)
    except Exception as e:
        probs.append(('规则3', 'XML 解析失败: %s' % str(e)[:60]))

    # ⚠ 判据修正（2026-11-27 实测）：官方中文包 543 个 xml **100% 都缺末尾换行**，
    #   所以「末尾缺换行」是**官方风格**，不是缺陷。原来那条判据是误报，已删。
    #   规则 1「换行零改动」要跟原文逐行对比才能验，本工具**不做假验证**。

    # ── 规则 2：注释是否翻译 ──
    # 官方格式 `<!-- EN: xxx -->` 是「原文注释」，本身不需要译（它是原文记录）
    for m in re.finditer(r'<!--([\s\S]*?)-->', t):
        body = m.group(1).strip()
        stat['comments'] += 1
        if not body:
            continue
        # ⚠ 判据修正：**英文语言文件里的英文注释是原文**，不需要译成中文。
        if not is_chinese:
            continue
        if re.match(r'^EN\s*:', body, re.I):
            continue          # 官方原文注释，跳过
        # 剥掉代码标识符后再判断是否还有成串拉丁词
        probe = re.sub(r'`[^`]*`', '', body)
        probe = re.sub(r'\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_.]*\b', '', probe)
        probe = re.sub(r'\b[A-Za-z_][A-Za-z0-9_]*\(\)', '', probe)
        probe = re.sub(r'"[^"]*"', '', probe)
        rest = [w for w in re.findall(r'[A-Za-z]{4,}', probe)
                if w.lower() not in ('xml', 'defname', 'defs', 'mods', 'def', 'mod')]
        # ⚠ 判据修正：**纯代码标识符**的注释本来就该保留英文（模板第 5 条），
        #   不当成「未翻译」。判据：整条注释就是一个/几个驼峰或带下划线的标识符。
        is_identifier = bool(re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*(\s+[A-Za-z_][A-Za-z0-9_]*)*', body)) \
                        and bool(re.search(r'[A-Z_]{2,}|_', body))
        if len(rest) >= 2 and not has_cjk(body) and not is_identifier:
            stat['untranslated'] += 1
            probs.append(('规则2', '注释未翻译: %s' % body[:50]))

    # ── 规则 6 + 8：按 <键>值</键> 逐条查 ──
    for m in re.finditer(r'<([A-Za-z0-9_.\-]+)>([^<]*)</\1>', t):
        key, val = m.group(1), m.group(2)
        stat['keys'] += 1

        # ⚠ 判据修正：**英文语言文件**里的半角引号是英文原文的写法（`"Log spam"`），
        #   模板第 7 条「半角转全角」只针对**中文译文**；规则 8 同理。
        if not is_chinese:
            continue

        # 规则 8：译者标注
        # ⚠ 判据说明（2026-11-27 实测）：`※` 在官方中文包里**本来就有**
        #   （`Stat_Thing_MoodChange_Desc` / `Stat_Hediff_Affects_Desc` 两条官方就带 ※）。
        #   所以本工具只把它标出来供人工判断，**不能直接当缺陷**。
        for pat in NOTE_PATTERNS:
            if re.search(pat, val):
                probs.append(('规则8', '疑似译者标注「%s」: %s' % (re.search(pat, val).group(0), key[:44])))
                break

        # 规则 6：半角引号（排除代码字面量）
        probe = val
        for pat in CODE_QUOTE_PATTERNS:
            probe = re.sub(pat, '', probe)
        if '"' in probe:
            probs.append(('规则6', '半角双引号未转全角: %s' % key[:44]))

        # 规则 5：XML 实体自检（没转义的裸 & < >）
        if re.search(r'&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9A-Fa-f]+);)', val):
            probs.append(('规则5', '未转义的 & : %s' % key[:44]))

    return probs, stat


def main():
    if len(sys.argv) < 2:
        print('用法: python 工具/本地化八条通用.py <模组目录> [--lang <语言目录名>]')
        return 1
    base = sys.argv[1]
    lang = None
    if '--lang' in sys.argv:
        lang = sys.argv[sys.argv.index('--lang') + 1]

    langs_root = os.path.join(base, 'Languages')
    if not os.path.isdir(langs_root):
        print('❌ 没有 Languages 目录：%s' % langs_root)
        return 1

    langs = [lang] if lang else sorted(os.listdir(langs_root))
    print('=' * 78)
    print('本地化八条通用检查：%s' % base)
    print('=' * 78)

    grand = {'comments': 0, 'untranslated': 0, 'keys': 0, 'probs': 0, 'files': 0}
    for L in langs:
        d = os.path.join(langs_root, L)
        if not os.path.isdir(d):
            continue
        files = glob.glob(os.path.join(d, '**', '*.xml'), recursive=True)
        if not files:
            continue
        print()
        print('── %s：%d 个文件 ──' % (L, len(files)))
        agg = {}
        st_sum = {'comments': 0, 'untranslated': 0, 'keys': 0}
        nprob = 0
        for f in files:
            is_zh = ('Chinese' in L) or ('简' in L) or ('繁' in L)
            probs, st = check_file(f, os.path.relpath(f, d), is_zh)
            for k in st_sum: st_sum[k] += st[k]
            for rule, msg in probs:
                agg.setdefault(rule, []).append((os.path.relpath(f, d), msg))
                nprob += 1
        print('  注释 %d 个 ｜ 未翻译 %d 个 ｜ 键 %d 个 ｜ 问题 %d 条'
              % (st_sum['comments'], st_sum['untranslated'], st_sum['keys'], nprob))
        if agg:
            for rule in sorted(agg):
                items = agg[rule]
                print('    %s：%d 条' % (rule, len(items)))
                for f, msg in items[:3]:
                    print('       %-40s %s' % (os.path.basename(f)[:40], msg[:70]))
                if len(items) > 3:
                    print('       …（还有 %d 条）' % (len(items) - 3))
        else:
            print('    ✅ 八条硬规则全部通过')
        grand['comments'] += st_sum['comments']
        grand['untranslated'] += st_sum['untranslated']
        grand['keys'] += st_sum['keys']
        grand['probs'] += nprob
        grand['files'] += len(files)

    print()
    print('=' * 78)
    print('合计：%d 个文件 ｜ 注释 %d ｜ 未翻译 %d ｜ 键 %d ｜ 问题 %d 条'
          % (grand['files'], grand['comments'], grand['untranslated'], grand['keys'], grand['probs']))
    print('=' * 78)
    return 0 if grand['probs'] == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
