#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""check-version-consistency.py · 版本号「三处一致 + 两环境一致」硬门禁

## 为什么要它（2026-11-16 审查 D-02）

模板 §1.1 把「版本号三处一致」列为完整性维度的一条硬要求：

    About.xml <modVersion>  /  csproj <Version>  /  DLL AssemblyVersion

本项目 2026-11-16 实测**不一致**：

    About.xml      1.4.63
    csproj         1.4.57      ← 最近 6 次改版只改了 About
    DLL            1.4.57.0

而 `GNH.LocalFixes.csproj` 第 22-29 行的注释明确写着「本行在本工程里是**真正生效**的，
没有 GenerateAssemblyInfo=false、也没有 AssemblyInfo.cs，这里改多少 DLL 里就是多少」——
也就是说这不是「写了不生效」的字段，是**漏改**。

## 判据

1. `About/About.xml` 的 `<modVersion>`（字符串，形如 `1.4.63`）
2. `GNH.LocalFixes.csproj` 的 `<Version>`（字符串，同形）
3. `bin/Release/GNH.LocalFixes.dll` 的 `AssemblyVersion`（四段，取前三段比对）
4. 部署到 **1.7 主安装** 与 **1.6 引擎副本** 的 `About.xml` 的 `<modVersion>`
5. 两个部署目标的 DLL 与 `bin/Release` **同 SHA256**

任一条不符 → exit 1。

## 为什么不用纯 Python 读 DLL 版本

PE/CLI metadata 里 `AssemblyVersion` 在 `#Blob` 堆的 `Assembly` 表里，
自己解析要写不少代码且容易出错；`AssemblyInformationalVersion` 又在 `#Strings` 堆，
两个都可能被 `AssemblyFileVersion` 混淆。用一次 `System.Reflection` 是**更短的证明**。

⚠ 依赖：需要 `powershell`（Windows 自带）。取不到就**报错退出**，不许静默跳过 ——
「读不到就当一致」是这类脚本最常见的自欺。
"""
import os
import re
import sys
import json
import hashlib
import subprocess
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ABOUT = os.path.join(REPO, "About", "About.xml")
CSPROJ = os.path.join(REPO, "GNH.LocalFixes.csproj")
DLL = os.path.join(REPO, "bin", "Release", "GNH.LocalFixes.dll")

TARGETS = [
    ("1.7 主安装", r"D:\steam\steamapps\common\RimWorld\Mods\模组兼容修复补丁"),
    ("1.6 引擎副本", r"D:\RimWorld-1.6\Mods\模组兼容修复补丁"),
]

fails = []


def bad(msg):
    fails.append(msg)
    print("  ❌ %s" % msg)


def ok(msg):
    print("  ✅ %s" % msg)


def read_about_version(p):
    root = ET.fromstring(open(p, "rb").read().decode("utf-8-sig"))
    e = root.find("modVersion")
    return (e.text or "").strip() if e is not None else None


def read_csproj_version(p):
    txt = open(p, "rb").read().decode("utf-8-sig")
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", txt)
    return m.group(1) if m else None


def read_dll_version(p):
    """返回 (AssemblyVersion, InformationalVersion, FileVersion)；读不到返回 (None,None,None)。"""
    ps = (
        "$ErrorActionPreference='Stop';"
        "$p=%s;"
        "$a=[System.Reflection.AssemblyName]::GetAssemblyName($p);"
        "$an=[System.Reflection.Assembly]::ReflectionOnlyLoadFrom($p);"
        "$attrs=$an.GetCustomAttributesData();"
        "$iv=($attrs|Where-Object{$_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute'});"
        "$fv=($attrs|Where-Object{$_.AttributeType.Name -eq 'AssemblyFileVersionAttribute'});"
        "$o=@{av=$a.Version.ToString();"
        "iv=if($iv){$iv[0].ConstructorArguments[0].Value}else{''};"
        "fv=if($fv){$fv[0].ConstructorArguments[0].Value}else{''}};"
        "$o|ConvertTo-Json -Compress"
    ) % ("'" + p.replace("'", "''") + "'")
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                           capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=60)
    except Exception as ex:                       # noqa: BLE001 - 兜底要报出来
        bad("读 DLL 版本时调用 powershell 失败：%s" % ex)
        return None, None, None
    out = (r.stdout or "").strip()
    if r.returncode != 0 or not out:
        bad("读 DLL 版本失败（powershell exit=%s）：%s"
            % (r.returncode, ((r.stderr or "")[:300])))
        return None, None, None
    try:
        d = json.loads(out.splitlines()[-1])
    except Exception as ex:                       # noqa: BLE001
        bad("解析 powershell 输出失败：%s / %r" % (ex, out[:200]))
        return None, None, None
    return d.get("av") or None, d.get("iv") or None, d.get("fv") or None


def sha(p):
    return hashlib.sha256(open(p, "rb").read()).hexdigest()


def main():
    print("=" * 72)
    print("版本号一致性（三处 + 两环境）")
    print("=" * 72)

    v_about = read_about_version(ABOUT)
    v_csproj = read_csproj_version(CSPROJ)
    if v_about is None:
        bad("About.xml 没有 <modVersion>")
    if v_csproj is None:
        bad("csproj 没有 <Version>")
    print("  About.xml <modVersion> : %s" % v_about)
    print("  csproj    <Version>    : %s" % v_csproj)

    if not os.path.exists(DLL):
        bad("找不到构建产物 %s（先跑 dotnet build -c Release）" % DLL)
        av = iv = fv = None
    else:
        av, iv, fv = read_dll_version(DLL)
        print("  DLL AssemblyVersion    : %s" % av)
        print("  DLL FileVersion        : %s" % fv)
        print("  DLL InfoVersion        : %s" % iv)
        if av:
            av3 = ".".join(av.split(".")[:3])
            if v_about and av3 != v_about:
                bad("DLL AssemblyVersion 前三段 %s ≠ About %s" % (av3, v_about))
            else:
                ok("DLL AssemblyVersion 与 About 一致（%s）" % av3)
        else:
            bad("读不到 DLL AssemblyVersion")
        if iv and v_about and iv != v_about:
            bad("DLL InformationalVersion %s ≠ About %s" % (iv, v_about))

    if v_about and v_csproj and v_about != v_csproj:
        bad("csproj <Version> %s ≠ About <modVersion> %s" % (v_csproj, v_about))
    elif v_about and v_csproj:
        ok("About 与 csproj 一致（%s）" % v_about)

    print()
    print("-" * 72)
    print("部署目标")
    print("-" * 72)
    repo_dll_sha = sha(DLL) if os.path.exists(DLL) else None
    for label, d in TARGETS:
        print("[%s] %s" % (label, d))
        if not os.path.isdir(d):
            bad("%s：部署目录不存在" % label)
            continue
        p_about = os.path.join(d, "About", "About.xml")
        p_dll = os.path.join(d, "Assemblies", "GNH.LocalFixes.dll")
        if not os.path.exists(p_about):
            bad("%s：缺 About/About.xml" % label)
        else:
            dv = read_about_version(p_about)
            if dv != v_about:
                bad("%s：About modVersion %s ≠ 仓库 %s" % (label, dv, v_about))
            else:
                ok("%s：About modVersion 一致（%s）" % (label, dv))
        if not os.path.exists(p_dll):
            bad("%s：缺 Assemblies/GNH.LocalFixes.dll" % label)
        elif repo_dll_sha and sha(p_dll) != repo_dll_sha:
            bad("%s：DLL 与 bin/Release 不同 SHA256（部署 %s… / 仓库 %s…）"
                % (label, sha(p_dll)[:12], repo_dll_sha[:12]))
        else:
            ok("%s：DLL 与 bin/Release 同 SHA256（%s…）"
               % (label, (repo_dll_sha or "")[:12]))
        print()

    print("=" * 72)
    if fails:
        print("❌ 失败 %d 项" % len(fails))
        for f in fails:
            print("   - %s" % f)
        return 1
    print("✅ 全部一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
