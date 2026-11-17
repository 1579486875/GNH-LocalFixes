# -*- coding: utf-8 -*-
"""闸门：同名语言目录对必须逐文件一致（模板 §B.17.4）。

背景（1.7.5011.24 反编译实证）：
    LoadedLanguage(string folderName)
        legacyFolderName = (folderName.Contains("(")
            ? folderName.Substring(0, folderName.IndexOf("(") - 1)
            : folderName).Trim();

    AllDirectories:
        dir = <loadFolder>/Languages/<folderName>
        if (dir.Exists) { yield return dir; continue; }        // ← 命中就跳过 legacy
        dir = <loadFolder>/Languages/<legacyFolderName>
        if (dir.Exists) yield return dir;

本机 Prefs.langFolderName = "ChineseSimplified (简体中文)"
→ 游戏**只读带括号那套**；不带括号那套在本机永远不会被加载。
→ 所以两份必须一致；只改一份 = 改了没效果，而且 hash 核对也过得去（对比的也是那份）。

本闸门就是把「两套必须一致」变成可执行的检查。

退出码：0 = 全部一致（或只有单套）；1 = 有差异
"""
import hashlib
import os
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP = {'.git', 'bin', 'obj', 'node_modules', '.vs', '_backup', '_scratch'}


def walk_lang_roots(root):
    """找出所有形如 .../Languages 的目录，返回 [(父目录, Languages 路径)]"""
    out = []
    for base, dirs, _ in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP]
        if os.path.basename(base) == 'Languages':
            out.append(base)
            dirs[:] = []
    return out


def file_hashes(d):
    """返回 {相对路径: sha256}"""
    h = {}
    for base, dirs, fs in os.walk(d):
        dirs[:] = [x for x in dirs if x not in SKIP]
        for f in fs:
            p = os.path.join(base, f)
            rel = os.path.relpath(p, d).replace('\\', '/')
            try:
                h[rel] = hashlib.sha256(open(p, 'rb').read()).hexdigest()
            except Exception as e:
                h[rel] = 'ERR:%s' % e
    return h


def main():
    pairs = 0
    bad = 0
    for langroot in walk_lang_roots(ROOT):
        entries = sorted(os.listdir(langroot))
        dirs = [e for e in entries if os.path.isdir(os.path.join(langroot, e))]
        rel = os.path.relpath(langroot, ROOT).replace('\\', '/')
        print('\n[%s]  目录 %d 个' % (rel, len(dirs)))
        for d in dirs:
            if '(' not in d:
                continue
            legacy = d[:d.index('(') - 1].strip()
            if legacy not in dirs:
                print('  · %-38s → 无对应 legacy 目录（单套，OK）' % d)
                continue
            pairs += 1
            a = file_hashes(os.path.join(langroot, d))
            b = file_hashes(os.path.join(langroot, legacy))
            only_a = sorted(set(a) - set(b))
            only_b = sorted(set(b) - set(a))
            diff = sorted(k for k in set(a) & set(b) if a[k] != b[k])
            if only_a or only_b or diff:
                bad += 1
                print('  ✗ %-30s vs %-24s  仅括号套 %d / 仅 legacy %d / 内容不同 %d'
                      % (d, legacy, len(only_a), len(only_b), len(diff)))
                for k in only_a[:6]:
                    print('      仅括号套: %s' % k)
                for k in only_b[:6]:
                    print('      仅 legacy: %s' % k)
                for k in diff[:6]:
                    print('      内容不同: %s  %s… / %s…' % (k, a[k][:8], b[k][:8]))
            else:
                print('  ✓ %-30s vs %-24s  完全一致（%d 个文件）' % (d, legacy, len(a)))

    print('\n' + '=' * 72)
    if bad:
        print('❌ 有 %d 对语言目录不一致 —— 只改了一份，游戏读的是带括号那份' % bad)
        return 1
    print('✅ 语言目录对：%d 对，全部一致（无对应目录的单套已跳过）' % pairs)
    return 0


if __name__ == '__main__':
    sys.exit(main())
