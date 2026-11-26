# -*- coding: utf-8 -*-
"""验证【70】替代实现的布局高度与原版 GenUI.DrawElementStack 完全等价。

## 为什么要这个

`DrawBackstoryTraits` 最后一句是：

    Rect val5 = GenUI.DrawElementStack<string>(
        new Rect(labelRect.x, height, labelRect.width, 0f), 25f, first, drawer,
        (string text2) => (labelRect.width - 10f) / 2f,   // widthGetter
        5f,    // rowMargin
        10f,   // elementMargin
        false);// allowOrderOptimization
    height += val5.height + 15f;

`height` 是 `ref` 传播的，**这一栏多算或少算，后面「随身物品」整栏就会上下错位**。
我们的替代实现自己排两列，所以必须逐项证明高度算得一模一样。

## 原版算法（反编译 Verse.GenUI.DrawElementStack，1.7.5014）

    tmpRects.Clear(); tmpRects2.Clear();
    for (i = 0; i < elements.Count; i++)
        tmpRects.Add(new StackedElementRect(new Rect(0,0,widthGetter(elements[i]), rowHeight), i));
    int num = Mathf.FloorToInt(rect.height / rowHeight);
    List<...> list = tmpRects;
    if (allowOrderOptimization) { ... }      // ← 我们传 false，跳过
    num2 = num3 = 0f;
    while (list.Count > 0)
    {
        var e = list[0];
        if (num2 + e.rect.width > rect.width)   // ← 装不下就换行
        {
            num2 = 0f;
            num3 += rowHeight + rowMargin;
        }
        drawer(new Rect(rect.x + num2, rect.y + num3, e.rect.width, e.rect.height), elements[e.elementIndex]);
        num2 += e.rect.width + elementMargin;
        list.RemoveAt(0);
    }
    return new Rect(rect.x, rect.y, rect.width, num3 + rowHeight);   // ← 注意 num3+rowHeight

**关键**：`num3` 只累计「换行」量，最后还要 `+ rowHeight` ——
所以**空列表也返回一个 rowHeight（25）**，这一点最容易漏。

**只读，不改任何文件。**
"""
import sys

sys.stdout.reconfigure(encoding='utf-8')

ROW_H = 25.0
ROW_MARGIN = 5.0
ELEM_MARGIN = 10.0
TAIL_GAP = 15.0


def vanilla_height(width, count):
    """复刻 GenUI.DrawElementStack(..., allowOrderOptimization=false) 返回的 height。"""
    if count == 0:
        return 0.0 + ROW_H            # num3 = 0 → 返回 0 + 25
    w = (width - 10.0) / 2.0          # widthGetter
    widths = [w] * count
    num2 = 0.0
    num3 = 0.0
    for i in range(count):
        if num2 + widths[i] > width:  # 装不下 → 换行
            num2 = 0.0
            num3 += ROW_H + ROW_MARGIN
        num2 += widths[i] + ELEM_MARGIN
    return num3 + ROW_H


def our_height(width, count):
    """复刻我们替代实现里的算法（rows = ceil(count/2)，最小 1）。"""
    rows = (count + 1) // 2
    if rows < 1:
        rows = 1
    return (rows - 1) * (ROW_H + ROW_MARGIN) + ROW_H


def main():
    widths = [200.0, 400.0, 640.0, 739.0, 1000.0]
    counts = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 20, 37, 100]
    print('=' * 92)
    print('【70】布局高度对照：原版 GenUI.DrawElementStack  vs  替代实现')
    print('=' * 92)
    print('参数：rowHeight=%.0f  rowMargin=%.0f  elementMargin=%.0f  tailGap=%.0f'
          % (ROW_H, ROW_MARGIN, ELEM_MARGIN, TAIL_GAP))
    print()

    total = 0
    bad = 0
    for width in widths:
        print('── 容器宽 %.0f（列宽 %.1f）──' % (width, (width - 10.0) / 2.0))
        print('  %-8s %-12s %-12s %-12s %s' % ('项数', '原版 stack', '替代 stack', '原版 height+', '一致'))
        for c in counts:
            v = vanilla_height(width, c)
            o = our_height(width, c)
            total += 1
            same = abs(v - o) < 1e-6
            if not same:
                bad += 1
            # height 的实际增量 = stack + tailGap
            print('  %-8d %-12.1f %-12.1f %-12.1f %s'
                  % (c, v, o, v + TAIL_GAP, '✅' if same else '❌ 差 %.1f' % (o - v)))
        print()

    print('=' * 92)
    print('结论：%d 组对照，不一致 %d 组' % (total, bad))
    if bad == 0:
        print('  ✅ 替代实现的高度累加与原版逐项一致 —— 后面栏位不会错位')
        print()
        print('  最终 height 增量的规律（可直接眼验）：')
        for c in [0, 1, 2, 3, 4, 5]:
            print('    %d 项 → +%.0f' % (c, vanilla_height(739.0, c) + TAIL_GAP))
    else:
        print('  ❌ 有不一致，需要修替代实现的 rows 计算')
    print('=' * 92)
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
