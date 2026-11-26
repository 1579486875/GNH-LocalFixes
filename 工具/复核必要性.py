# -*- coding: utf-8 -*-
"""§1.4 必要性复核 —— 判据 B（目标有没有变）。

## 模板要求（原文）

> 每轮都要跑下面两个判据，两个都成立才判「冗余」：
>   A/B 移除实测 —— 关掉开关，重跑场景，问题不复现
>   反编译目标当前版本 —— 看它**现在**的方法体 / Def
>
> **全量深查不现实 —— 先粗筛**：
>   目标模组在上次审查之后更新过  → 必须重查
>   这条修复当初是绕过对方缺陷的  → 必须重查
>   两条都不满足                  → 记「仍必要（目标未变）」，不必反编译
>
> ⚠ 对「本身就由一堆补丁组成的模组」几乎不过滤，换成：
>   修原版行为的项 → 游戏本体程序集有没有变（Version.txt + Assembly-CSharp.dll 的 mtime）
>   修第三方模组的项 → 目标模组目录内最新 mtime / 版本号，变了才深查

## 本脚本做什么

1. 从 `源码/FixToggles.cs` 的 `requiresModById` 字典里提取全部目标 packageId
2. 在本地 `Mods` 与工坊 `294100` 里定位这些模组目录
3. 取每个目录内**最新文件的 mtime**，与本轮审查的启动时刻对比
4. 输出三栏：**已变（需重查）** / **未变（仍必要）** / **找不到（目标缺失）**

**只读，不改任何文件。**
"""
import os
import re
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIXTOGGLES = os.path.join(REPO, '源码', 'FixToggles.cs')

MOD_ROOTS = [
    r'D:\steam\steamapps\common\RimWorld\Mods',
    r'D:\steam\steamapps\workshop\content\294100',
]

# 本轮审查的启动时刻（基线）：早于这个时间点就算「上次审查之后没动过」
# 用本脚本运行时刻的当天 00:00 作分界 —— 保守但可复现。
TODAY0 = time.mktime(time.strptime(time.strftime('%Y-%m-%d'), '%Y-%m-%d'))


def read_package_ids():
    """从 requiresModById 字典里提取 ``{ "开关id", "packageId[;packageId2]" }``。"""
    t = open(FIXTOGGLES, encoding='utf-8').read()
    m = re.search(r'requiresModById\s*=\s*\r?\n\s*new Dictionary<string, string>[^{]*\{([\s\S]*?)\r?\n\s*\};', t)
    if not m:
        return {}
    body = m.group(1)
    pairs = {}
    for line in body.splitlines():
        s = line.strip()
        if not s.startswith('{'):
            continue
        mm = re.match(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', s)
        if not mm:
            continue
        toggle_id, ids = mm.group(1), mm.group(2)
        for one in ids.split(';'):
            one = one.strip()
            if one:
                pairs.setdefault(toggle_id, []).append(one)
    return pairs


def find_mod_dir(pkg_id):
    """在本地 Mods 与工坊目录里找到 packageId 对应的模组目录。"""
    want = pkg_id.lower()
    for root in MOD_ROOTS:
        if not os.path.isdir(root):
            continue
        # 本地 Mods 是「一个模组一个子目录」
        if root.endswith('Mods'):
            for name in os.listdir(root):
                d = os.path.join(root, name)
                if not os.path.isdir(d):
                    continue
                ab = os.path.join(d, 'About', 'About.xml')
                if not os.path.isfile(ab):
                    continue
                try:
                    txt = open(ab, encoding='utf-8', errors='ignore').read()
                except Exception:
                    continue
                mm = re.search(r'<packageId>\s*([^<\s]+)', txt, re.I)
                if mm and mm.group(1).strip().lower() == want:
                    return d, '本地'
        else:
            # 工坊是「一个数字 ID 一个子目录」，先扫 About
            for name in os.listdir(root):
                d = os.path.join(root, name)
                if not os.path.isdir(d):
                    continue
                ab = os.path.join(d, 'About', 'About.xml')
                if not os.path.isfile(ab):
                    continue
                try:
                    txt = open(ab, encoding='utf-8', errors='ignore').read()
                except Exception:
                    continue
                mm = re.search(r'<packageId>\s*([^<\s]+)', txt, re.I)
                if mm and mm.group(1).strip().lower() == want:
                    return d, '工坊'
    return None, None


def newest_mtime(d):
    """目录内最新文件的 mtime（跳过 obj/bin 与备份目录，那些不代表模组本身更新）。"""
    newest = 0.0
    newest_file = ''
    for base, dirs, names in os.walk(d):
        low = base.lower()
        if 'obj' in low or '\\bin' in low or '\\.git' in low:
            dirs[:] = []
            continue
        for fn in names:
            p = os.path.join(base, fn)
            try:
                m = os.path.getmtime(p)
            except Exception:
                continue
            if m > newest:
                newest = m
                newest_file = p
    return newest, newest_file


def main():
    pairs = read_package_ids()
    all_ids = sorted({i for v in pairs.values() for i in v})
    print('requiresModById 覆盖 %d 个开关、%d 个 packageId' % (len(pairs), len(all_ids)))
    print('基线（当天 0 点）: %s' % time.strftime('%Y-%m-%d %H:%M', time.localtime(TODAY0)))
    print()

    changed, unchanged, missing = [], [], []
    for pkg in all_ids:
        d, kind = find_mod_dir(pkg)
        if not d:
            missing.append((pkg, ''))
            continue
        mt, mf = newest_mtime(d)
        rec = (pkg, kind, time.strftime('%Y-%m-%d %H:%M', time.localtime(mt)) if mt else '?', d)
        if mt > TODAY0:
            changed.append(rec)
        else:
            unchanged.append(rec)

    def dump(title, rows, note=''):
        print('=== %s：%d 个 ===' % (title, len(rows)))
        if note:
            print('  %s' % note)
        if not rows:
            print('  （无）')
        for r in rows:
            if len(r) == 2:
                print('  · %-46s %s' % (r[0][:46], '目录找不到' if not r[1] else r[1]))
            else:
                print('  · %-46s %-4s 最新 %s' % (r[0][:46], r[1], r[2]))
        print()

    dump('① 上次审查之后更新过 —— 必须重查', changed,
         '这些模组的文件在新基线之后被动过，按 §1.4 要重跑 A/B 或反编译')
    dump('② 未变 —— 记「仍必要（目标未变）」', unchanged,
         '按 §1.4 粗筛表，不必再反编译')
    dump('③ 目录里找不到 —— 目标缺失（可能没装/已改名/只在别处）', missing)

    print('=== 结论 ===')
    print('  需重查 %d / 仍必要 %d / 目标缺失 %d' % (len(changed), len(unchanged), len(missing)))
    if changed:
        print('  ⚠ 需重查的项见上面第 ① 节，优先用「目标消失」类判据（§1.4 末条）')
    else:
        print('  ✅ 没有目标变动的项 —— 本轮的「修第三方」补丁全部维持「仍必要」判定')


if __name__ == '__main__':
    main()
