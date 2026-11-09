# -*- coding: utf-8 -*-
r"""核对 requiresModById 里每个 packageId 与 ModsConfig.xml 里的**实际大小写**。

## 为什么这是「暗伤」

`FixToggles.StatusOf()` 的判据是：

    if (requiresModById.TryGetValue(entry.Id, out modId) ...)
        if (!ModsConfig.IsActive(modId)) return FixStatus.TargetMissing;

而 `Verse.ModsConfig.IsActive(string modId)` 内部是
`data.activeMods.Contains(modId)` —— `List<string>.Contains` 用默认比较器，
**大小写敏感**。

⇒ 登记时大小写写错，后果是**反向误报**：目标模组装着，
界面却一直标暗黄「目标模组未启用」。**比不登记更糟** ——
用户会以为自己少装了东西，去重装、去排查。

（`requiresModById` 字典本身是 `OrdinalIgnoreCase` 的，所以**查字典**没问题；
问题只出在最后拿它去问 `ModsConfig.IsActive` 那一步。）

## 本脚本做什么

对每条登记，在若干份 `ModsConfig.xml` 里找「忽略大小写相等」的那一条，
把**文件里的真实写法**打出来，与登记值逐字符比对。
"""
import os
import re
import sys

SRC = r'E:\TAML\modfix\GNH-LocalFixes\src\FixToggles.cs'
DEFAULT = r'C:\Users\asus\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml'
PROFILES = r'C:\Users\asus\Desktop\其他杂项\游戏和模组启动器及管理工具\RimModManager\data\profiles'


def actives(p):
    if not os.path.exists(p):
        return None
    raw = open(p, 'rb').read().decode('utf-8')
    body = re.search(r'<activeMods>([\s\S]*?)</activeMods>', raw).group(1)
    return re.findall(r'<li>([^<]+)</li>', body)


def main():
    t = open(SRC, encoding='utf-8').read()
    i = t.find('requiresModById')
    j = t.find('};', i)
    reg = dict(re.findall(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}', t[i:j]))

    envs = [('default(1179)', DEFAULT)]
    if os.path.isdir(PROFILES):
        for d in sorted(os.listdir(PROFILES)):
            envs.append((d[:12], os.path.join(PROFILES, d, 'Config', 'ModsConfig.xml')))

    # 先把所有版本的 id 大小写收集起来（同一个 packageId 在不同档案里可能写法不同）
    seen = {}          # lower -> set(原始写法)
    for name, p in envs:
        a = actives(p)
        if not a:
            continue
        for x in a:
            seen.setdefault(x.lower(), set()).add(x)

    print('登记项 %d 条；从 %d 份 ModsConfig 收集到 %d 个不同 packageId\n'
          % (len(reg), len(envs), len(seen)))

    bad, okc, absent = [], 0, 0
    for eid, pid in reg.items():
        variants = seen.get(pid.lower())
        if not variants:
            absent += 1
            continue
        if pid in variants:
            okc += 1
        else:
            bad.append((eid, pid, sorted(variants)))

    print('=== 大小写完全一致 %d 条' % okc)
    print('=== 在所有环境里都没找到（本机没装，属正常）%d 条' % absent)
    print()
    if not bad:
        print('✅ 没有任何一条存在大小写不一致')
        return 0

    print('★ 大小写不一致 %d 条 —— 会导致「目标模组未启用」反向误报：' % len(bad))
    for eid, pid, variants in bad:
        print('  %-30s 登记=%s' % (eid, pid))
        print('      文件里实际= %s' % ' / '.join(variants))
    print()
    print('⇒ 把登记值改成文件里的写法（大小写逐字符一致）即可。')
    return 1


if __name__ == '__main__':
    sys.exit(main())
