#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
按「RimWorld模组-全面审查与修复-通用模板」§6.2 打包并验证发布产物。

模板 §6.2 的六项判据，逐条实现：

  ① 解压回验        —— 把包解开，**逐文件与源比对**（不是「zip 生成成功」就算完）
  ② 双向清单比对    —— 仓库「应进包」清单 vs 包内清单：0 缺 / 0 多 / 0 大小不同
  ③ 包里的版本号    —— 从**包里解出来的** About.xml 读 <modVersion>，不看文件名
  ④ 完整性自检      —— zip 用 testzip()
  ⑤ 三处版本一致    —— 包内版本 / About.xml / csproj / README，必须一致
  ⑥ 同名多份同一文件 —— 比 SHA256（本脚本只出一份，留钩子给多份场景）

用法：
    python 工具/发布打包.py            # 打包 + 全部验证
    python 工具/发布打包.py --check    # 只验证已有的包，不重新打
"""

import hashlib
import os
import re
import shutil
import sys
import zipfile

sys.stdout.reconfigure(encoding='utf-8')

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, '发布')

# ── 「应进包」清单：模组运行时真正需要的东西 ──────────────────────────────
# 刻意**不含**：源码/ 工具/ 文档/ 备份/ bin/ obj/ _scratch/ 临时-*/.git
INCLUDE_DIRS = [
    'About',
    'Languages',
    'Patches',
]
INCLUDE_FILES = [
    # 单版本模组，没有 LoadFolders.xml；DLL 单独从 bin/Release 取（见下）
    'LICENSE',
]
# DLL 的源（仓库里的 Assemblies/ 是空的，构建产物在 bin/Release）
DLL_SRC = os.path.join(ROOT, 'bin', 'Release', 'Assemblies', 'GNH.LocalFixes.dll')
DLL_DST = 'Assemblies/GNH.LocalFixes.dll'

# 包里不该出现的东西（模板 §6.1「确认工作区干净」的镜像检查）
FORBIDDEN = [
    'bin/', 'obj/', '源码/', '工具/', '文档/', '备份/', '.git/',
    '_scratch/', '引用/', '临时-', '发布/', '.cs', '.csproj', '.py',
]


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def human(n):
    for unit in ('B', 'KB', 'MB'):
        if n < 1024:
            return '%.0f %s' % (n, unit) if unit == 'B' else '%.1f %s' % (n, unit)
        n /= 1024.0
    return '%.1f GB' % n


def build_expected():
    """算出「仓库里应进包」的清单：相对路径 → 绝对路径。"""
    exp = {}
    for d in INCLUDE_DIRS:
        base = os.path.join(ROOT, d)
        if not os.path.isdir(base):
            continue
        for dirpath, _dirnames, filenames in os.walk(base):
            for fn in filenames:
                full = os.path.join(dirpath, fn)
                rel = os.path.relpath(full, ROOT).replace('\\', '/')
                exp[rel] = full
    for fn in INCLUDE_FILES:
        full = os.path.join(ROOT, fn)
        if os.path.isfile(full):
            exp[fn] = full
    return exp


def do_pack():
    exp = build_expected()
    if not os.path.isfile(DLL_SRC):
        print('❌ 找不到构建产物：%s' % DLL_SRC)
        print('   先跑 dotnet build -c Release')
        return None
    os.makedirs(OUT_DIR, exist_ok=True)

    # 版本号从 About.xml 读（不是文件名）
    about = open(os.path.join(ROOT, 'About', 'About.xml'), encoding='utf-8-sig').read()
    m = re.search(r'<modVersion>([^<]+)</modVersion>', about)
    ver = m.group(1).strip() if m else 'unknown'

    zip_path = os.path.join(OUT_DIR, 'GNH-LocalFixes-v%s.zip' % ver)
    if os.path.exists(zip_path):
        os.remove(zip_path)

    print('════ ① 打包 ════')
    print('  目标：%s' % os.path.relpath(zip_path, ROOT))
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        # 模组文件
        for rel in sorted(exp):
            z.write(exp[rel], rel)
        # DLL
        z.write(DLL_SRC, DLL_DST)
    print('  ✅ 写入 %d 个文件 + 1 个 DLL' % len(exp))
    print('  包大小：%s' % human(os.path.getsize(zip_path)))
    return zip_path, exp, ver


def verify(zip_path, exp, ver):
    ok = True
    print()
    print('════ ④ 完整性自检（testzip）════')
    with zipfile.ZipFile(zip_path) as z:
        bad = z.testzip()
        if bad:
            print('  ❌ 损坏成员：%s' % bad)
            ok = False
        else:
            print('  ✅ 全部成员 CRC 校验通过')

    print()
    print('════ ② 双向清单比对 ════')
    with zipfile.ZipFile(zip_path) as z:
        in_zip = {n for n in z.namelist() if not n.endswith('/')}
        zip_sizes = {n: z.getinfo(n).file_size for n in in_zip}

    expected_names = set(exp.keys()) | {DLL_DST}
    missing = sorted(expected_names - in_zip)
    extra = sorted(in_zip - expected_names)
    size_diff = []
    for n in sorted(expected_names & in_zip):
        src = DLL_SRC if n == DLL_DST else exp[n]
        if os.path.getsize(src) != zip_sizes[n]:
            size_diff.append((n, os.path.getsize(src), zip_sizes[n]))

    print('  应进包：%d 个 ｜ 包内：%d 个' % (len(expected_names), len(in_zip)))
    print('  %s 缺 %d 个 %s' % ('✅' if not missing else '❌', len(missing), missing[:5]))
    print('  %s 多 %d 个 %s' % ('✅' if not extra else '❌', len(extra), extra[:5]))
    print('  %s 大小不同 %d 个 %s' % ('✅' if not size_diff else '❌', len(size_diff), size_diff[:3]))
    if missing or extra or size_diff:
        ok = False

    print()
    print('════ ②b 包里有没有混进不该进的东西 ════')
    hits = [n for n in in_zip if any(f in n for f in FORBIDDEN)]
    if hits:
        print('  ❌ 混入了 %d 个：%s' % (len(hits), hits[:6]))
        ok = False
    else:
        print('  ✅ 没有源码 / 构建产物 / 备份 / 脚本')

    print()
    print('════ ①b 解压回验（逐文件与源比对 SHA256）════')
    tmp = os.path.join(OUT_DIR, '_verify')
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    with zipfile.ZipFile(zip_path) as z:
        z.extractall(tmp)
    mism = []
    for n in sorted(expected_names & in_zip):
        src = DLL_SRC if n == DLL_DST else exp[n]
        a = sha256_file(src)
        b = sha256_file(os.path.join(tmp, n.replace('/', os.sep)))
        if a != b:
            mism.append(n)
    print('  %s 逐文件 SHA256 一致 %d 个，不一致 %d 个 %s'
          % ('✅' if not mism else '❌', len(expected_names & in_zip) - len(mism), len(mism), mism[:3]))
    if mism:
        ok = False

    print()
    print('════ ③ 包里的版本号（从包内 About.xml 读，不看文件名）════')
    about_in_zip = os.path.join(tmp, 'About', 'About.xml')
    t = open(about_in_zip, encoding='utf-8-sig').read()
    m = re.search(r'<modVersion>([^<]+)</modVersion>', t)
    pkg_ver = m.group(1).strip() if m else None
    print('  包内 About.xml modVersion = %s' % pkg_ver)
    print('  文件名里的版本           = %s' % ver)
    if pkg_ver != ver:
        print('  ❌ 两者不一致')
        ok = False
    else:
        print('  ✅ 一致')

    print()
    print('════ ⑤ 多处版本一致 ════')
    src_about = re.search(r'<modVersion>([^<]+)</modVersion>',
                          open(os.path.join(ROOT, 'About', 'About.xml'), encoding='utf-8-sig').read()).group(1).strip()
    csproj = re.search(r'<Version>([^<]+)</Version>',
                       open(os.path.join(ROOT, 'GNH.LocalFixes.csproj'), encoding='utf-8-sig').read()).group(1).strip()
    readme = re.search(r'当前版本：([0-9.]+)',
                       open(os.path.join(ROOT, 'README.md'), encoding='utf-8-sig').read()).group(1).strip()
    rows = [('包内 About.xml', pkg_ver), ('仓库 About.xml', src_about),
            ('csproj', csproj), ('README', readme)]
    for name, v in rows:
        print('  %-16s %s %s' % (name, v, '✅' if v == pkg_ver else '❌'))
    if len({v for _, v in rows}) != 1:
        ok = False
    else:
        print('  ✅ 四处全部一致')

    print()
    print('════ ⑥ 同名多份是否同一文件 ════')
    zips = [f for f in os.listdir(OUT_DIR) if f.endswith('.zip')]
    if len(zips) <= 1:
        print('  ─  当前只有 1 个包（%s），本项不适用' % zips[0] if zips else '  ─  无包')
    else:
        hs = {}
        for f in zips:
            hs.setdefault(sha256_file(os.path.join(OUT_DIR, f)), []).append(f)
        for h, fs in hs.items():
            print('  %s  %s' % (h[:12], fs))
        print('  %s' % ('✅ 全部同 SHA256' if len(hs) == 1 else '⚠ 存在不同内容的包，按模板需人工确认'))

    shutil.rmtree(tmp, ignore_errors=True)
    return ok


def main():
    check_only = '--check' in sys.argv
    if check_only:
        zips = sorted(f for f in os.listdir(OUT_DIR) if f.endswith('.zip')) if os.path.isdir(OUT_DIR) else []
        if not zips:
            print('❌ 没有可验证的包'); return 1
        zp = os.path.join(OUT_DIR, zips[-1])
        ver = re.search(r'v([0-9.]+)\.zip$', zp)
        ver = ver.group(1) if ver else 'unknown'
        exp = build_expected()
    else:
        r = do_pack()
        if not r:
            return 1
        zp, exp, ver = r

    print()
    print('=' * 78)
    print('§6.2 验证：%s' % os.path.basename(zp))
    print('=' * 78)
    ok = verify(zp, exp, ver)
    print()
    print('=' * 78)
    print('✅ 全部通过' if ok else '❌ 存在不通过项（见上）')
    print('=' * 78)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
