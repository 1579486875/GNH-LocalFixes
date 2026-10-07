# ============================================================================
#  deploy.ps1 —— 把编译好的 bin\<配置>\ 部署到 RimWorld 的 Mods\ 目录
#
#  【它做什么】
#
#    1. 先确认游戏没在运行。正在运行时 dll 被占用、复制必然失败，而且会留下
#       「新 About + 旧 dll」这种半套状态，比不部署更难排查 —— 所以默认直接拒绝；
#    2. 把目标目录里现有的版本**整个备份**到工程目录下
#       _backup\<名字>.backup-<时间戳>\ —— 刻意不放在 Mods\ 里，原因见下面「⚠ 备份放哪」；
#    3. 复制 About\ / Assemblies\ / Patches\ / README.md；
#    4. 打印部署后的目录树和 dll 的 SHA256，方便和 bin\ 下的对一下。
#
#  【怎么用】—— 在本工程根目录下
#
#    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
#    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -ModsDir "D:\你的\RimWorld\Mods"
#    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Force   # 游戏在跑也硬上（不推荐）
#
#  【退出码】
#
#    0 = 部署成功
#    1 = 游戏正在运行，本次没有部署
#    2 = 前置条件缺失（bin 里没有 dll、或 Mods 目录不存在）
#    3 = 已复制但 dll 哈希与源不一致（部署动作做了，结果不对）
#
#  【改这个文件时注意：必须保存为「UTF-8 带 BOM」】
#
#    Windows PowerShell 5.1 读没有 BOM 的 .ps1 时会按系统 ANSI 代码页解码，
#    本文件里的中文（包括默认的模组目录名）会变成乱码，甚至让解析器报
#    "Unexpected token"。改完务必确认开头三个字节仍是 EF BB BF。
# ============================================================================

param(
    [string]$ModsDir = 'D:\steam\steamapps\common\RimWorld\Mods',
    [string]$DeployName = 'GNH-本地修复补丁',
    [string]$Configuration = 'Release',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$src  = Join-Path $root ("bin\{0}" -f $Configuration)
$dst  = Join-Path $ModsDir $DeployName

Write-Host '============================================================'
Write-Host ' GNH LocalFixes: deploy'
Write-Host '============================================================'
Write-Host ("  source     : " + $src)
Write-Host ("  destination: " + $dst)
Write-Host ''

# ---- 前置检查 ---------------------------------------------------------------
$builtDll = Join-Path $src 'Assemblies\GNH.LocalFixes.dll'
if (-not (Test-Path -LiteralPath $builtDll)) {
    Write-Host ("  MISSING: " + $builtDll)
    Write-Host ("  先编译：dotnet build GNH.LocalFixes.csproj -c " + $Configuration)
    # -Configuration 传错是最常见的成因（例如顺手写成 Debug），单独提示一句。
    if ($Configuration -ne 'Release') {
        Write-Host ("  注意：本次 -Configuration 传的是 '" + $Configuration + "'，本工程平时用的是 Release。") -ForegroundColor Yellow
    }
    exit 2
}
if (-not (Test-Path -LiteralPath $ModsDir)) {
    Write-Host ("  MISSING: Mods 目录不存在 → " + $ModsDir)
    Write-Host '  用 -ModsDir 指定正确的路径。'
    exit 2
}

# ---- 游戏是否在运行 ---------------------------------------------------------
$proc = Get-Process -Name 'RimWorldWin64' -ErrorAction SilentlyContinue
if ($proc -and -not $Force) {
    Write-Host ("  RimWorld 正在运行（pid " + (($proc | ForEach-Object { $_.Id }) -join ',') + "），本次不部署。")
    Write-Host '  原因：游戏加载中的 dll 被占用，复制会失败，而且会留下'
    Write-Host '        「新 About.xml + 旧 dll」这种半套状态，比不部署更难排查。'
    Write-Host '  请先退出游戏，再重新运行本脚本。（确实要硬上就加 -Force，不推荐。）'
    exit 1
}

# ---- 备份旧版本 -------------------------------------------------------------
#
# ⚠ 备份放哪 —— 2026-10-06 踩过的坑：
#
#   RimWorld 加载模组时扫的是「Mods\ 下每一个子目录」，只要那里有 About\About.xml
#   就当成一个模组；RimCrow 也是同样的扫法。所以备份一旦留在 Mods\ 里，
#   就会凭空多出一个**同 packageId 的第二个副本**，RimCrow 的「处理重复模组」
#   界面立刻报冲突，还得手工去清。
#
#   这里把备份放到工程目录下的 _backup\（部署目标在 Mods\ 里，两者天然分开），
#   既不占 Mods 的扫描范围，也离源码近、好找。
$backupRoot = Join-Path $root '_backup'
if (Test-Path -LiteralPath $dst) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    if (-not (Test-Path -LiteralPath $backupRoot)) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    }
    # 同一个时间戳如果已经用过（同一秒内跑了两次），就往后加序号 ——
    # 否则 Copy-Item 会把源目录整个**塞进已存在的那个备份目录里**，
    # 变成 _backup\名字.backup-<时间戳>\名字\... 这种套娃结构。
    $bak = Join-Path $backupRoot ($DeployName + '.backup-' + $stamp)
    $suffix = 1
    while (Test-Path -LiteralPath $bak) {
        $suffix++
        $bak = Join-Path $backupRoot ($DeployName + '.backup-' + $stamp + '-' + $suffix)
    }
    Copy-Item -LiteralPath $dst -Destination $bak -Recurse -Force
    Write-Host ("  已备份旧版本 → " + $bak)
} else {
    Write-Host '  目标目录不存在，将新建。'
}

# ---- 清理目标侧的「幽灵文件」------------------------------------------------
#
# 下面那个 Copy-Item -Force 只覆盖同名文件，**不会删除**目标侧多出来的东西。
# 于是「bin 里删掉了某个 Patches\*.xml」之后，部署目录里会一直留着它的旧副本，
# 游戏照样会加载它 —— 表现为「明明删了却还在生效」，极难排查。
# 所以这里先把「bin 里已不存在、但部署目录里还有」的文件摘掉。
#（上面已经整目录备份过，误删也能从 _backup\ 拿回来。）
$staleRemoved = 0
foreach ($item in @('About', 'Assemblies', 'Patches')) {
    $sItem = Join-Path $src $item
    $dItem = Join-Path $dst $item
    if (-not (Test-Path -LiteralPath $dItem)) { continue }
    Get-ChildItem -LiteralPath $dItem -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($dItem.Length).TrimStart('\')
        if (-not (Test-Path -LiteralPath (Join-Path $sItem $rel))) {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Host ("  removed stale: " + $item + '\' + $rel)
            $staleRemoved++
        }
    }
}
if ($staleRemoved -gt 0) {
    Write-Host ("  已清理 " + $staleRemoved + " 个 bin 里已不存在的残留文件。")
}

# ---- 复制 -------------------------------------------------------------------
New-Item -ItemType Directory -Path $dst -Force | Out-Null

foreach ($item in @('About', 'Assemblies', 'Patches')) {
    $s = Join-Path $src $item
    if (-not (Test-Path -LiteralPath $s)) { continue }
    $d = Join-Path $dst $item
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    Copy-Item -Path (Join-Path $s '*') -Destination $d -Recurse -Force
    Write-Host ("  copied " + $item + "\")
}

$readme = Join-Path $src 'README.md'
if (Test-Path -LiteralPath $readme) {
    Copy-Item -LiteralPath $readme -Destination $dst -Force
    Write-Host '  copied README.md'
}

# ---- 报告 -------------------------------------------------------------------
Write-Host ''
Write-Host '--- deployed tree ---'
Get-ChildItem -LiteralPath $dst -Recurse -File | ForEach-Object {
    Write-Host ("  " + $_.FullName.Replace($dst + '\', '') + "  [" + $_.Length + "]")
}

Write-Host ''
Write-Host '--- dll hash (must match the one under bin\) ---'
$h1 = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
$h2 = (Get-FileHash -LiteralPath (Join-Path $dst 'Assemblies\GNH.LocalFixes.dll') -Algorithm SHA256).Hash
Write-Host ("  bin\Assemblies  : " + $h1)
Write-Host ("  Mods\<name>\...  : " + $h2)
if ($h1 -ne $h2) {
    Write-Host '  *** MISMATCH ***'
    Write-Host '  注意：此时文件**已经复制过去了**，只是复制结果和源不一致'
    Write-Host '        （磁盘问题 / 杀软拦截 / 中途被占用都可能造成）。'
    Write-Host '  这与「游戏在运行、根本没部署」是两回事，所以用不同的退出码。'
    exit 3
}
Write-Host '  OK - identical'

Write-Host ''
Write-Host '部署完成。'
exit 0
