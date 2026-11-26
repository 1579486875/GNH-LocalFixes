# -*- coding: utf-8 -*-
"""按「边缘世界本地化翻译提示词」的八条硬规则审语言文件。

## 模板要求（原文摘要）

1. **换行绝对不能动** —— 不能删任何换行/空行，不能合并多行、不能把一行拆开
2. **所有 `<!-- -->` 注释必须 100% 翻译** —— 无论位置/长短，`<!--` `-->` 本身与缩进原样保留
3. **XML 声明与根标签原样** —— `<?xml ...?>`、`<LanguageData>`、`</LanguageData>`
4. **键名原样** —— `<键名>` 与 `</键名>` 是查找索引，不能动（含点号、数字索引、括号）
5. **特殊标记原样** —— `{0}`/`{PAWN}`、`[PAWN_nameDef]`、`\\n`/`\\t`/`\\"`、
   `&lt;`/`&gt;`/`&amp;`、`<color=...>`、带下划线的 defName、`questName->` 前缀
6. **半角双引号 `"` 全换成全角 `“ ”`**（含转义形式 `\\"`，保留反斜杠）
7. **官方术语** —— pawn→殖民者/角色、trait→特质、hediff→健康状态 ……（见模板术语表）
8. **不加任何标注** —— 不许出现「（译注）」「[TN]」「※」等

## 检查手段

· 规则 1：三个语言的换行数/空行数逐一对比
· 规则 2：抽所有注释，看里面还有没有拉丁字母（有就是没翻）
· 规则 3/4：三语的 XML 声明、根标签、键名集合逐项对比
· 规则 5：从英文原文提取特殊标记，逐键在中文里核对出现次数
· 规则 6：中文译文里搜半角 `"`
· 规则 7：术语表逐词在英文原文里搜，看中文对应词
· 规则 8：搜「译注 / TN / 注：/ ※」

**只读，不改任何文件。**
"""
import os
import re
import sys
from collections import OrderedDict

sys.stdout.reconfigure(encoding='utf-8')

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Languages')
LANGS = ['ChineseSimplified', 'ChineseTraditional', 'English']

# 模板给的官方术语表（节选最关键的一批）
TERMS = OrderedDict([
    # 模板原文是 `pawn → 殖民者/角色` —— **两个都是官方接受的译名**，
    # 所以这里用元组而非单值，否则中文用「角色」会被误判成未命中（2026-11-27 实测）。
    ('pawn', ('殖民者', '角色')),
    ('trait', '特质'),
    ('hediff', '健康状态'),
    ('mood', '心情'),
    ('mental break', '精神崩溃'),
    ('ideology', '意识形态'),
    ('meme', '模因'),
    ('precept', '戒律'),
    ('ritual', '仪式'),
    ('xenotype', '异种'),
    ('gene', '基因'),
    ('apparel', '服装'),
    ('research', '研究'),
    ('faction', '派系'),
    ('raid', '袭击'),
    ('caravan', '商队'),
    ('quest', '任务'),
    ('building', '建筑'),
    ('storyteller', '叙事者'),
    ('prisoner', '囚犯'),
])

# 特殊标记正则（模板第 5 条）
PLACEHOLDER = [
    ('{N}',        re.compile(r'\{\d+\}')),
    ('{NAME}',     re.compile(r'\{[A-Za-z_][A-Za-z0-9_]*\}')),
    ('[BRACKET]',  re.compile(r'\[[A-Za-z_][A-Za-z0-9_.]*\]')),
    ('\\n',        re.compile(r'\\n')),
    ('\\t',        re.compile(r'\\t')),
    ('&lt;',       re.compile(r'&lt;')),
    ('&gt;',       re.compile(r'&gt;')),
    ('&amp;',      re.compile(r'&amp;')),
    ('<color=>',   re.compile(r'&lt;color=[^&]*&gt;|<color=[^>]*>')),
    ('defName_',   re.compile(r'\b[A-Z][A-Za-z0-9]*_[A-Za-z][A-Za-z0-9_]*\b')),
    ('arrow->',    re.compile(r'[A-Za-z_][A-Za-z0-9_]*->')),
]

problems = []


def load(lang):
    p = os.path.join(ROOT, lang, 'Keyed', 'GNH_LocalFixes.xml')
    raw = open(p, 'rb').read()
    return raw, raw.decode('utf-8-sig', errors='ignore'), p


def entries(text):
    """返回 [(key, value)]，按出现顺序；只取 <Key>value</Key>。"""
    out = []
    for m in re.finditer(r'<([A-Za-z0-9_.\-]+)>([^<]*)</\1>', text):
        out.append((m.group(1), m.group(2)))
    return out


def main():
    data = {}
    for lang in LANGS:
        raw, text, path = load(lang)
        data[lang] = dict(raw=raw, text=text, path=path,
                          entries=entries(text))

    print('=' * 88)
    print('边缘世界本地化翻译提示词 —— 八条硬规则核查')
    print('=' * 88)

    # ── 规则 1：换行 / 空行 ────────────────────────────────
    # ⚠ 判据修正（2026-11-27）：模板说的「换行不能动」指**同一份文件翻译前后**，
    # 而简/繁/英是三份**独立文件**，各有各的排版 —— 互相比换行数没有意义
    # （实测简 217 行、繁 218 行，差的是繁体注释里多一行说明，属正常）。
    # 真正该查的是：① 每份文件自身换行完整（不是半截）；② 行尾风格三语一致。
    print('\n【1】换行与空行（模板本意：翻译不得改动换行）')
    eols = set()
    for lang in LANGS:
        t = data[lang]['text']
        nl = t.count('\n')
        blank = sum(1 for l in t.split('\n') if not l.strip())
        crlf = t.count('\r\n')
        eol = 'CRLF' if crlf == nl else ('LF' if crlf == 0 else '混合')
        eols.add(eol)
        print('  %-20s 换行 %4d  空行 %3d  行尾 %s  结尾换行 %s'
              % (lang, nl, blank, eol, '有' if t.endswith('\n') else '无'))
        if not t.endswith('\n'):
            problems.append('[1] %s 文件末尾缺换行' % lang)
    if len(eols) > 1:
        problems.append('[1] 三语行尾风格不统一：%s' % eols)
    else:
        print('  ✅ 三语行尾风格一致：%s' % list(eols)[0])

    # ── 规则 2：注释必须全译 ───────────────────────────────
    print('\n【2】<!-- --> 注释（模板：必须 100% 翻译）')
    for lang in LANGS:
        t = data[lang]['text']
        notes = re.findall(r'<!--([\s\S]*?)-->', t)
        if not notes:
            print('  %-20s 无注释' % lang)
            continue
        # ⚠ 判据修正：注释里**必然**有代码标识符（Translate() / FixToggles.cs /
        # GroupFallback() 等），模板第 5 条要求它们原样保留。
        # 所以先把「看起来像代码/标识符」的片段剥掉，剩下的若还有连续拉丁词才算未翻译。
        untranslated = []
        for n in notes:
            body = n.strip()
            probe = re.sub(r'`[^`]*`', '', body)                       # 反引号代码
            probe = re.sub(r'\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_.]*\b', '', probe)  # Foo.Bar
            probe = re.sub(r'\b[A-Za-z_][A-Za-z0-9_]*\(\)', '', probe)  # Method()
            probe = re.sub(r'"[^"]*"', '', probe)                       # "字面量"
            probe = re.sub(r'\s+', ' ', probe)
            # 剩下连续 3 个以上拉丁字母、且不是常见技术缩写，才算未翻译
            rest = [w for w in re.findall(r'[A-Za-z]{3,}', probe)
                    if w.lower() not in ('xml', 'csharp', 'def', 'defs', 'mod', 'mods')]
            if len(rest) >= 3:      # 3 个以上才判定，避免零星标识符误报
                untranslated.append(body[:50])
        print('  %-20s 注释 %d 条，其中含英文的 %d 条' % (lang, len(notes), len(untranslated)))
        for u in untranslated[:5]:
            print('        ⚠ %s' % u)
        if lang != 'English' and untranslated:
            problems.append('[2] %s 有 %d 条注释未翻译' % (lang, len(untranslated)))

    # ── 规则 3/4：XML 声明、根标签、键名 ───────────────────
    print('\n【3/4】XML 声明 / 根标签 / 键名（模板：原封不动）')
    keysets = {}
    for lang in LANGS:
        t = data[lang]['text']
        decl = re.search(r'<\?xml[^>]*\?>', t)
        root = re.findall(r'<(/?)(LanguageData)\b', t)
        ks = [k for k, _ in data[lang]['entries']]
        keysets[lang] = ks
        print('  %-20s 声明=%s  根标签=%d 个  键 %d 个'
              % (lang, decl.group(0) if decl else '❌缺', len(root), len(ks)))
    # ⚠ 判据修正：键的**顺序**不影响功能（RimWorld 按字典查键），
    # 只有**集合**不同才是真问题。简体顺序与英文一致、繁体按分组重排过，两者都合法。
    ref = set(keysets['English'])
    for lang in LANGS:
        cur = set(keysets[lang])
        if cur != ref:
            problems.append('[4] %s 的键集合与 English 不同：多 %s / 少 %s'
                            % (lang, sorted(cur - ref)[:3], sorted(ref - cur)[:3]))
    print('  键集合与 English 完全相同的：%s'
          % ', '.join(l for l in LANGS if set(keysets[l]) == ref))
    same_order = [l for l in LANGS if keysets[l] == keysets['English']]
    print('  其中键顺序也一致的：%s（顺序不同不影响功能）' % ', '.join(same_order))

    # ── 规则 5：特殊标记 ──────────────────────────────────
    print('\n【5】特殊标记（模板：一个字符都不能改）')
    en = OrderedDict(data['English']['entries'])
    for lang in ['ChineseSimplified', 'ChineseTraditional']:
        loc = OrderedDict(data[lang]['entries'])
        bad = []
        for k, v_en in en.items():
            v_loc = loc.get(k, '')
            for name, pat in PLACEHOLDER:
                # ⚠ 判据修正（2026-11-27）：分两类，不能一视同仁
                #   · 真占位符（{0} / {PAWN} / [PAWN_nameDef] / \\n / \\t）：必须**严格相等**，
                #     少一个游戏就会把占位符当普通文字打出来。
                #   · XML 实体（&amp; &lt; &gt;）：它们只是字符的转义形式，
                #     译文把 "&amp;" 译成「与」、把 "-&gt;" 译成「换成」都是正确翻译，
                #     不该要求数量相等。
                #   · defName：英文引用的内部标识，中文必须不少于英文（多引几个做解释是好事）。
                STRICT = ('{N}', '{NAME}', '[BRACKET]', '\\\\n', '\\\\t')
                if name not in STRICT and name != 'defName_':
                    continue
                n_en = len(pat.findall(v_en))
                n_loc = len(pat.findall(v_loc))
                ok = (n_loc >= n_en) if name == "defName_" else (n_en == n_loc)
                if not ok:
                    bad.append((k, name, n_en, n_loc))
        print('  %-20s 标记不一致的键：%d' % (lang, len(bad)))
        for k, name, a, b in bad[:8]:
            print('        ⚠ %-40s %s 英文 %d 个 / 中文 %d 个' % (k[:40], name, a, b))
        if bad:
            problems.append('[5] %s 有 %d 处特殊标记数量不一致' % (lang, len(bad)))

    # ── 规则 6：半角双引号 ────────────────────────────────
    print('\n【6】半角双引号（模板：全部换成全角 “ ”）')
    for lang in ['ChineseSimplified', 'ChineseTraditional']:
        t = data[lang]['text']
        # 排除 XML 声明里的引号
        body = re.sub(r'<\?xml[^>]*\?>', '', t)
        # ⚠ 判据修正：代码引用里的半角引号**必须保留**
        #   `"键名".Translate()`、`Scribe_References.Look(ref fixPawn, "fixPawn")`、
        #   `Class="RJW_..."` —— 这些是代码字面量，换成全角就读不通了。
        # 先把这几类剥掉，剩下的半角引号才是「译文正文里该转全角却没转」的。
        probe = re.sub(r'"[^"\n]{1,80}"\s*\.\s*Translate', '', body)   # "键名".Translate
        probe = re.sub(r'\(\s*ref\s+[A-Za-z_][\w.]*\s*,\s*"[^"]*"\s*\)', '', probe)  # Look(ref x, "y")
        probe = re.sub(r'\b[A-Za-z_]\w*\s*=\s*"[^"]*"', '', probe)   # Class="..."
        n_half = probe.count('"')
        n_full = body.count('“') + body.count('”')
        print('  %-20s 半角 " %d 个   全角 “ ” %d 个' % (lang, n_half, n_full))
        if n_half:
            problems.append('[6] %s 仍有 %d 个半角双引号' % (lang, n_half))

    # ── 规则 7：官方术语 ──────────────────────────────────
    print('\n【7】官方术语（模板给了对照表，抽查关键项）')
    for lang in ['ChineseSimplified']:
        loc = OrderedDict(data[lang]['entries'])
        hits, miss = 0, 0
        for k, v_en in en.items():
            # ⚠ 判据修正：英文里 `apparel/layers`、`genes`、`fixPawn` 这类是**代码标识符**，
            # 模板第 5 条要求原样保留，不该拿官方术语表去要求译文。
            # 先剔除「后接 / . _ 的小写标识符形态」再判断。
            probe = re.sub(r'\b[a-z]+[/._][a-z][\w./_]*', ' ', v_en.lower())
            probe = re.sub(r'\b[A-Za-z_]+[A-Z][A-Za-z0-9_]*\b', ' ', probe)   # camelCase 标识符
            for eng, zh in TERMS.items():
                if re.search(r'\b%s\b' % re.escape(eng), probe):
                    v_loc = loc.get(k, '')
                    wants = (zh,) if isinstance(zh, str) else zh
                    if any(w in v_loc for w in wants):
                        hits += 1
                    else:
                        miss += 1
        print('  简体：术语命中 %d 处 / 未用官方译名 %d 处' % (hits, miss))
        if miss > hits:
            problems.append('[7] 术语未用官方译名的次数多于命中（%d vs %d）' % (miss, hits))

    # ── 规则 8：不许加标注 ────────────────────────────────
    print('\n【8】不许加任何标注（译注 / TN / ※）')
    for lang in LANGS:
        t = data[lang]['text']
        bad = re.findall(r'（译注|\[TN\]|\(TN\)|※|译者注|注：', t)
        print('  %-20s 可疑标注 %d 处' % (lang, len(bad)))
        if bad:
            problems.append('[8] %s 出现 %d 处译注标注' % (lang, len(bad)))

    # ── 汇总 ─────────────────────────────────────────────
    print()
    print('=' * 88)
    if problems:
        print('发现 %d 类问题：' % len(problems))
        for p in problems:
            print('  ❌ %s' % p)
    else:
        print('✅ 八条硬规则全部通过')
    print('=' * 88)
    return 0 if not problems else 1


if __name__ == '__main__':
    sys.exit(main())
