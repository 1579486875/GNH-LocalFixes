# -*- coding: utf-8 -*-
"""监视 RimWorld.exe，一旦进程退出就立刻把新编译的 DLL 部署到两处游戏目录。

用途：游戏运行期间没法覆盖 DLL（Device or resource busy）。
本脚本在后台轮询，抢在用户重新启动游戏之前完成覆盖。

用法：
    python 工具/deploy-on-exit.py            # 监视并部署
    python 工具/deploy-on-exit.py --now      # 立刻尝试一次（不等待）
"""
import hashlib
import os
import subprocess
import sys
import time
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

SRC = os.path.join(ROOT, "bin", "Release", "GNH.LocalFixes.dll")
DESTS = [
    r"D:\steam\steamapps\common\RimWorld\Mods\模组兼容修复补丁\Assemblies\GNH.LocalFixes.dll",
    r"D:\RimWorld-1.6\Mods\模组兼容修复补丁\Assemblies\GNH.LocalFixes.dll",
]
LOG = os.path.join(ROOT, "_deploy_watch.log")

MAX_WAIT = 30 * 60      # 最多等 30 分钟
POLL = 0.4              # 轮询间隔（秒）


def log(msg):
    line = "[%s] %s" % (datetime.now().strftime("%H:%M:%S"), msg)
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def game_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq RimWorld.exe", "/NH"],
            capture_output=True, text=True, timeout=20,
            encoding="gbk", errors="replace",
        ).stdout
        return "RimWorld.exe" in out
    except Exception as e:                      # noqa: BLE001
        log("tasklist 调用失败（按「未运行」处理）：%r" % (e,))
        return False


def deploy():
    if not os.path.isfile(SRC):
        log("源 DLL 不存在：%s" % SRC)
        return False
    src_hash = sha256(SRC)
    src_size = os.path.getsize(SRC)
    log("源：%s  (%d B)  sha=%s" % (SRC, src_size, src_hash[:16]))

    ok = True
    for dst in DESTS:
        if not os.path.isdir(os.path.dirname(dst)):
            log("目标目录不存在，跳过：%s" % dst)
            ok = False
            continue
        try:
            old = sha256(dst) if os.path.isfile(dst) else "(无)"
            if old == src_hash:
                log("已是最新，跳过：%s" % dst)
                continue
            with open(SRC, "rb") as fi, open(dst, "wb") as fo:
                fo.write(fi.read())
            new = sha256(dst)
            if new != src_hash:
                log("写入后哈希不符！%s" % dst)
                ok = False
            else:
                log("已部署：%s  (%s -> %s)" % (dst, old[:16], new[:16]))
        except Exception as e:                  # noqa: BLE001
            log("部署失败 %s：%r" % (dst, e))
            ok = False
    return ok


def main():
    force_now = "--now" in sys.argv
    if force_now:
        if game_running():
            log("游戏仍在运行，无法覆盖 DLL。")
            return 1
        return 0 if deploy() else 1

    log("=== 开始监视 RimWorld.exe，退出即部署 ===")
    waited = 0.0
    while waited < MAX_WAIT:
        if not game_running():
            log("检测到 RimWorld.exe 已退出，立刻部署…")
            # 再确认一次，避免读到残留
            time.sleep(0.3)
            if game_running():
                log("游戏又起来了，继续等待。")
                time.sleep(POLL)
                waited += POLL
                continue
            ok = deploy()
            log("=== 部署完成：%s ===" % ("成功" if ok else "有失败项"))
            return 0 if ok else 1
        time.sleep(POLL)
        waited += POLL
    log("等待超时（%d 秒），未部署。" % MAX_WAIT)
    return 2


if __name__ == "__main__":
    sys.exit(main())
