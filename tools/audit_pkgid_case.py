# -*- coding: utf-8 -*-
r"""核对 `requiresModById` 的登记值，并检查各环境 ModsConfig 的大小写前提。

## 真实因果（2026-11-10 反编译订正）

`FixToggles.StatusOf()` 用 `ModsConfig.IsActive(modId)` 判断前置模组在不在场。
反编译 1.7.5000 的 `Verse.ModsConfig`（`rw17_20261110`，MVID 6c268a95…）：

    public static bool IsActive(string id)
    {
        return activeModsHashSet.Contains(id.ToLower());   // ← 只把【查询参数】转小写
    }

而 `activeModsHashSet` 是在 `RecacheActiveMods()` 里用 `data.activeMods` **原样**建的
（`data.activeMods` 直接来自 ModsConfig.xml 的 `<li>` 文本）。

### ⇒ 结论与旧版脚本相反

旧版说「`List.Contains` 大小写敏感，所以登记值要改成小写」——**半对半错**：

· 说它对：**当 XML 里的 id 全小写时**，`id.ToLower()` 之后确实能匹配，行为等价于「不敏感」；
  本机两个环境（1179 / 1174 条）实测**全小写**，所以那 10 处大小写差异**不是缺陷**，
  改它们只会产出零收益的 diff。
· 说它错：真正的大小写敏感发生在**存储侧**。如果某个玩家的 `ModsConfig.xml` 里写了
  `Brrainz.Harmony`（手改、或第三方启动器写的），那么 `IsActive("brrainz.harmony")`
  会返回 **false** —— 该模组的一切 `MayRequire` 判定都会失效。
  **这是游戏侧的行为，不是我们的登记表能修的。**

### 因此本脚本现在做两件事

1. **检查各环境 `ModsConfig.xml` 的 `<li>` 是否全小写**（真风险：非全小写会导致全游戏
   `IsActive` 误判）。发现非小写就点名报出来 —— 这是可以修的数据问题。
2. **检查每条登记值是否能在任一环境里找到**（忽略大小写）。找不到 = 拼错的 packageId，
   后果是界面永远标「目标模组未启用」（反向误报）。本机实跑 29 条全部命中。

⚠ **不再建议「把登记值改成小写」** —— 那是零收益的 diff，还会掩盖上面第 1 条的真风险。
"""
import os
import re

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'src', 'FixToggles.cs')
DEFAULT = r'C:\Users\asus\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml'
PROFILES = r'C:\Users\asus\Desktop\其他杂项\游戏和模组启动器及管理工具\RimModManager\data\profiles'


def actives(p):
    if not os.path.exists(p):
        return None
    raw = open(p, 'rb').read().decode('utf-8-sig', errors='replace')
    m = re.search(r'<activeMods>([\s\S]*?)</activeMods>', raw)
    if not m:
        return None
    return re.findall(r'<li>([^<]+)</li>', m.group(1))


def main():
    src = open(SRC, encoding='utf-8').read()
    i = src.find('requiresModById')
    j = src.find('};', i)
    reg = dict(re.findall(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', src[i:j]))
    print('登记条数: %d\n' % len(reg))

    envs = [('default', DEFAULT)]
    if os.path.isdir(PROFILES):
        for d in sorted(os.listdir(PROFILES)):
            envs.append((d[:12], os.path.join(PROFILES, d, 'Config', 'ModsConfig.xml')))

    # ── ① 各环境的大小写体检 ──
    print('=' * 96)
    print('① ModsConfig 的 id 大小写体检（非全小写 → 全游戏的 IsActive 会误判）')
    all_ids = {}          # lower -> 原始写法集合
    non_lower_envs = []
    for name, p in envs:
        ids = actives(p)
        if not ids:
            continue
        bad = [x for x in ids if x != x.lower()]
        for x in ids:
            all_ids.setdefault(x.lower(), set()).add(x)
        flag = 'OK' if not bad else '★ %d 条非小写' % len(bad)
        print('  %-14s %5d 条   %s' % (name, len(ids), flag))
        if bad:
            non_lower_envs.append((name, bad[:8]))

    # ── ② 登记值能否在任一环境找到 ──
    print()
    print('=' * 96)
    print('② requiresModById 的登记值是否能对上真实 packageId')
    miss = []
    for tid, pid in sorted(reg.items()):
        hit = all_ids.get(pid.lower())
        if hit:
            variants = sorted(hit)
            note = '' if variants == [pid] else '（实际写法 %s）' % '/'.join(variants)
            print('  OK   %-38s %s%s' % (tid, pid, note))
        else:
            print('  ★MISS %-37s %s' % (tid, pid))
            miss.append((tid, pid))

    print()
    print('=' * 96)
    rc = 0
    if non_lower_envs:
        rc = 1
        print('★ ① 发现非小写 id（会导致 IsActive 误判，进而让 MayRequire 失效）：')
        for name, bad in non_lower_envs:
            print('   %s: %s' % (name, bad))
        print('   修法：把该环境 ModsConfig.xml 里这些 <li> 改成全小写（只改数据，不改代码）。')
    else:
        print('✅ ① 所有环境全小写 —— 在当前数据下 IsActive 等价于大小写不敏感，')
        print('   因此 requiresModById 里的登记值**不需要**为大小写做任何改动。')
    if miss:
        rc = 1
        print('★ ② 有 %d 条登记值在所有环境都找不到，疑似拼写错误：' % len(miss))
        for tid, pid in miss:
            print('   %s -> %s' % (tid, pid))
    else:
        print('✅ ② 全部 %d 条登记值都能对上真实 packageId。' % len(reg))
    return rc


if __name__ == '__main__':
    raise SystemExit(main())
