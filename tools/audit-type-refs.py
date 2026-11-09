# -*- coding: utf-8 -*-
r"""审计：XML 补丁里「往不确定存在的类型上挂引用」的高危写法。

## 起因（2026-11-09，工坊 20+ 玩家取消订阅）

`GNH-LocalFixes/Patches/NMM_StaleCompCleanup.xml` 的两条 `PatchOperationAdd`
无条件往 `BasePawn/comps` 补 `<li Class="NudityMattersMore.CompProperties_NMM"/>`。
玩家那一版 NMM 的 dll 里没有这个类型 →
`BasePawn` 整表解析失败 → **所有 pawn 的 def 被丢弃** → 模组被游戏禁用。

同一个错误在 v1.3.28（补 `RJW_BGS.RaceGeneDef`）已经犯过一次，
v1.3.29 撤回时只写进了 README 的历史变更段，**没有变成可执行的检查**，
于是 v1.4.25 又踩了一遍。这个脚本就是把那条结论变成机械检查。

## 三级判据（只有第 4 级才报 HIGH）

1. `Class="X"` 出现在 **原版类型表**里（`Assembly-CSharp.dll` 的短名或全名）→ 安全。
   （`ThinkNode_ConditionalPawnKind` / `StockGenerator_Tag` 这类无命名空间的短名
   都是原版 —— 第一版脚本没做这一步，报了 336 处假阳性。）
2. `X` 属于**补丁所在模组自己的 dll** → 安全。
3. `X` 属于本模组 `modDependencies` 里的某个包 → 安全（缺依赖时模组根本不加载）。
4. 否则 → **HIGH**：玩家一旦没装那个模组（或版本不对），该 def 被整只丢弃。

## 用法

    python audit-type-refs.py                # 扫我们维护的 31 个模组
    python audit-type-refs.py <目录>         # 只扫指定目录
    python audit-type-refs.py --fix-hint     # 额外打印修法示例

出口码：有 HIGH 则 1（可直接接进收工流程）。
"""
import os
import re
import sys
import json
import xml.etree.ElementTree as ET

ROOT = r'E:\TAML'
VANILLA_JSON = os.path.join(r'E:\TAML\_scratch\audit-20261109', 'vanilla-types.json')
ADD_LIKE = ('PatchOperationAdd', 'PatchOperationInsert',
            'PatchOperationReplace', 'PatchOperationAddModExtension')
GUARD = ('PatchOperationFindMod', 'PatchOperationTest', 'PatchOperationConditional')

# 我们维护的 7 个自制 + 24 个分支的根目录
OURS = [
    os.path.join(ROOT, 'modfix', 'GNH-LocalFixes'),
    os.path.join(ROOT, 'modfix', 'LuxandraNeutralRestore'),
    os.path.join(ROOT, '床铺增益建筑通用兼容-Universal Bed Facility Compatibility'),
    os.path.join(ROOT, '米莉拉关键物品制作补丁-MiliraKeyComponentPatch'),
    os.path.join(ROOT, 'NudityMattersMoreOpinions-CN'),
    os.path.join(ROOT, 'RimTalk-ET-Prompt-CN'),
    os.path.join(ROOT, 'RimWorld-CoreZh-GNH'),
]
# 分支目录是【24 个模组的容器】，必须展开成子目录逐个判 ——
# 当成一个包会把所有子模组的 dll 混成一个「自有类型」集合，
# 于是 MihoLib.* / RigorMortis.* 这类「其实属于子模组自己」的类型全被判成 HIGH（实测 9 处假阳性）。
_BR = os.path.join(ROOT, 'GNH-RimWorld-LocalBranches', 'mods')
if os.path.isdir(_BR):
    OURS += [os.path.join(_BR, d) for d in sorted(os.listdir(_BR))
             if os.path.isdir(os.path.join(_BR, d))]


# ---------------------------------------------------------------------------
# 已人工核实表
# ---------------------------------------------------------------------------
# 下面这些会被自动判据报成 HIGH，但我逐个看过实际归属，确认安全。
# 自动判据判不出来的原因只有一种：【命名空间 ≠ packageId】。
# 工具只能拿「类型名」去比「硬依赖包的 dll 类型名」，而某些前置把类型放在
# 与 packageId 毫无字面关系的命名空间里（MihoLib vs miho.fortifiedoutremer），
# 或者干脆就是本模组自己 dll 里的类型（RigorMortis vs fxz.moelotlzombie.update）。
#
# ⚠ 这张表**只能人工加**。每次改动补丁后重跑本脚本，任何【新出现的】HIGH
#    都必须在补丁里加 MayRequire 或 FindMod 守卫生掉，不许往这里塞。
CONFIRMED = {
    'MihoLib.CompProperties_AbilitySummonPawn':
        'mcw.mihopatch 硬依赖 miho.fortifiedoutremer，MihoLib 是它的命名空间',
    'MihoLib.VerbProperties_ShotGun':
        'mcw.mihopatch 硬依赖 miho.fortifiedoutremer，MihoLib 是它的命名空间',
    'MihoLib.HomingProjectileProperties':
        'mcw.mihopatch 硬依赖 miho.fortifiedoutremer，MihoLib 是它的命名空间',
    'MihoLib.ProjectileProperties_fragment':
        'mcw.mihopatch 硬依赖 miho.fortifiedoutremer，MihoLib 是它的命名空间',
    'TorgueMiho.Verb_Properties_ShotGun':
        'mcw.mihopatch 硬依赖 miho.fortifiedoutremer，TorgueMiho 是它的命名空间',
    'RigorMortis.CompProperties_YinAndMalevolent':
        'fxz.moelotlzombie.update 自己的 dll（RigorMortis.dll）里的类型',
    'SpecificWorkJobGiver.JobGiver_SpecificWork':
        '已改为 MayRequire 守卫（2026-11-09 提交 04acefb），此处仅作历史登记',
}


def load_vanilla():
    if not os.path.exists(VANILLA_JSON):
        print('★ 缺原版类型表 %s' % VANILLA_JSON)
        print('  先用 PowerShell 导出：')
        print('  $asm=[Reflection.Assembly]::LoadFrom("<游戏>\\Assembly-CSharp.dll"); ...')
        return set()
    with open(VANILLA_JSON, encoding='utf-8') as f:
        return set(x.strip() for x in f if x.strip())



def build_pkg_index():
    """扫 Mods / workshop / 1.6 Mods 的 About.xml，建 packageId → 模组根目录。"""
    idx = {}
    for base in (r'D:\steam\steamapps\common\RimWorld\Mods',
                 r'D:\steam\steamapps\workshop\content94100',
                 r'D:\RimWorld-1.6\Mods'):
        if not os.path.isdir(base):
            continue
        for dp, dn, fn in os.walk(base):
            if 'About.xml' in fn and os.path.basename(dp).lower() == 'about':
                root = os.path.dirname(dp)
                try:
                    r = ET.parse(os.path.join(dp, 'About.xml')).getroot()
                    pid = (r.findtext('packageId') or '').strip().lower()
                except Exception:
                    continue
                if pid and pid not in idx:
                    idx[pid] = root
    return idx


def dep_types(pkgidx, deps, cache):
    """硬依赖包自己的 dll 类型名（缓存，避免重复扫）。"""
    out = set()
    for d in deps:
        d = d.strip().lower()
        if d in cache:
            out |= cache[d]
            continue
        root = pkgidx.get(d)
        got = dll_types(root) if root else set()
        cache[d] = got
        out |= got
    return out


def about_of(modroot):
    """读模组的 packageId / modDependencies（沿 1.6 / 1.7 / 根 逐层找）。"""
    for cand in (os.path.join(modroot, 'About', 'About.xml'),
                 *[os.path.join(modroot, v, 'About', 'About.xml') for v in ('1.7', '1.6')]):
        if os.path.exists(cand):
            try:
                r = ET.parse(cand).getroot()
                pid = (r.findtext('packageId') or '').strip().lower()
                deps = [(x.text or '').strip().lower()
                        for x in r.findall('modDependencies/li/packageId')]
                return pid, deps, cand
            except ET.ParseError:
                continue
    return None, [], None


def dll_types(modroot):
    """枚举该模组全部 dll 的类型名（短名 + 全名）。失败则返回空集。"""
    names = set()
    for dp, dn, fn in os.walk(modroot):
        dn[:] = [d for d in dn if d not in ('.git', 'bin', 'obj', 'Source', '_backup',
                                            '1.3', '1.4', '1.5')]
        for f in fn:
            if not f.lower().endswith('.dll'):
                continue
            p = os.path.join(dp, f)
            try:
                # 用 pythonnet 不可靠；退回「读字符串」方式，够用来判归属
                raw = open(p, 'rb').read()
            except OSError:
                continue
            for m in re.finditer(rb'[\x20-\x7e]{4,80}', raw):
                s = m.group().decode('ascii', 'ignore')
                if '.' in s and s[0].isalpha() and not s.startswith('System.'):
                    names.add(s)
                elif s[:1].isalpha() and '_' in s:
                    names.add(s)
    return names


def collect_types(node, out):
    for e in node.iter():
        c = e.get('Class')
        if c:
            out.add(c)


def scan_file(p, known_ok, vanilla):
    """返回 [(type, guarded, chain)]，只保留「不安全」的。"""
    try:
        root = ET.parse(p).getroot()
    except ET.ParseError:
        return []
    bad = []

    def walk(op, guarded, chain):
        cls = op.get('Class') or ''
        if cls in GUARD:
            for child in op:
                if isinstance(child.tag, str) and child.tag in ('match', 'nomatch', 'onFail'):
                    for sub in child.iter():
                        if isinstance(sub.tag, str) and sub.tag == 'Operation':
                            walk(sub, True, chain + [cls])
            return
        if cls in ADD_LIKE:
            guarded_here = guarded or bool(op.get('MayRequire'))
            val = op.find('value')
            if val is not None:
                ts = set()
                collect_types(val, ts)
                for t in ts:
                    if t in vanilla or t in known_ok or t.split('.')[0] in known_ok:
                        continue
                    if t in CONFIRMED:
                        continue
                    # xpath 自带判定也算守卫
                    xp = op.findtext('xpath') or ''
                    if t in xp or t.split('.')[-1] in xp:
                        continue
                    if not guarded_here:
                        bad.append((t, False, ' > '.join(chain) or '(无)'))
        for child in op:
            if isinstance(child.tag, str) and child.tag == 'operations':
                for sub in child.iter():
                    if isinstance(sub.tag, str) and sub.tag == 'Operation':
                        walk(sub, False, chain)

    for op in root.iter():
        if isinstance(op.tag, str) and op.tag == 'Operation':
            # 只在顶层父为 Patch 时当作无条件入口（Sequence 内部下面会再走）
            if op.get('Class') in GUARD:
                walk(op, False, [])
    # 再扫一遍顶层真正执行的
    bad.clear()
    for child in root:
        if isinstance(child.tag, str) and child.tag == 'Operation':
            walk(child, False, [])
    return bad


def main():
    vanilla = load_vanilla()
    pkgidx = build_pkg_index()
    depcache = {}
    print('packageId 索引: %d 个模组' % len(pkgidx))
    print('原版类型表: %d 个名字' % len(vanilla))
    print()

    roots = [a for a in sys.argv[1:] if not a.startswith('--')] or OURS
    total_files = 0
    all_bad = []
    skipped = []
    for r in roots:
        if not os.path.isdir(r):
            continue
        pid, deps, acf = about_of(r)
        own = dll_types(r)
        # 硬依赖包也算安全（缺了它模组不加载）。
        # ★ 注意不能拿「命名空间」去比「packageId」—— MihoLib vs miho.fortifiedoutremer
        #   永远不相等，第一版就是在这里报了 9 处假阳性。
        #   正确做法：把硬依赖包的 dll 也枚举出来，拿【类型名】比【类型名】。
        known_ok = own | dep_types(pkgidx, deps, depcache)
        files = []
        for dp, dn, fn in os.walk(r):
            dn[:] = [d for d in dn if d not in ('.git', 'bin', 'obj', '_backup',
                                                'Source', '1.3', '1.4', '1.5')]
            for f in fn:
                if f.lower().endswith('.xml') and 'patch' in os.path.join(dp, f).lower():
                    files.append(os.path.join(dp, f))
        total_files += len(files)
        b = []
        for p in files:
            for t, g, ch in scan_file(p, known_ok, vanilla):
                if t in CONFIRMED:
                    skipped.append(t)
                    continue
                b.append((p, t, g, ch))
        if b:
            print('── %s  (%s)' % (os.path.basename(r), pid or '?未读到 packageId'))
            print('   modDependencies: %s' % (deps or '(无)'))
            print('   自有 dll 类型名 %d 个' % len(own))
            for p, t, g, ch in b:
                print('   ★ HIGH  %-50s  %s' % (t, os.path.basename(p)))
                print('            守卫链: %s' % ch)
            all_bad += b
            print()

    print('=' * 70)
    print('扫描 %d 个补丁 XML（%d 个模组）' % (total_files, len(roots)))
    if skipped:
        print('（另有 %d 处命中已人工核实表，按安全跳过：%s）'
              % (len(skipped), ', '.join(sorted(set(skipped)))))
    if not all_bad:
        print('✅ 没有发现「往不确定类型上挂引用」的高危写法')
        return 0
    print('★ 发现 %d 处 HIGH：这些地方引用了既非原版、又非硬依赖的类型' % len(all_bad))
    print('  修法：给该 Operation 加 MayRequire="<目标 packageId>"，')
    print('        或套一层 PatchOperationFindMod；绝不能无条件挂模组类型。')
    return 1


if __name__ == '__main__':
    sys.exit(main())
