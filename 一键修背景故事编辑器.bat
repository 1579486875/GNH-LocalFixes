@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================================
echo   修「背景故事创建器」一打开就报空引用
echo ============================================================
echo.

rem ── ① 检查游戏是否还开着 ────────────────────────────────────
tasklist /FI "IMAGENAME eq RimWorld.exe" 2>nul | find /I "RimWorld.exe" >nul
if not errorlevel 1 (
    echo [×] 游戏还在运行。
    echo     请先完全退出 RimWorld（不是退回主菜单，是关掉进程），再双击本文件。
    echo.
    pause
    exit /b 1
)
echo [√] 游戏已关闭
echo.

rem ── ② 重新部署本修复补丁（把新 DLL 写进两个模组目录）────────
echo ── 第 1 步：部署「多模组红字修复补丁」──
python 工具\deploy-dll.py --write
if errorlevel 1 (
    echo.
    echo [×] 部署失败。请确认已安装 Python，并在本目录下运行。
    pause
    exit /b 1
)
echo.

rem ── ③ 修「背景故事创建器」的配置文件 ──────────────────────
echo ── 第 2 步：修「背景故事创建器」的配置文件 ──
python 工具\修背景故事编辑器.py --写入
echo.

rem ── ④ 验证 ─────────────────────────────────────────────────
echo ── 第 3 步：全量验证 ──
python 工具\full-regression.py
echo.

echo ============================================================
echo   完成。现在可以启动游戏了。
echo   进游戏后打开「背景故事创建器」，「禁用特性」与「绑定特性」
echo   两栏应当正常显示、加号可以点。
echo ============================================================
echo.
pause
