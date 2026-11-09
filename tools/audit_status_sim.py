# -*- coding: utf-8 -*-
r"""逐项模拟 FixToggles.StatusOf()，与界面上的统计对账。

## 为什么要这个

用户问「是不是有几个还是没有（暗黄的『目标模组未启用』）」。
光看代码不够，得**把 46 项逐个算一遍**，与界面的统计对账：

· 算出来 == 界面  → 一致，没有遗漏
· 算出来  >  界面  → 有项的判定没走到（真 bug）
· 算出来  <  界面  → 有项误标（也是 bug）

## StatusOf 的真实判据（源码 + 反编译双证）

    if (!IsFixEnabled(id))                            return Disabled;      // ① 开关关着
    if (requiresModById.TryGetValue(id, out modId))
        if (!ModsConfig.IsActive(modId))              return TargetMissing; // ② 登记了且不在场
    return Active;                                                          // ③ 其余

关键细节：

· **默认关闭**由 `FixToggles.IsDefaultOff(id)` **唯一**决定（`Entry` 构造器没有这个参数），
  本版是 `deepProfilerQuiet` 与 `ew2TranspilerProbe` 两项。
  ① 排在 ② 前面 ⇒ 即使登记了目标模组，默认关闭的项也显示「已关闭」而不是「未启用」。

· `ModsConfig.IsActive(string id)` = `activeModsHashSet.Contains(id.ToLower())`；
  `RecacheActiveMods` 把 `data.activeMods` **原样**灌进 hashset（本机 XML 全小写）。
  ⇒ **大小写不敏感**：登记值写 `OskarPotocki.VFE.Empire` 也能命中
    `oskarpotocki.vfe.empire`。所以比较时统一 `.lower()`。
"""
import os
import re
import sys

SRC = r'E:\TAML\modfix\GNH-LocalFixes\src\FixToggles.cs'
ENVS = {
    'MOD-TEST（21 条）': r'C:\Users\asus\Desktop\其他杂项\游戏和模组启动器及管理工具\RimModManager\data\profiles\1cbb6e1ab3ea4b4593d3735b8d7fd695\Config\ModsConfig.xml',
    '主环境（1179 条）': r'C:\Users\asus\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml',
}


def actives(p):
    if not os.path.exists(p):
        return None
    raw = open(p, 'rb').read().decode('utf-8')
    body = re.search(r'<activeMods>([\s\S]*?)</activeMods>', raw).group(1)
    return {x.lower() for x in re.findall(r'<li>([^<]+)</li>', body)}


def parse(t):
    entries = [(m.group(1), m.group(2)) for m in
               re.finditer(r'new Entry\(\s*"([^"]+)"\s*,\s*\w+\s*,\s*"([^"]*)"', t)]
    i = t.find('requiresModById')
    j = t.find('};', i)
    reg = dict(re.findall(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', t[i:j]))
    # IsDefaultOff 的名单
    k = t.find('internal static bool IsDefaultOff')
    body = t[k:t.find('\n        }', k)]
    off = set(re.findall(r'id,\s*"([^"]+)"', body))
    return entries, reg, off


def main():
    t = open(SRC, encoding='utf-8').read()
    entries, reg, default_off = parse(t)
    print('Entry %d 项；requiresModById %d 条；默认关闭 %s\n'
          % (len(entries), len(reg), sorted(default_off)))

    for envname, path in ENVS.items():
        act = actives(path)
        if act is None:
            print('%s：配置不存在，跳过\n' % envname)
            continue
        A, D, M = [], [], []
        for eid, label in entries:
            if eid in default_off:                 # ① 先判开关
                D.append((eid, label))
            elif eid in reg and reg[eid].lower() not in act:   # ② 再判目标
                M.append((eid, label, reg[eid]))
            else:
                A.append((eid, label))
        print('=== %s' % envname)
        print('    已装载 %d · 已关闭 %d · 目标模组未启用 %d（合计 %d）'
              % (len(A), len(D), len(M), len(A) + len(D) + len(M)))
        print('    已关闭: %s' % ', '.join(x[0] for x in D))
        print('    目标未启用（%d）:' % len(M))
        for eid, label, pid in M:
            print('       %-32s %-34s ← %s' % (eid, label[:32], pid))
        print()
    return 0


if __name__ == '__main__':
    sys.exit(main())
