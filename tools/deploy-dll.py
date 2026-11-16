#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""deploy-dll.py · 把仓库里的 DLL/About/Languages 部署到 1.7 与 1.6 两个副本。

## 为什么单独做一个

游戏运行时 DLL 被占用（`Device or resource busy`），改不了。
所以本脚本**先检查游戏是否在跑**：在跑就明确告诉你「关掉游戏再来」，
而不是让你对着一串 `cp: Device or resource busy` 猜。

## 用法

    python tools/deploy-dll.py            # 只检查，不写
    python tools/deploy-dll.py --write    # 真部署

## 部署什么

| 文件 | 1.7 | 1.6 |
|---|---|---|
| `bin/Release/Assemblies/GNH.LocalFixes.dll` | ✅ | ✅ |
| `About/About.xml` | ✅ | ✅ |
| `Languages/{S,T,E}/Keyed/GNH_LocalFixes.xml` | ✅ | ✅ |

⚠ 不部署 `Patches/`（那是仓库与部署共享的同一份，且本脚本不负责 XML 补丁的版本差异）。
"""
import os, sys, shutil, subprocess, hashlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

TARGETS = [
    (r"D:\steam\steamapps\common\RimWorld\Mods\模组兼容修复补丁", '1.7'),
    (r"D:\RimWorld-1.6\Mods\模组兼容修复补丁", '1.6'),
]

SRC_DLL = os.path.join(ROOT, 'bin', 'Release', 'Assemblies', 'GNH.LocalFixes.dll')
SRC_AB = os.path.join(ROOT, 'About', 'About.xml')
LANGS = ('ChineseSimplified', 'ChineseTraditional', 'English')

WRITE = '--write' in sys.argv


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def game_running():
    try:
        r = subprocess.run(['tasklist'], capture_output=True, text=True, errors='ignore')
        return 'RimWorld.exe' in (r.stdout or '')
    except Exception:
        return False


print(f"仓库 DLL: {os.path.getsize(SRC_DLL):,} B" if os.path.exists(SRC_DLL) else "❌ 仓库 DLL 不存在（先编译）")
if not os.path.exists(SRC_DLL):
    sys.exit(1)

running = game_running()
print(f"游戏进程: {'**正在运行**（DLL 会被占用）' if running else '未运行'}")

total = miss = same = diff = 0
for d, label in TARGETS:
    if not os.path.isdir(d):
        print(f"  ⚠ [{label}] 目标目录不存在：{d}")
        continue
    pairs = [(SRC_DLL, os.path.join(d, 'Assemblies', 'GNH.LocalFixes.dll')),
             (SRC_AB, os.path.join(d, 'About', 'About.xml'))]
    for L in LANGS:
        pairs.append((os.path.join(ROOT, 'Languages', L, 'Keyed', 'GNH_LocalFixes.xml'),
                      os.path.join(d, 'Languages', L, 'Keyed', 'GNH_LocalFixes.xml')))
    for src, dst in pairs:
        if not os.path.exists(src):
            miss += 1
            print(f"  ⚠ [{label}] 源缺失：{os.path.relpath(src, ROOT)}")
            continue
        total += 1
        if os.path.exists(dst) and sha(src) == sha(dst):
            same += 1
            continue
        diff += 1
        if not WRITE:
            continue
        try:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
        except PermissionError:
            print(f"  ❌ [{label}] 写不进去（被占用）：{os.path.basename(dst)} —— 请先关游戏")
            continue
        except Exception as ex:
            print(f"  ❌ [{label}] {os.path.basename(dst)}：{ex}")
            continue
        ok = sha(src) == sha(dst)
        print(f"  {'✅' if ok else '❌'} [{label}] {os.path.basename(dst)}")

print(f"\n文件 {total} 个：一致 {same} / 需更新 {diff} / 源缺失 {miss}")
if diff and not WRITE:
    if running:
        print("\n⚠ 游戏正在运行 —— 先关掉游戏，再加 --write 部署（DLL 会被占用）。")
    else:
        print("\n加 --write 才会真部署。")
elif WRITE:
    print("\n✅ 部署完成" if diff == 0 or not running else "\n⚠ 部分文件因占用未写入")
