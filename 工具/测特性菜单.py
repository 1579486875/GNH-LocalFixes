# -*- coding: utf-8 -*-
"""自动走一遍背景故事编辑器：打开 → 创建模板 → 点「+」→ 读弹出的特性菜单。

## 为什么要这个

【70】修好后，点「+」弹出的 `Verse.FloatMenu` 里选项文字显示成
`类型为…` + 多行 `System.String`。这是作者 `DebugMenuOption` 的
label 拼装问题（`val4.LabelCap + " (" + val3.modContentPack.Name + ")"`），
我们只动了列表成员、没动 label 拼法。

本脚本把那个菜单的**实际元素**抓下来，确认：
  · 它到底列了几项
  · 每项的 label 原文是什么
  · 有没有可点项

## 前置

· 游戏在跑、桥已起（日志里有 `[RimBridge] GABP server running ... port`）
· 复用 `工具/桥客户端.py` 里的 Bridge 类

用法：
    python 测特性菜单.py
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '工具'))
sys.stdout.reconfigure(encoding='utf-8')

from importlib import import_module
bridge_mod = import_module('桥客户端')
Bridge = bridge_mod.Bridge
find_bridge = bridge_mod.find_bridge

WS = 'BackstoryConstructor.Window_BackstoryConstructor'
LOG = (r'C:\Users\asus\AppData\LocalLow\Ludeon Studios'
       r'\RimWorld by Ludeon Studios\Logs\Player.log')


def layout(b, offset=0):
    r = b.request('tools/call', {'name': 'rimworld/get_ui_layout', 'arguments': {'offset': offset}})
    return (r.get('result') or {})


def find_button(b, label):
    """在编辑器 surface 里按 label 找可点按钮，返回 targetId。

    编辑器元素在分页末尾，所以先拿 total，再从 total 附近往前翻几页找。
    """
    r = layout(b)
    total = (r.get('page') or {}).get('total') or 0
    # 从末尾往前翻，最多 5 页（每页约 150）
    for back in range(0, 6):
        off = max(0, total - 150 * (back + 1))
        r = layout(b, off)
        for s in r.get('surfaces') or []:
            if 'Backstory' not in (s.get('type') or ''):
                continue
            for e in s.get('elements') or []:
                if (e.get('label') or '').strip() == label:
                    return e.get('targetId')
    return None


def dump_surfaces(b):
    r = layout(b)
    out = []
    for s in r.get('surfaces') or []:
        out.append('%s(%s)' % ((s.get('type') or '?').split('.')[-1], s.get('elementCount')))
    return '  surface: ' + ' / '.join(out)


def main():
    port, token = find_bridge()
    if not port or not token:
        print('❌ 桥没起')
        return 1

    b = Bridge('127.0.0.1', port, token)
    try:
        b.hello()
        print('✅ 已连上桥')

        # ① 打开编辑器
        r = b.request('tools/call', {'name': 'rimworld/open_window_by_type',
                                     'arguments': {'windowType': WS}})
        print('① 打开编辑器: %s' % (r.get('result') or {}).get('success'))
        print(dump_surfaces(b))

        # ② 点「创建新的背景故事」
        tid = find_button(b, '创建新的背景故事')
        print('\n② 找到「创建新的背景故事」: %s' % tid)
        if tid:
            r = b.request('tools/call', {'name': 'rimworld/click_ui_target',
                                         'arguments': {'targetId': tid}})
            print('   点击结果: %s  %s' % ((r.get('result') or {}).get('success'),
                                          (r.get('result') or {}).get('message', '')[:60]))
        print(dump_surfaces(b))

        # ③ 点「+」
        tid2 = find_button(b, '+')
        print('\n③ 找到「+」: %s' % tid2)
        if not tid2:
            print('   ❌ 找不到 + 按钮')
            return 1
        r = b.request('tools/call', {'name': 'rimworld/click_ui_target',
                                     'arguments': {'targetId': tid2}})
        print('   点击结果: %s' % ((r.get('result') or {}).get('success')))
        print(dump_surfaces(b))

        # ④ 读 FloatMenu 的全部元素
        print('\n④ 弹窗内容')
        r = layout(b)
        total = (r.get('page') or {}).get('total') or 0
        fm = None
        for s in r.get('surfaces') or []:
            if 'FloatMenu' in (s.get('type') or ''):
                fm = s
        if not fm:
            print('   ⚠ 没看到 FloatMenu（可能已经关了）')
            return 0
        print('   %s  元素 %s' % (fm.get('type'), fm.get('elementCount')))
        # FloatMenu 元素在末尾，往前翻
        found = []
        for back in range(0, 4):
            off = max(0, total - 100 * (back + 1))
            rr = layout(b, off)
            for s in rr.get('surfaces') or []:
                if 'FloatMenu' not in (s.get('type') or ''):
                    continue
                for e in s.get('elements') or []:
                    found.append(e)
        print('   本页读到 %d 个元素' % len(found))
        for e in found[:20]:
            print('   · %-24s act=%-5s %-12s label=%r' % (
                e.get('targetId'), e.get('actionable'),
                (e.get('kind') or '')[:12], str(e.get('label') or '')[:60]))

        # ⑤ 顺便看一眼最近日志
        print('\n⑤ 最近的【70】日志')
        try:
            with open(LOG, encoding='utf-8', errors='ignore') as f:
                lines = [l for l in f if 'GNH LocalFixes' in l and ('【70】' in l or '特性' in l)]
            for l in lines[-3:]:
                print('   ' + l.strip()[:200])
        except Exception as e:
            print('   读日志失败: %s' % e)
        return 0
    finally:
        b.close()


if __name__ == '__main__':
    sys.exit(main())
