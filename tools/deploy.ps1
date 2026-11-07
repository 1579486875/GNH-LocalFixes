# ============================================================================
#  deploy.ps1 —— 把编译好的 bin\<配置>\ 部署到 RimWorld 的 Mods\ 目录
#
#  【它做什么】
#
#    1. 先确认游戏没在运行。正在运行时 dll 被占用、复制必然失败，而且会留下
#       「新 About + 旧 dll」这种半套状态，比不部署更难排查 —— 所以默认直接拒绝；
#    2. 把目标目录里现有的版本**整个备份**到工程目录下
#       _backup\<名字>.backup-<时间戳>\ —— 刻意不放在 Mods\ 里，原因见下面「⚠ 备份放哪」；
#    3. 复制 About\ / Assemblies\ / Patches\ / Languages\ / README.md；
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
#  【为什么要有「根目录白名单校验」这一步】
#
#    2026-11-07 实测：部署根目录里躺着一个 293 KB 的 GNH-LocalFixes\ 嵌套子目录
#    （含 README.md 103,599 B + 技术说明.md 190,112 B，都是 2026-11-03 一次误复制
#    留下的），另外还有 .gitignore / .gitattributes / GNH.LocalFixes.csproj 三个
#    工程文件。而上面那个 $dirMap 只清理 About / Assemblies / Patches / Languages
#    **四个目录的内部** —— 根层的散文件与子目录它一个都看不见。
#
#    后果不是报错，而是「部署 == 仓库」这个断言在工具层面变成了假的：
#    脚本照样打印 OK - identical，人照样以为干净，污染就一直留着。
#
#    所以部署收尾时扫一遍部署目标目录的**根层**，与白名单比对，
#    白名单以外的条目逐个打出名字与字节数（只报警、不自动删）。
#    需要连删一起做时加 -PruneUnexpected（移入回收站，不是永久删除）。
#
#  【改这个文件时注意：必须保存为「UTF-8 带 BOM」】
#
#    Windows PowerShell 5.1 读没有 BOM 的 .ps1 时会按系统 ANSI 代码页解码，
#    本文件里的中文（包括默认的模组目录名）会变成乱码，甚至让解析器报
#    "Unexpected token"。改完务必确认开头三个字节仍是 EF BB BF。
# ============================================================================

param(
    [string]$ModsDir = 'D:\steam\steamapps\common\RimWorld\Mods',
    [string]$DeployName = '模组兼容修复补丁',
    [string]$Configuration = 'Release',
    [switch]$Force,
    # 二级保护：白名单以外的条目不是只报警，而是移入回收站（默认关闭）。
    # 为什么默认关：自动删有风险，先让人看一眼报出来的名字再决定。
    [switch]$PruneUnexpected
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

# ---- 备份轮转：只保留最近 N 份 ---------------------------------------------
#
# 2026-11-01 整理：此前这里的备份是「只增不减」，一个月堆了 106 份、31 MB，
# 其中九成是同一天里连续部署产生的近重复快照（同一秒跑两次还会多出一份
# 带 -2 后缀的）。真正有价值的只是「最近几次」和「每个工作日的最终态」。
#
# 规则：按 LastWriteTime 倒序保留最近 $BackupKeep 份，更早的移入**回收站**
#      （不是永久删除，误删可以从回收站还原）。
#      只处理本脚本自己生成的 <部署名>.backup-* 目录；_backup\ 下人工放的
#      其它备份（pre-xxx、deployed-xxx、preview-xxx 等）一律不碰。
$BackupKeep = 10
$autoBaks = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -like ($DeployName + '.backup-*') } |
              Sort-Object LastWriteTime -Descending)
if ($autoBaks.Count -gt $BackupKeep) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    $rotated = 0
    foreach ($old in ($autoBaks | Select-Object -Skip $BackupKeep)) {
        try {
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
                $old.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
            $rotated++
        } catch {
            Write-Host ("  备份轮转失败: " + $old.Name + " -- " + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    if ($rotated -gt 0) {
        Write-Host ("  备份轮转：移入回收站 " + $rotated + " 份，保留最近 " + $BackupKeep + " 份")
    }
}

# ---- 清理目标侧的「幽灵文件」------------------------------------------------
#
# 下面那个 Copy-Item -Force 只覆盖同名文件，**不会删除**目标侧多出来的东西。
# 于是「bin 里删掉了某个 Patches\*.xml」之后，部署目录里会一直留着它的旧副本，
# 游戏照样会加载它 —— 表现为「明明删了却还在生效」，极难排查。
# 所以这里先把「bin 里已不存在、但部署目录里还有」的文件摘掉。
#（上面已经整目录备份过，误删也能从 _backup\ 拿回来。）
# 要同步的目录清单：每一项都写清「从哪儿取」。
#   Assemblies / Patches —— 从构建输出 bin\Release 取（它们本身就是构建产物）；
#   About                —— ⚠ **必须从工程根目录取**，不能从 bin\Release 取。
#                           bin 里的 About 是「上一次构建时」复制过去的**快照**：
#                           改了 About.xml / 换了封面但还没重新构建就跑部署，
#                           会把刚做的改动**静默回退**成旧版。
#                           2026-10-12 实测：bin\Release\About\About.xml 比工程根
#                           少 11 字节、Preview.png 少 702 字节 —— 一次部署就等于
#                           撤销上一轮的全部文案/封面改动，而且日志里一个字都不提。
#   Languages            —— 同样从工程根目录取（模组设置界面的中/英翻译）。
#                           漏掉它的后果：部署目录里没有翻译文件，英文环境下设置
#                           界面会回退成代码里写死的中文 —— 不会崩，但「英文环境
#                           显示英文」这个目标就落空了。
$dirMap = @(
    @{ From = $root; Name = 'About' },
    @{ From = $src;  Name = 'Assemblies' },
    @{ From = $src;  Name = 'Patches' },
    @{ From = $root; Name = 'Languages' }
)
# ⚠ 保留白名单：这些文件**只在部署目录里存在**，源侧永远没有 —— 绝不能当成
#   「幽灵文件」删掉。
#     About\PublishedFileId.txt —— Steam 工坊的发布凭据（内容 3818289834，
#     用户本人的工坊项）。删掉它，游戏就认不出本模组对应哪个工坊项，
#     工坊更新会失效，而且要重新走一遍上传流程才能找回这个 ID。
#     2026-10-12 起它也已纳入仓库（工程根 About\ 下有一份），但白名单仍然保留：
#     万一以后有人把它从仓库里移走，这里也不该顺手把部署侧那份删了。
$keepRelPaths = @('About\PublishedFileId.txt')
$staleRemoved = 0
foreach ($entry in $dirMap) {
    $sItem = Join-Path $entry.From $entry.Name
    $dItem = Join-Path $dst $entry.Name
    if (-not (Test-Path -LiteralPath $dItem)) { continue }
    Get-ChildItem -LiteralPath $dItem -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($dItem.Length).TrimStart('\')
        $relKey = $entry.Name + '\' + $rel
        if ($keepRelPaths -contains $relKey) {
            # 白名单文件不参与「幽灵文件」清理（原因见上面那段注释）。
            # 这里用 return 而不是 continue：在 ForEach-Object 的脚本块里，
            # return 表示「跳过当前这个对象、继续下一个」，正是我们要的语义。
            return
        }
        if (-not (Test-Path -LiteralPath (Join-Path $sItem $rel))) {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Host ("  removed stale: " + $entry.Name + '\' + $rel)
            $staleRemoved++
        }
    }
}
if ($staleRemoved -gt 0) {
    Write-Host ("  已清理 " + $staleRemoved + " 个 bin 里已不存在的残留文件。")
}

# ---- 复制 -------------------------------------------------------------------
New-Item -ItemType Directory -Path $dst -Force | Out-Null

foreach ($entry in $dirMap) {
    $s = Join-Path $entry.From $entry.Name
    if (-not (Test-Path -LiteralPath $s)) {
        Write-Host ("  MISSING dir: " + $s) -ForegroundColor Yellow
        continue
    }
    $d = Join-Path $dst $entry.Name
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    Copy-Item -Path (Join-Path $s '*') -Destination $d -Recurse -Force
    Write-Host ("  copied " + $entry.Name + "\")

    # 排除编辑期备份：*.bak / *.bak-<时间戳> 是改 XML 时手工留下的，一旦落在
    # About\ 或 Languages\ 里，就会被 build 带进 bin\Release，再被上面那行
    # 整目录复制到 Mods\ —— RimCrow 扫描时会多出一批同内容文件。
    # 2026-11-03 实测发生过一次（About.xml + 三份语言文件，共 4 个）。
    $bakLeft = @(Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -like '*.bak*' })
    foreach ($b in $bakLeft) {
        Remove-Item -LiteralPath $b.FullName -Force
        Write-Host ("  skipped backup: " + $entry.Name + '\' + $b.Name)
    }
    if ($bakLeft.Count -gt 0) {
        Write-Host ("  已排除 " + $bakLeft.Count + " 个 *.bak 备份文件（不该进 Mods\）")
    }
}

# ---- 根目录下的说明文件 ------------------------------------------------------
#
# 这三个文件从【工程根目录】取，不是从 bin\ 取：csproj 只为 README.md 配了
# CopyToOutputDirectory，LICENSE 与 技术说明.md 根本不在构建输出里。
# 若照旧写 Join-Path $src，会静默漏掉后两个（Test-Path 不成立就跳过），
# 部署目录于是比仓库少东西 —— 这类「少文件」最难发现，日志里什么都不打印。
foreach ($rf in @('README.md', 'LICENSE', '技术说明.md')) {
    $p = Join-Path $root $rf
    if (Test-Path -LiteralPath $p) {
        Copy-Item -LiteralPath $p -Destination $dst -Force
        Write-Host ("  copied " + $rf)
    } else {
        Write-Host ("  MISSING root file: " + $p) -ForegroundColor Yellow
    }
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

# ---- 根目录白名单校验 -------------------------------------------------------
#
# 上面那个 $dirMap 只处理四个目录**内部**；根层的散文件与子目录不在它的视野里。
# 这里补一道闸：白名单以外的一律列出来（名字 + 字节数），默认只报警不删。
$rootWhitelist = @('About', 'Assemblies', 'Patches', 'Languages',
                   'README.md', 'LICENSE', '技术说明.md')
$unexpected = @(Get-ChildItem -LiteralPath $dst -Force |
                Where-Object { $rootWhitelist -notcontains $_.Name })
Write-Host ''
if ($unexpected.Count -eq 0) {
    Write-Host '--- 根目录白名单校验：通过（无白名单以外的条目）---'
} else {
    Write-Host '--- 根目录白名单校验：发现白名单以外的条目 ---' -ForegroundColor Yellow
    foreach ($u in $unexpected) {
        if ($u.PSIsContainer) {
            $sum = (Get-ChildItem -LiteralPath $u.FullName -Recurse -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum).Sum
            if ($null -eq $sum) { $sum = 0 }
            Write-Host ("  DIR   " + $u.Name + "\  [" + $sum + " B]") -ForegroundColor Yellow
        } else {
            Write-Host ("  FILE  " + $u.Name + "  [" + $u.Length + " B]") -ForegroundColor Yellow
        }
    }
    Write-Host '  ⚠ 这些不在发布包白名单里：可能是误复制、工程文件、或历史残留。' -ForegroundColor Yellow
    Write-Host '    本步只报警、不删除（自动删有风险）。确认无用后手工清理，' -ForegroundColor Yellow
    Write-Host '    或加 -PruneUnexpected 让脚本把它们移入回收站。' -ForegroundColor Yellow
    if ($PruneUnexpected) {
        Add-Type -AssemblyName Microsoft.VisualBasic
        foreach ($u in $unexpected) {
            try {
                if ($u.PSIsContainer) {
                    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
                        $u.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
                } else {
                    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
                        $u.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
                }
                Write-Host ("  pruned -> 回收站: " + $u.Name)
            } catch {
                Write-Host ("  prune 失败: " + $u.Name + " -- " + $_.Exception.Message) -ForegroundColor Yellow
            }
        }
    }
}

Write-Host ''
Write-Host '部署完成。'
exit 0
