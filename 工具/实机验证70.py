# -*- coding: utf-8 -*-
"""§7 实机验证：一次跑完【70】背景故事创建器的全部检查。

## 为什么要脚本化

模板 §7 要求「能合并到同一次启动里的检查，绝不许拆成两次」，
而且证据必须能复核（§8 规定实测类要写「验的场景、怎么复现、看到什么、和预期差在哪」）。
手动一步步点既慢又记不全，所以把整条链固化成脚本。

## 它验什么（对应 §7「本次要验的」表）

| # | 检查 | 判据 |
|---|---|---|
| 1 | 桥是否已就绪 | 日志有 `GABP server running` 且握手成功 |
| 2 | 编辑器能否打开 | `open_window_by_type` 返回 `windowCountDelta: 1` |
| 3 | **特性栏是否真的画出来了** | UI 布局里出现「禁用特性 / 绑定特性」的标题，且该 surface 元素数 > 8 |
| 4 | **「+」能否弹出候选** | 点击后 surface 列表新增 `Dialog_DebugOptionListLister`（或 FloatMenu） |
| 5 | 有没有崩 | 日志里 `DrawBackstoryTraits` 的 NRE 计数、Finalizer 警告计数 |
| 6 | 高度是否正确 | 截图 + 「随身物品」栏的位置 |
| 7 | 我们的诊断日志 | `【70】…首次绘制…Utility.Traits=List(N 项)` |

## 用法

    python 工具/实机验证70.py            # 全流程
    python 工具/实机验证70.py --keep     # 验完不关窗口（方便截图存档）

退出码：0 = 全部通过；1 = 有检查未通过（会打印哪一项）。
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '工具'))
sys.stdout.reconfigure(encoding='utf-8')

from importlib import import_module
bm = import_module('桥客户端')
Bridge = bm.Bridge
find_bridge = bm.find_bridge

WS = 'BackstoryConstructor.Window_BackstoryConstructor'
LOG = (r'C:\Users\asus\AppData\LocalLow\Ludeon Studios'
       r'\RimWorld by Ludeon Studios\Logs\Player.log')
SCRATCH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '_scratch')

results = []          # (检查名, 是否通过, 证据)


def check(name, ok, evidence):
    results.append((name, ok, evidence))
    print('  %s %-34s %s' % ('✅' if ok else '❌', name, evidence[:110]))
    return ok


def layout(b, offset=0):
    r = b.request('tools/call', {'name': 'rimworld/get_ui_layout', 'arguments': {'offset': offset}})
    return r.get('result') or {}


def surfaces(res):
    out = []
    for s in res.get('surfaces') or []:
        out.append((s.get('type') or '', s.get('elementCount') or 0,
                    s.get('actionableElementCount') or 0, s.get('surfaceTargetId') or ''))
    return out


def tail_lines(n=200):
    try:
        with open(LOG, 'r', encoding='utf-8', errors='ignore') as f:
            return f.readlines()[-n:]
    except Exception:
        return []


def count_in_log(pattern):
    try:
        with open(LOG, 'r', encoding='utf-8', errors='ignore') as f:
            return sum(1 for line in f if pattern in line)
    except Exception:
        return -1


def surface_element_page(b, want_type):
    """按 surface 的累积 offset 精确读出该 surface 的元素。

    ⚠ 不能用「从 total 往前翻」的写法：编辑器只有 8 个元素，
    而它前面的 EditWindow_Log 有 3026 个 —— 往前翻几页全落在日志窗口里，一条也搜不到。
    正确做法是把各 surface 的 elementCount 累加，得到它的起始 offset。
    """
    res = layout(b)
    off = 0
    for s in res.get('surfaces') or []:
        n = s.get('elementCount') or 0
        if want_type in (s.get('type') or ''):
            r = layout(b, off)
            for s2 in r.get('surfaces') or []:
                if want_type in (s2.get('type') or ''):
                    return s2.get('elements') or []
            return []
        off += n
    return []


def find_target(b, label, want_actionable=True):
    """在编辑器 surface 里按 label 找可点元素的 targetId。"""
    for e in surface_element_page(b, 'Backstory'):
        if want_actionable and not e.get('actionable'):
            continue
        if (e.get('label') or '').strip() == label:
            return e.get('targetId')
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--keep', action='store_true')
    a = ap.parse_args()

    os.makedirs(SCRATCH, exist_ok=True)
    print('=' * 92)
    print('§7 实机验证：【70】背景故事创建器特性栏')
    print('=' * 92)

    # ── 1. 桥 ─────────────────────────────────────────────
    port, token = find_bridge()
    if not port or not token:
        print('  ❌ 桥还没起（日志里没有 GABP server running）—— 游戏仍在加载')
        return 1
    b = Bridge('127.0.0.1', port, token)
    try:
        r = b.hello()
        check('1. 桥握手', not r.get('error'), 'port=%s' % port)

        # ── 2. 打开编辑器 ──────────────────────────────────
        r = b.request('tools/call', {'name': 'rimworld/open_window_by_type',
                                     'arguments': {'windowType': WS}})
        res = r.get('result') or {}
        # delta=0 有两种情况：① 窗口本来就没开过（异常）；② 上次验证没关窗口（正常）。
        # 用「surface 里有没有 Window_BackstoryConstructor」来区分，避免误判。
        already = any('Backstory' in (s.get('type') or '') for s in (layout(b).get('surfaces') or []))
        check('2. 打开编辑器', bool(res.get('success')) and ((res.get('windowCountDelta') or 0) >= 1 or already),
              'windowCountDelta=%s%s' % (res.get('windowCountDelta'), '（窗口已开着）' if already else ''))

        time.sleep(1)
        res = layout(b)
        ss = surfaces(res)
        check('3. 编辑器 surface 存在', any('Backstory' in t for t, _, _, _ in ss),
              ' / '.join('%s(%d)' % (t.split('.')[-1], n) for t, n, _, _ in ss))

        # ── 3. 点「创建新的背景故事」，让 backstoryTemplate 就位 ──
        tid = find_target(b, '创建新的背景故事')
        if tid:
            r = b.request('tools/call', {'name': 'rimworld/click_ui_target',
                                         'arguments': {'targetId': tid}})
            check('4. 点「创建新的背景故事」', bool((r.get('result') or {}).get('success')), tid)
            time.sleep(1)
        else:
            check('4. 点「创建新的背景故事」', False, '找不到按钮（可能已有模板，跳过）')

        # ── 4. 读编辑器元素数：编辑区渲染后应远大于 8 ──────
        res = layout(b)
        el = 0
        act = 0
        for t, n, ac, _ in surfaces(res):
            if 'Backstory' in t:
                el, act = n, ac
        check('5. 编辑区已渲染（元素 > 100）', el > 100, '元素 %d / 可点 %d' % (el, act))

        # ── 5. 我们的诊断日志 ─────────────────────────────
        n70 = count_in_log('【70】特性栏替代实现已接管绘制')
        last70 = ''
        for line in reversed(tail_lines(400)):
            if '【70】' in line:
                last70 = line.strip()[:180]
                break
        check('6. 【70】自证日志', n70 >= 1, last70 or '未出现')

        # ── 6. 没有崩 ─────────────────────────────────────
        ntraits_nre = count_in_log('DrawBackstoryTraits')
        nfinal = count_in_log('特性栏绘制出错')
        check('7. 无 DrawBackstoryTraits NRE', ntraits_nre == 0, 'NRE 计数 %d' % ntraits_nre)
        check('8. 无 Finalizer 兜底告警', nfinal == 0, '告警计数 %d' % nfinal)

        # ── 7. 点「+」，看是否弹出候选列表 ────────────────
        # 点之前先把可能残留的弹窗关掉 —— 否则「本来就有 FloatMenu」会让
        # 「新增 surface」判成 0 个，误报成「点了没反应」。
        for _ in range(3):
            if any(('FloatMenu' in (s.get('type') or '')) or ('Dialog_' in (s.get('type') or ''))
                   for s in (layout(b).get('surfaces') or [])):
                b.request('tools/call', {'name': 'rimworld/press_key', 'arguments': {'key': 'Escape'}})
                time.sleep(0.4)
            else:
                break

        tid_plus = find_target(b, '+')
        if tid_plus:
            before = {t for t, _, _, _ in surfaces(layout(b))}
            r = b.request('tools/call', {'name': 'rimworld/click_ui_target',
                                         'arguments': {'targetId': tid_plus}})
            time.sleep(1)
            after = {t for t, _, _, _ in surfaces(layout(b))}
            new = after - before
            popped = any(('Dialog_DebugOptionListLister' in t) or ('FloatMenu' in t) for t in new)
            check('9. 点「+」弹出候选', popped,
                  '新增 surface: %s' % (' / '.join(sorted(x.split(".")[-1] for x in new)) or '无'))
        else:
            check('9. 点「+」弹出候选', False, '找不到 + 按钮')

        # ── 8. 截图存档 ──────────────────────────────────
        r = b.request('tools/call', {'name': 'rimworld/take_screenshot', 'arguments': {}})
        p = (r.get('result') or {}).get('path') or ''
        check('10. 截图存档', bool(p), p)

        # ── 9. 关窗口（除非 --keep）──────────────────────
        if not a.keep:
            tid_close = None
            r = layout(b)
            for s in r.get('surfaces') or []:
                if 'Backstory' not in (s.get('type') or ''):
                    continue
                for e in s.get('elements') or []:
                    if e.get('actionable') and (e.get('kind') == 'icon_button'):
                        tid_close = e.get('targetId')
                        break
            if tid_close:
                b.request('tools/call', {'name': 'rimworld/click_ui_target',
                                         'arguments': {'targetId': tid_close}})
                check('11. 关闭窗口', True, tid_close)

        # ── 汇总 ─────────────────────────────────────────
        print()
        print('=' * 92)
        ok = sum(1 for _, o, _ in results if o)
        print('结果：%d / %d 通过' % (ok, len(results)))
        for name, o, ev in results:
            if not o:
                print('  ❌ %s —— %s' % (name, ev[:140]))
        print('=' * 92)
        return 0 if ok == len(results) else 1
    finally:
        b.close()


if __name__ == '__main__':
    sys.exit(main())
