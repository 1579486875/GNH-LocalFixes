# -*- coding: utf-8 -*-
"""通过 RimBridgeServer 的 GABP 协议连进正在运行的 RimWorld，列出它注册的工具。

## 为什么要这个

【70】背景故事创建器那个崩溃，修了四版都没能实机验证——
因为「打开编辑器 / 点 +」只能靠人手操作，而我在命令行这边看不到。
RimBridgeServer（`brrainz.rimbridgeserver`）在游戏里跑了一个 TCP 服务，
只要它能执行代码，我就能自己触发那个窗口。

## 协议（反编译 Lib.GAB 得到的，不是猜的）

帧格式（`TcpConnection.SendMessageAsync`）：

    Content-Length: <UTF-8 字节数>\r\n
    Content-Type: application/json\r\n
    \r\n
    <JSON 体>

报文（`GabpMessage` 及其子类，Newtonsoft.Json 序列化）：

    请求  { "v":"gabp/1", "id":"<guid>", "type":"request",
            "method":"session/hello", "params":{...} }
    响应  { "v":"gabp/1", "id":"<guid>", "type":"response",
            "result":{...}, "error":{...} }

方法（`GabpServer.HandleRequestAsync` 里的 switch）：

    session/hello        —— 必须先发，params.Token 要匹配桥的 token
    tools/list           —— 列出注册的工具
    tools/call           —— 调某个工具，params = { name, arguments }
    events/subscribe / events/unsubscribe
    attention/current / attention/ack

## 桥的端口与 token 从哪来

游戏日志里有两行：

    [RimBridge] GABP server running standalone on port 5174
    [RimBridge] Bridge token: abc123...

**桥只在主菜单之后才起**（要等满模组加载完）。

## 用法

    python 桥客户端.py                    # 列工具
    python 桥客户端.py --call <工具名> --args '{"k":"v"}'
"""
import argparse
import json
import re
import socket
import sys
import uuid

sys.stdout.reconfigure(encoding='utf-8')

LOG = (r'C:\Users\asus\AppData\LocalLow\Ludeon Studios'
       r'\RimWorld by Ludeon Studios\Logs\Player.log')


def find_bridge():
    """从日志里读端口与 token（取最后一次出现的，避免读到旧会话）。"""
    try:
        with open(LOG, 'r', encoding='utf-8', errors='ignore') as f:
            text = f.read()
    except Exception as e:
        print('读不到日志：%s' % e)
        return None, None

    ports = re.findall(r'\[RimBridge\][^\n]*?port\s+(\d{2,5})', text, re.I)
    tokens = re.findall(r'\[RimBridge\][^\n]*?[Tt]oken:\s*([A-Za-z0-9_\-]+)', text)

    port = int(ports[-1]) if ports else None
    token = tokens[-1] if tokens else None
    return port, token


class Bridge:
    def __init__(self, host, port, token, timeout=10.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.sock.settimeout(timeout)
        self.token = token
        self.buf = b''

    def _send(self, obj):
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        head = ('Content-Length: %d\r\nContent-Type: application/json\r\n\r\n'
                % len(body)).encode('utf-8')
        self.sock.sendall(head + body)

    def _recv_one(self):
        """读一帧。先找 \r\n\r\n，再按 Content-Length 读体。"""
        while True:
            sep = self.buf.find(b'\r\n\r\n')
            if sep >= 0:
                header = self.buf[:sep].decode('utf-8', errors='ignore')
                m = re.search(r'Content-Length:\s*(\d+)', header, re.I)
                if not m:
                    raise RuntimeError('帧头里没有 Content-Length：%r' % header[:200])
                need = int(m.group(1))
                start = sep + 4
                if len(self.buf) >= start + need:
                    body = self.buf[start:start + need]
                    self.buf = self.buf[start + need:]
                    return json.loads(body.decode('utf-8'))
            chunk = self.sock.recv(65536)
            if not chunk:
                raise RuntimeError('连接被对端关闭')
            self.buf += chunk

    def request(self, method, params=None):
        rid = str(uuid.uuid4())
        msg = {'v': 'gabp/1', 'id': rid, 'type': 'request', 'method': method}
        if params is not None:
            msg['params'] = params
        self._send(msg)
        # 跳过事件推送，直到拿到同 id 的 response
        for _ in range(50):
            resp = self._recv_one()
            if resp.get('type') == 'response' and resp.get('id') == rid:
                return resp
        raise RuntimeError('等不到 %s 的响应' % method)

    def hello(self):
        return self.request('session/hello', {
            'Token': self.token,
            'BridgeVersion': '1.0',
            'Platform': 'windows',
            'LaunchId': str(uuid.uuid4()),
        })

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--call', default=None, help='要调用的工具名')
    ap.add_argument('--args', default='{}', help='工具参数（JSON）')
    a = ap.parse_args()

    port, token = find_bridge()
    print('桥端口: %s' % port)
    print('桥 token: %s' % ('（已读到 %d 字符）' % len(token) if token else '（没读到）'))
    print()

    if not port or not token:
        print('❌ 日志里还没有桥的启动行。')
        print('   [RimBridge] GABP server running standalone on port ...')
        print('   [RimBridge] Bridge token: ...')
        print('   这两行只在**主菜单之后**才出现 —— 满模组加载大约要 60 分钟。')
        return 1

    b = Bridge('127.0.0.1', port, token)
    try:
        r = b.hello()
        if r.get('error'):
            print('❌ 握手失败：%s' % r['error'])
            return 1
        res = r.get('result') or {}
        print('✅ 握手成功')
        print('   应用: %s %s' % (res.get('App', {}).get('Name'), res.get('App', {}).get('Version')))
        caps = res.get('Capabilities') or {}
        print('   可用方法: %s' % caps.get('Methods'))
        print('   可用事件: %s' % (caps.get('Events') or [])[:6])
        print()

        if a.call:
            args = json.loads(a.args)
            r2 = b.request('tools/call', {'name': a.call, 'arguments': args})
            print('=== tools/call %s ===' % a.call)
            print(json.dumps(r2, ensure_ascii=False, indent=2)[:4000])
        else:
            r2 = b.request('tools/list', {})
            tools = ((r2.get('result') or {}).get('tools')
                     or (r2.get('result') or {}).get('Tools') or [])
            print('=== 注册的工具（%d 个）===' % len(tools))
            for t in tools:
                name = t.get('name') or t.get('Name')
                desc = (t.get('description') or t.get('Description') or '')[:90]
                print('  · %-32s %s' % (name, desc))
        return 0
    finally:
        b.close()


if __name__ == '__main__':
    sys.exit(main())
