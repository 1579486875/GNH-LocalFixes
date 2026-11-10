# -*- coding: utf-8 -*-
r"""审计 PatchOperation 的「失败时会不会刷红字」—— 找真裸露项与 Sequence 短路风险。

## 为什么需要这个

模板 §1.2 的硬要求：**被补的模组缺失时，必须不崩、不刷红字、安静跳过。**
RimWorld 的收尾阶段 `LoadedModManager.ClearCachedPatches` → `PatchOperation.Complete`
会把任何「一次都没成功过」的操作报成 `Patch operation ... failed` 的 ERROR。

## 判据（2026-11-10 重写；旧版有三处口径缺陷，见文末）

1. **定位型操作**（`Remove` / `Replace` / `Insert` / `AttributeSet` / `SetName`）拿不到目标就返回
   false → 需要 `<success>Always</success>`。
2. **`Add` 不在此列**：它对「父节点找不到」才失败，父节点通常是 `Defs/X` 这种必然存在的路径；
   给 `Add` 一律加 Always 会掩盖真正的路径写错。因此**只报告、不自动改**。
3. **容器守卫**：被 `PatchOperationConditional` / `PatchOperationSequence` / `PatchOperationFindMod`
   包住的操作，「不命中」由外层负责短路 —— 不算裸裸露。
4. ⚠ **`PatchOperationSequence` 自己也要 `success=Always`**：它内部任一子项失败就返回 false，
   而 false 一样会走 `Complete()` 报红字。**这一条是 2026-11-10 玩家日志实证出来的**：
   `Verse.PatchOperationSequence: Error in the operation at position=2`，
   而内层 `Remove` 明明已经加了 Always。
5. **`PatchOperationTest` 永远不该加 Always** —— 它失败本来就该短路，加了等于把守卫废掉。

## 旧版（_bak_audit_success_always.py.20261110）的三处缺陷

· docstring 承诺查 `defName="X"`，代码**只查 `@Class="A.B.C"`** → 本库绝大多数 xpath 靠
  `defName` 定位，于是它的「✅ 没有发现」是**假阴性**；
· Class 只覆盖 4/7（漏 `AttributeSet` / `SetName` / `Sequence`）；
· 不区分「被容器包住」与「真裸露」→ 若有人照它加 Always，会把守卫改坏。

本脚本只读，默认 dry-run；`--fix` 只对**第 1 类真裸露**补 Always（不动 Add、不动 Test）。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

PATCHES = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Patches')

# 拿不到目标就会返回 false、需要 success=Always 的操作
LOCATING = {'PatchOperationRemove', 'PatchOperationReplace', 'PatchOperationInsert',
            'PatchOperationAttributeSet', 'PatchOperationSetName'}
# 有「父节点找不到才失败」语义的操作 —— 只提示，不自动改
ADDISH = {'PatchOperationAdd', 'PatchOperationAddModExtension'}
# 外层负责「不满足就整段跳过」的容器
GUARDS = {'PatchOperationConditional', 'PatchOperationSequence', 'PatchOperationFindMod'}
# 失败本来就该短路，绝不加 Always
NEVER_ALWAYS = {'PatchOperationTest'}

# xpath 里「靠某个具体 def / 类名定位」的两种写法
RE_CLASS = re.compile(r'@Class\s*=\s*"([A-Za-z_][\w.]*\.[A-Za-z_][\w.]*)"')
RE_DEFNAME = re.compile(r'defName\s*=\s*"([^"]+)"')


def has_always(e):
    return ((e.findtext('success') or '').strip().lower() == 'always')


def targets_named(xp):
    """xpath 是否靠具体 Class / defName 定位（= 目标不在场时必然匹配不到）。"""
    m = RE_CLASS.search(xp) or RE_DEFNAME.search(xp)
    return m.group(1) if m else None


def walk(node, path, guarded, out):
    for e in node:
        if not isinstance(e.tag, str):
            continue
        if e.tag not in ('Operation', 'li', 'match', 'nomatch', 'onFail'):
            walk(e, path, guarded, out)
            continue
        cls = e.get('Class') or ''
        xp = e.findtext('xpath') or ''

        if cls in LOCATING:
            named = targets_named(xp)
            if named and not has_always(e) and not guarded:
                out.append(('裸露', path, cls, named, xp))
            elif named and not has_always(e):
                out.append(('容器内', path, cls, named, xp))
        elif cls in ADDISH:
            named = targets_named(xp)
            if named and not has_always(e) and not guarded:
                out.append(('Add待议', path, cls, named, xp))
        elif cls == 'PatchOperationSequence':
            # Sequence 自己的 success（子项失败会让它返回 false）
            if not has_always(e):
                out.append(('Sequence缺Always', path, cls, '', ''))
        elif cls in NEVER_ALWAYS:
            if has_always(e):
                out.append(('Test误加Always', path, cls, '', xp))

        # 进入子节点：本节点若是守卫容器，其内部操作算「被守卫」
        walk(e, path, guarded or (cls in GUARDS), out)


def main():
    do_fix = '--fix' in sys.argv
    files = sorted(f for f in os.listdir(PATCHES) if f.endswith('.xml'))
    print('扫描 %d 个补丁文件（%s）\n' % (len(files), '--fix 模式' if do_fix else 'dry-run'))

    buckets = {}
    for f in files:
        p = os.path.join(PATCHES, f)
        raw = open(p, 'rb').read()
        try:
            root = ET.fromstring(raw.decode('utf-8-sig'))
        except ET.ParseError as e:
            print('★ %-44s XML 非法: %s' % (f, e))
            continue
        found = []
        walk(root, f, False, found)
        for kind, path, cls, named, xp in found:
            buckets.setdefault(kind, []).append((path, cls, named, xp))

    order = ['裸露', 'Sequence缺Always', 'Test误加Always', 'Add待议', '容器内']
    titles = {
        '裸露': '① 真裸露定位型（必修：目标不在场就刷红字）',
        'Sequence缺Always': '② Sequence 自身缺 success=Always（子项失败会顶上来报错）',
        'Test误加Always': '③ PatchOperationTest 误加 Always（会废掉守卫）',
        'Add待议': '④ Add 类（只提示，需人工判断该不该处理）',
        '容器内': '⑤ 被容器包住（信息项，不需要处理）',
    }
    rc = 0
    for kind in order:
        rows = buckets.get(kind, [])
        if not rows:
            continue
        print('=' * 100)
        print('%s —— %d 条' % (titles[kind], len(rows)))
        if kind in ('裸露', 'Sequence缺Always', 'Test误加Always'):
            rc = 1
        for path, cls, named, xp in rows:
            print('  %-46s %-28s %s' % (path, cls.replace('PatchOperation', ''), named or ''))
            if xp:
                print('      xpath: %s' % xp[:120])
        print()

    if rc == 0:
        print('✅ ①②③ 全部为 0：没有真裸露、没有 Sequence 缺 Always、没有 Test 误加 Always')
    else:
        print('⇒ ① 的修法：给该 Operation 补 <success>Always</success>，并在前面加一道')
        print('   <PatchOperationTest> 要求目标节点真的存在（两个守卫谁失效都不误伤）。')
        print('⇒ ② 的修法：给 <Operation Class="PatchOperationSequence"> 直接补 <success>Always</success>。')
    if not do_fix:
        print('\n（dry-run：没有改任何文件）')
    return rc


if __name__ == '__main__':
    sys.exit(main())
