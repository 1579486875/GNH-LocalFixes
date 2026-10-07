# ============================================================================
#  sync-refs.ps1 —— 把游戏本体的程序集同步进 GNH-LocalFixes\refs\
#
#  【什么时候需要跑它】
#
#    RimWorld 本体更新之后、以及在**新机器**上第一次编译本工程之前。
#
#    原因：工程里的 refs\Assembly-CSharp.dll 是游戏程序集的一份快照。
#    游戏更新后它不会自动变，你会拿旧 API 编译，编出来的 dll 装上去才炸
#    （MissingMethodException / TypeLoadException），而且报错信息离根因很远。
#
#  【它做什么】
#
#    把下面五份从游戏目录复制进 refs\：
#        Assembly-CSharp.dll
#        UnityEngine.dll / UnityEngine.CoreModule.dll
#        UnityEngine.IMGUIModule.dll / UnityEngine.TextRenderingModule.dll
#
#    ⚠ 后两个是 2026-10-06 审计后补上的：原先本脚本只同步三份，
#      而 csproj 的 GNHValidateRefs 会硬性检查 UnityEngine.IMGUIModule.dll 是否存在，
#      报错文案还写着「请运行 tools\sync-refs.ps1 同步」—— 那个脚本却永远补不上它。
#      叠加 .gitignore 忽略了 refs\，全新克隆会陷入「脚本补不齐 → 编译失败 →
#      报错又让你跑这个脚本」的死循环。
#    Windows 复制会保留原文件的修改时间，所以复制完两边时间戳一致 ——
#    这也正是「是否同步过」的判定依据。
#
#    refs\ 里的另外三个（0Harmony.dll / ElToro_BAddon.dll / RJW.dll）
#    不在游戏目录里，来自模组，本脚本不动它们；模组更新时若出现编译错误，
#    再手动更新那三个即可。
#
#  【怎么用】
#
#    这台机器默认禁止运行脚本，所以必须带 -ExecutionPolicy Bypass：
#
#
#    powershell -ExecutionPolicy Bypass -File tools\sync-refs.ps1
#    powershell -ExecutionPolicy Bypass -File tools\sync-refs.ps1 -RimWorldDir "D:\你的\RimWorld"
# ============================================================================

param(
    [string]$RimWorldDir = "D:\steam\steamapps\common\RimWorld"
)

$ErrorActionPreference = 'Stop'

$managed = Join-Path $RimWorldDir 'RimWorldWin64_Data\Managed'
$refs    = Join-Path $PSScriptRoot '..\refs'
$refs    = [System.IO.Path]::GetFullPath($refs)

Write-Host "游戏目录 : $RimWorldDir"
Write-Host "refs 目录: $refs"
Write-Host ""

if (-not (Test-Path -LiteralPath $managed)) {
    Write-Host "★ 找不到游戏程序集目录：$managed" -ForegroundColor Red
    Write-Host "  请用 -RimWorldDir 指定正确的 RimWorld 安装目录。" -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath $refs)) {
    Write-Host "★ 找不到 refs 目录：$refs" -ForegroundColor Red
    exit 2
}

$files = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextRenderingModule.dll'
)

$changed = 0
foreach ($f in $files) {
    $src = Join-Path $managed $f
    $dst = Join-Path $refs    $f

    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "  跳过（游戏目录里没有）：$f" -ForegroundColor Yellow
        continue
    }

    $srcTime = (Get-Item -LiteralPath $src).LastWriteTimeUtc
    $needCopy = $true
    if (Test-Path -LiteralPath $dst) {
        $dstTime = (Get-Item -LiteralPath $dst).LastWriteTimeUtc
        # 注意：refs\ 可能位于 exFAT 分区（时间戳只有 2 秒精度），
        # 而游戏目录在 NTFS（100 纳秒精度）。直接比 tick 会永远不相等，
        # 所以这里允许 2 秒以内的误差，并且同时比文件大小。
        $timeClose = [math]::Abs(($srcTime - $dstTime).TotalSeconds) -lt 2
        if ($timeClose -and (Get-Item -LiteralPath $src).Length -eq (Get-Item -LiteralPath $dst).Length) {
            Write-Host "  已是最新：$f" -ForegroundColor Green
            $needCopy = $false
        }
    }

    if ($needCopy) {
        Copy-Item -LiteralPath $src -Destination $dst -Force
        $newTime = (Get-Item -LiteralPath $dst).LastWriteTimeUtc
        Write-Host ("  已更新  ：{0}  （{1:yyyy-MM-dd HH:mm}）" -f $f, $newTime) -ForegroundColor Cyan
        $changed++
    }
}

Write-Host ""
if ($changed -eq 0) {
    Write-Host "refs\ 已经与游戏本体一致，无需改动。" -ForegroundColor Green
} else {
    Write-Host "已同步 $changed 个文件。现在可以编译了：" -ForegroundColor Green
    Write-Host '    dotnet build GNH.LocalFixes.csproj -c Release'
}
# ---- refs\ 里来自模组的那三个（本脚本没法自动同步，但必须能查出缺没缺） ----------
#
# 这三个不在游戏目录里，来源是模组，路径随工坊 ID / 本地模组目录结构变化，
# 所以不适合写死自动复制。但「什么都不说」是不行的：新克隆上它们一定缺失，
# 而缺任何一个都编不过。这里至少把它们查出来，并给出当前已知的出处。
$modFiles = @(
    @{ Name = '0Harmony.dll';      Where = '创意工坊 2009463077（Harmony）下的 Current\Assemblies\' },
    @{ Name = 'ElToro_BAddon.dll'; Where = 'Mods\RJW_玩法_ElToro的人兽杂交\1.6\Assemblies\' },
    @{ Name = 'RJW.dll';           Where = 'Mods\RJW_核心_本体\1.6\Assemblies\' }
)
$missingMod = @()
foreach ($m in $modFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $refs $m.Name))) { $missingMod += $m }
}

Write-Host ""
if ($missingMod.Count -eq 0) {
    Write-Host "refs\ 里的三个模组来源文件（0Harmony / ElToro_BAddon / RJW）都在。" -ForegroundColor Green
} else {
    Write-Host "★ 还缺下面这些来自模组的文件（不在游戏目录里，需要手动复制进 refs\）：" -ForegroundColor Yellow
    foreach ($m in $missingMod) {
        Write-Host ("    " + $m.Name) -ForegroundColor Yellow
        Write-Host ("      ← " + $m.Where) -ForegroundColor DarkGray
    }
    Write-Host "  这些路径会随模组目录结构变化；找不到就按文件名在模组目录里搜。" -ForegroundColor DarkGray
    Write-Host "  三个都备齐之前，本工程编译不会成功。" -ForegroundColor DarkGray
}
