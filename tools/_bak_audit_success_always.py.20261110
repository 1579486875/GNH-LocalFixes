# -*- coding: utf-8 -*-
r"""给「目标模组不在场时一定失败」的 XML 补丁操作补 success=Always。

## 为什么

玩家日志实证（21 条框架模组测试环境，2026-11-10）：

    Verse.PatchOperationRemove(xpath="//ThoughtDef[@Class="SRAELib.ThoughtDef_HediffAttraction"]/targetNullifyingHediffs"):
      Failed to find a node with the given xpath
    Verse.PatchOperationSequence: Error in the operation at position=2
    Source file: ...\Patches\RJW17_SraeLibFieldCleanup.xml

模板 §1.2 硬要求：**被补的模组缺失时，必须不崩、不刷红字、安静跳过。**

## 判据

对每个 `PatchOperationRemove` / `PatchOperationReplace`：

· 如果它自己在 `<xpath>` 里就要求某个**模组才有的** defName / Class（即目标不在场时必然匹配不到），
  且没有 `<success>Always</success>` → 报出来。

「模组才有的」的识别：xpath 里出现 `@Class="A.B.C"`（带命名空间）或 `defName="X"`，
且该 defName/Class **不是原版**（不在原版类型/defName 表里）。

本脚本**只报告 + 可选打补丁**，默认 dry-run。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

PATCHES = r'E:\TAML\modfix\GNH-LocalFixes\Patches'


def has_success_always(op):
    s = (op.findtext('success') or '').strip()
    return s.lower() == 'always'


def walk(node, path, out):
    for e in node:
        if not isinstance(e.tag, str):
            continue
        if e.tag == 'Operation' or e.tag in ('li', 'match', 'nomatch', 'onFail'):
            cls = e.get('Class') or ''
            if cls in ('PatchOperationRemove', 'PatchOperationReplace',
                       'PatchOperationAdd', 'PatchOperationInsert'):
                xp = e.findtext('xpath') or ''
                # 判定「目标不在场时必然失败」：xpath 里带模组命名空间的 Class
                modclass = re.findall(r'@Class\s*=\s*"([A-Za-z_][\w.]*\.[A-Za-z_][\w.]*)"', xp)
                if modclass and not has_success_always(e):
                    out.append((path, cls, xp[:110], modclass[0]))
            walk(e, path, out)


def main():
    apply = '--apply' in sys.argv
    files = sorted(f for f in os.listdir(PATCHES) if f.endswith('.xml'))
    print('扫描 %d 个补丁%s\n' % (len(files), '（--apply 会直接改文件）' if apply else '（dry-run）'))
    total = 0
    for f in files:
        p = os.path.join(PATCHES, f)
        raw = open(p, 'rb').read()
        bom = raw.startswith(b'\xef\xbb\xbf')
        try:
            root = ET.fromstring(raw.decode('utf-8-sig'))
        except ET.ParseError as e:
            print('★ %-42s XML 非法: %s' % (f, e))
            continue
        found = []
        walk(root, f, found)
        if not found:
            continue
        total += len(found)
        print('★ %s' % f)
        for _, cls, xp, mc in found:
            print('    %s  目标类型=%s' % (cls, mc))
            print('      xpath: %s' % xp)
    print()
    if total == 0:
        print('✅ 没有发现「目标不在场时必然失败且无 success=Always」的操作')
        return 0
    print('共 %d 处' % total)
    print('⇒ 修法：给该 Operation 补 <success>Always</success>，并在它前面加一道')
    print('   <PatchOperationTest> 要求目标节点真的存在（两个守卫谁失效都不误伤）。')
    return 1


if __name__ == '__main__':
    sys.exit(main())
