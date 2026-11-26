# -*- coding: utf-8 -*-
r"""闸门：开关的 `requiresModById` 指向的模组必须真的存在于模组库。

## 为什么

`FixToggles.cs` 里有一张 `requiresModById`：开关 → 前置模组的 packageId。
某个开关登记了前置，界面上就会显示「目标未安装 / 未启用」，玩家一看就知道
「这条修的东西不在这里，不用管」。

**但如果前置写成死 id（模组改名、下架、拼错），这个开关就永远不可能激活** ——
它既不会生效，也不会告诉玩家为什么，等于一条「静默消失的修复」。
这类错不会报红字、不会崩，只有人拿着清单一条条核才会发现。

## 判据

对 `requiresModById` 里的每一个 packageId（`;` 可分隔多个）：

- 在 `mod_manager.db` 的 `modasset` 里能查到（`LOWER()` 比对）→ **存在**（OK）
- 查不到 → 再去磁盘上扫 `About/About.xml` 的 `<packageId>` 兜底
- 两边都没有 → **缺陷**：这个开关永远不能被激活

> 模板 §B.12b 的坑：读 About.xml 时不能「取第一个 `<packageId>`」——
> 不少模组把 `<modDependencies>` 写在自身 `<packageId>` 之前。
> 本脚本在磁盘兜底时才解析，并且**只取 `<ModMetaData>` 的直接子节点**。

## 用法

    python 工具/check-requires-mods.py            # 只报缺陷 + 未安装提示
    python 工具/check-requires-mods.py -v         # 连「已存在」的一起列

退出码：0 = 无缺陷；1 = 有死 id
"""
import json
import os
import re
import sqlite3
import sys
import xml.etree.ElementTree as ET

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, '源码', 'FixToggles.cs')
RIMCROW_DATA = (r'C:/Users/asus/Desktop/其他杂项/游戏和模组启动器及管理工具/'
                r'RimModManager/data/mod_manager.db')
DISK_ROOTS = [
    r'D:/steam/steamapps/common/RimWorld/Mods',
    r'D:/steam/steamapps/workshop/content/294100',
    r'D:/RimWorld-1.6/Mods',
]


def parse_requires(src):
    m = re.search(r'requiresModById\s*=\s*new\s+Dictionary<string,\s*string>\s*'
                  r'(?:\([^)]*\))?\s*\{', src)
    if not m:
        return None
    seg = src[m.end():]
    depth, j = 1, 0
    while j < len(seg) and depth:
        if seg[j] == '{':
            depth += 1
        elif seg[j] == '}':
            depth -= 1
        j += 1
    block = seg[:j - 1]
    pairs = re.findall(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', block)
    out = {}
    for toggle, spec in pairs:
        for p in spec.split(';'):
            p = p.strip()
            if p:
                out.setdefault(p.lower(), []).append(toggle)
    return out


def db_ids():
    ids = set()
    if not os.path.exists(RIMCROW_DATA):
        return ids
    try:
        c = sqlite3.connect('file:%s?mode=ro' % RIMCROW_DATA.replace('\\', '/'), uri=True)
        for (pid,) in c.execute('select package_id from modasset'):
            if pid:
                ids.add(pid.lower())
    except Exception as e:
        print('!! 读 mod_manager.db 失败：%s' % e)
    return ids


def about_pid(p):
    """只取 <ModMetaData> 的直接子节点 —— 避开 <modDependencies> 里那些。"""
    for name in ('About.xml', 'about.xml'):
        f = os.path.join(p, 'About', name)
        if not os.path.exists(f):
            continue
        try:
            root = ET.parse(f).getroot()
        except Exception:
            return None
        for ch in root:
            if ch.tag == 'packageId' and (ch.text or '').strip():
                return ch.text.strip().lower()
    return None


def disk_ids():
    ids = set()
    for root in DISK_ROOTS:
        if not os.path.isdir(root):
            continue
        for d in os.listdir(root):
            p = os.path.join(root, d)
            if not os.path.isdir(p):
                continue
            pid = about_pid(p)
            if pid:
                ids.add(pid)
    return ids


def main():
    verbose = '-v' in sys.argv
    # 2026-11-17 加：环境缺失按 skip（判据依赖 RimCrow 数据库与本机磁盘）。
    if not os.path.exists(RIMCROW_DATA) and not os.path.isdir(DISK_ROOTS[0]):
        print('⏭ 跳过：既没有 RimCrow 数据库，也没有 RimWorld Mods 目录')
        print('   本项**未核**（不是通过）。')
        return 0
    src = open(SRC, 'rb').read().decode('utf-8-sig')
    req = parse_requires(src)
    if req is None:
        print('❌ 在 FixToggles.cs 里找不到 requiresModById 初始化块')
        return 1
    print('requiresModById：%d 个 packageId（%d 个开关登记）'
          % (len(req), sum(len(v) for v in req.values())))

    db = db_ids()
    print('mod_manager.db 里的 packageId：%d' % len(db))
    disk = set()
    missing = [p for p in req if p not in db]
    if missing:
        print('库里查不到 %d 个，去磁盘兜底扫描…' % len(missing))
        disk = disk_ids()
        print('磁盘 About.xml 里的 packageId：%d' % len(disk))

    dead = []
    for p in sorted(req):
        where = 'db' if p in db else ('disk' if p in disk else None)
        if where is None:
            dead.append(p)
            print('  ❌ %-46s 库/盘都查不到 ← 开关：%s'
                  % (p, ', '.join(req[p])))
        elif verbose:
            print('  ✅ %-46s (%s)' % (p, where))

    print('\n' + '=' * 74)
    if dead:
        print('❌ %d 个死 id —— 这些开关永远不可能被激活，等于静默消失的修复' % len(dead))
        return 1
    print('✅ 全部 %d 个前置 id 都能在库或磁盘上找到' % len(req))
    return 0


if __name__ == '__main__':
    sys.exit(main())
