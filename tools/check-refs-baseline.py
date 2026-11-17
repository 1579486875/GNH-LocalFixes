# -*- coding: utf-8 -*-
r"""闸门：`refs\Assembly-CSharp.dll` 必须与 **1.6 引擎副本**的内容**逐哈希一致**。

## 为什么这条闸门是「反过来」的

大多数工程的常识是「游戏更新后把 refs 同步到最新」。**本工程不是。**

本模组同时支持 1.6 与 1.7，编译时用的是 **1.6 的 `Assembly-CSharp.dll`** ——
这样能保证「只用 1.6 就存在的 API」，产物在两版上都能跑。

反过来，若拿 1.7 的程序集编译，一旦用到 1.7 新增的 API，
1.6 玩家那边就是 `MissingMethodException`，而编译期完全看不出来。

**实测（2026-11-17）**：

| 文件 | FileVersion | SHA256 前 8 | 大小 |
|---|---|---|---|
| `refs\Assembly-CSharp.dll` | 1.6.9676.17735 | `5cf1b5be` | 15,777,280 |
| `D:\RimWorld-1.6\…\Managed\Assembly-CSharp.dll` | 1.6.9676.17735 | `5cf1b5be` | 15,777,280 |
| `D:\steam\…\RimWorld\Assembly-CSharp.dll` | 1.7.9816.739 | `7fbce7a5` | 15,637,504 |

→ 前两者逐哈希一致；第三个**故意不同**。

## 它防什么

防「有人顺手跑了 `tools/sync-refs.ps1`」。
那个脚本按 `$RimWorldDir`（默认 1.7 主安装）同步，
一跑就会把 refs 升到 1.7 —— **静默破坏 1.6 兼容**，而且编译照样通过，
问题只在 1.6 玩家那边出现。

## 判据

    refs\Assembly-CSharp.dll 的 SHA256 == <1.6 引擎副本>\RimWorldWin64_Data\Managed\Assembly-CSharp.dll 的 SHA256

1.6 副本不存在时 → **skip**（exit 0 + 明确打印「本项未核」）。

## 用法

    python tools/check-refs-baseline.py
    python tools/check-refs-baseline.py -v

退出码：0 = 一致（或环境缺失 skip）；1 = 不一致
"""
import hashlib
import os
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REFS = os.path.join(REPO, 'refs', 'Assembly-CSharp.dll')
BASE16 = r'D:\RimWorld-1.6\RimWorldWin64_Data\Managed\Assembly-CSharp.dll'
GAME17 = r'D:\steam\steamapps\common\RimWorld\Assembly-CSharp.dll'


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def ver(p):
    """尽量读 FileVersion；读不到就返回 '?'"""
    try:
        import subprocess
        out = subprocess.run(
            ['powershell', '-NoProfile', '-Command',
             "(Get-Item '%s').VersionInfo.FileVersion" % p],
            capture_output=True, text=True, timeout=20)
        return (out.stdout or '').strip() or '?'
    except Exception:
        return '?'


def main():
    verbose = '-v' in sys.argv
    if not os.path.exists(REFS):
        print('❌ 找不到 refs\\Assembly-CSharp.dll')
        return 1
    if not os.path.exists(BASE16):
        print('⏭ 跳过：没有 1.6 引擎副本')
        print('   %s' % BASE16)
        print('   本项**未核**（不是通过）—— 没有基准就无法判断 refs 该钉在哪个版本。')
        return 0

    a, b = sha(REFS), sha(BASE16)
    print('refs\\Assembly-CSharp.dll        %s  %s  %d B'
          % (ver(REFS), a[:20], os.path.getsize(REFS)))
    print('1.6 引擎副本                    %s  %s  %d B'
          % (ver(BASE16), b[:20], os.path.getsize(BASE16)))
    if os.path.exists(GAME17):
        c = sha(GAME17)
        same17 = (a == c)
        print('1.7 主安装                      %s  %s  %d B%s'
              % (ver(GAME17), c[:20], os.path.getsize(GAME17),
                 '   ⚠ 与 refs 相同（说明 refs 已被同步到 1.7）' if same17 else '   （故意不同，见文件头说明）'))
        if same17:
            print('\n❌ refs 现在是 1.7 的程序集 —— 这会破坏 1.6 兼容')
            print('   要恢复：把 1.6 的那份拷进 refs\\（**不要**跑 tools\\sync-refs.ps1）')
            return 1

    if a != b:
        print('\n❌ refs 与 1.6 引擎副本的哈希不一致')
        print('   若是有意升到 1.7，请先在 1.6 的副本上验一遍编译；否则应把 1.6 那份拷回 refs\\。')
        return 1

    print('\n' + '=' * 74)
    print('✅ refs 与 1.6 引擎副本逐哈希一致 —— 双版本兼容的编译基线正确')
    if verbose:
        print('   （本工程有意钉在 1.6，理由见 GNH.LocalFixes.csproj 里的长注释）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
