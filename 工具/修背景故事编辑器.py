# -*- coding: utf-8 -*-
"""修复「背景故事创建器」编辑器一打开就报空引用的问题。

## 根因（反编译 BackstoryConstructor.dll 1.7 版实证）

    // BackstoryConstructor.Window_BackstoryConstructor
    private void DrawBackstoryDisallowedTraits(ref float height, float width)
    {
        DrawBackstoryTraits(ref height, width, "BackstoryConstructor.DisallowedTraits",
                            backstoryTemplate.disallowedTraits,   // ← 可能是 null
                            backstoryTemplate.forcedTraits);      // ← 可能是 null
    }

    private void DrawBackstoryTraits(..., List<string> first, List<string> second)
    {
        ...
        if (!first.Contains(traitInfo) && !second.Contains(traitInfo))   // ← 不判空
        ...

    // BackstoryTemplate
    public BackstoryTemplate() { }                      // ← 构造函数是空的
    public void ExposeData()
    {
        ...
        Scribe_Collections.Look<string>(ref forcedTraits,     "forcedTraits",     LookMode.Value, Array.Empty<object>());
        Scribe_Collections.Look<string>(ref disallowedTraits, "disallowedTraits", LookMode.Value, Array.Empty<object>());
    }

关键点：`Scribe_Collections.Look` 的第 4 个参数是 `params object[] ctorArgs`，
**不是默认值**。所以配置文件里若没有这个键，字段就保持 null（构造函数没初始化）。

`Utility.MakeBackstoryTemplate()`（「新建」按钮走的路径）会初始化这两个 List，
所以**新建的模板是安全的**；凡是从旧配置读进来、或由其它模组直接 new 出来的模板，
这两个字段就都是 null。

实测本机配置：`Mod_2907131508_BackstoryConstructorMod.xml` 里
**1,074 个模板，`forcedTraits` 与 `disallowedTraits` 一个都没有** → 全部 null。

## 这个脚本做什么

给配置文件里**每个** `<li>` 模板块补齐两个空节点：

    <forcedTraits />
    <disallowedTraits />

`Scribe_Collections.Look` 读到空元素会创建空 `List<string>()`，null 问题就消失了。

## 用法

**必须先完全退出游戏**（否则游戏写盘会用内存里的旧数据覆盖掉）

    python 修背景故事编辑器.py            # 只检查，不改
    python 修背景故事编辑器.py --写入      # 真正写盘（自动先备份）

写盘前会在同目录留一份 `.bak-<时间戳>` 备份。
"""
import os
import re
import sys
import shutil
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8')

CONFIG = (r'C:\Users\asus\AppData\LocalLow\Ludeon Studios'
          r'\RimWorld by Ludeon Studios\Config'
          r'\Mod_2907131508_BackstoryConstructorMod.xml')

# 这两个字段缺了就补；顺序按 ExposeData 里的写法（forced 先、disallowed 后）
FIELDS = ['forcedTraits', 'disallowedTraits']


def game_running():
    """用 tasklist 判断游戏是否在跑。"""
    try:
        import subprocess
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq RimWorld.exe'],
                             capture_output=True, text=True, timeout=10)
        return 'RimWorld.exe' in (out.stdout or '')
    except Exception:
        return False


def main():
    write = '--写入' in sys.argv or '--write' in sys.argv

    if not os.path.exists(CONFIG):
        print('❌ 找不到配置文件：')
        print('   %s' % CONFIG)
        print('   （如果没装这个模组，或还没在游戏里存过设置，就不会有这个文件）')
        return 1

    if game_running():
        print('❌ 游戏正在运行 —— 现在改会被游戏写盘覆盖掉。')
        print('   请先完全退出 RimWorld，再跑这个脚本。')
        return 1

    raw = open(CONFIG, 'rb').read()
    had_bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig')
    orig_len = len(text)

    # 拆出每个模板块：<backstoryTemplates> 下的顶层 <li>…</li>
    start = text.find('<backstoryTemplates>')
    if start < 0:
        print('❌ 配置文件里没有 <backstoryTemplates>，结构不符，未改动。')
        return 1
    end = text.find('</backstoryTemplates>', start)
    if end < 0:
        print('❌ <backstoryTemplates> 没有闭合，未改动。')
        return 1

    head = text[:start + len('<backstoryTemplates>')]
    body = text[start + len('<backstoryTemplates>'):end]
    tail = text[end:]

    def block_end(s, start):
        """从 s[start:] 的 <li> 起，按字符级深度扫描找配对的 </li> 结束位置（不含）。

        不能直接用 s.find('</li>')：模板内部到处是嵌套的 <li>（skillGains 的
        keys/values 全是），第一个 </li> 是内层的，会把整块截断。
        """
        depth = 0
        i = start
        while i < len(s):
            nxt_open = s.find('<li>', i)
            nxt_close = s.find('</li>', i)
            if nxt_close < 0:
                return len(s)
            if nxt_open >= 0 and nxt_open < nxt_close:
                depth += 1
                i = nxt_open + 4
            else:
                depth -= 1
                i = nxt_close + 5
                if depth <= 0:
                    return i
        return len(s)

    # 按 <defName> 定位每个模板块起点（模板块一定含 defName）
    blocks = list(re.finditer(r'<li>\s*<defName>', body))
    print('配置里共 %d 个模板块' % len(blocks))

    # 逐块检查
    inserts = []
    for m in blocks:
        bstart = m.start()
        bend = block_end(body, bstart)
        seg = body[bstart:bend]
        # 只认"空元素"或"成对节点"，两者都算已存在
        def has(f):
            return ('<%s>' % f) in seg or ('<%s />' % f) in seg or ('<%s/>' % f) in seg
        missing = [f for f in FIELDS if not has(f)]
        if missing:
            inserts.append((bstart, bend, seg, missing))

    if not inserts:
        print('✅ 全部模板已有这两个字段，无需修改。')
        return 0

    print('需要补字段的模板：%d 个' % len(inserts))
    for bstart, bend, seg, missing in inserts[:3]:
        dn = re.search(r'<defName>(.*?)</defName>', seg)
        print('   %-28s 缺 %s' % ((dn.group(1) if dn else '?')[:28], '、'.join(missing)))
    if len(inserts) > 3:
        print('   …另有 %d 个' % (len(inserts) - 3))

    if not write:
        print()
        print('这是只检查模式。确认无误后加 --写入 参数真正改盘：')
        print('   python 修背景故事编辑器.py --写入')
        return 0

    # 备份
    stamp = datetime.now().strftime('%Y%m%d-%H%M%S')
    bak = CONFIG + '.bak-' + stamp
    shutil.copy2(CONFIG, bak)
    print('\n已备份 → %s' % os.path.basename(bak))

    # 从后往前插，保持前面块的偏移不变
    new_body = body
    for bstart, bend, seg, missing in reversed(inserts):
        # 找块内最后一个 </li> 之前的缩进层级：用 defName 那行的缩进 + 1 tab
        m = re.search(r'\r?\n(\t+)<defName>', seg)
        indent = (m.group(1) + '\t') if m else '\t\t\t\t'
        nl = '\r\n' if '\r\n' in seg else '\n'
        add = ''.join('%s<%s />%s' % (indent, f, nl) for f in missing)
        # 插在块尾 </li> 之前
        pos = seg.rfind('</li>')
        if pos < 0:
            continue
        new_seg = seg[:pos] + add + seg[pos:]
        new_body = new_body[:bstart] + new_seg + new_body[bend:]

    out = head + new_body + tail
    data = out.encode('utf-8')
    if had_bom:
        data = b'\xef\xbb\xbf' + data
    open(CONFIG, 'wb').write(data)

    # 校验：能解析、字段齐、其他内容没动
    import xml.etree.ElementTree as ET
    try:
        root = ET.fromstring(out)
    except Exception as e:
        open(CONFIG, 'wb').write(raw)          # 回滚
        print('❌ 写入后 XML 解析失败，已回滚：%s' % e)
        return 1

    # 逐项核对：字段数应等于 模板数 × 2
    n_f = out.count('<forcedTraits />')
    n_d = out.count('<disallowedTraits />')
    print('写入完成：%d B → %d B' % (orig_len, len(out)))
    print('  <forcedTraits />     %d 个' % n_f)
    print('  <disallowedTraits /> %d 个' % n_d)
    print()
    print('✅ 现在可以启动游戏，打开「背景故事创建器」——「禁用特性」与「绑定特性」应当能点开了。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
