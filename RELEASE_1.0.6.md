# Kagenui 影縫い v1.0.6

补上 1.0.5 剩下的那半个问题：**已经在跑的连接，事后加进目标不会换道。**

## 之前为什么切不过去

包过滤器只在**新连接的第一个 SYN** 上做决定。一条连接已经建立、数据还在流，
它的包属于老流程，加进目标列表也追不回来——除非让程序重连。

## 这一版

往列表里加进程时，顺手把它**当前已建立的 TCP 连接全部断掉**：

```csharp
// SetTcpEntry(MIB_TCP_STATE_DELETE_TCB) —— 按四元组删除内核里的 TCB
var del = new Native.MibTcpRow {
    State = 12,
    LocalAddr = row.LocalAddr,   LocalPort  = row.LocalPort,
    RemoteAddr = row.RemoteAddr, RemotePort = row.RemotePort,
};
Native.SetTcpEntry(ref del);
```

程序自己会立刻重连，重连的 SYN 就按新列表被拦下。

**只重置「新增」的目标**：算差集 `added = 新列表 - 旧列表`，已经在列表里的目标不受影响。

## 实测

```
目标列表已更新: powershell
已重置 1 条旧连接，powershell 会重连后进入代理
→ 客户端随即收到 "An existing connection was forcibly closed by the remote host"
```

## 行为提醒

这个重置**不分青红皂白**。把一个正在下载大文件的程序加进目标，它会当场断掉、下载失败。
这是有意为之——不断就切不过去。加目标之前心里有数就行。

## 校验

```
sha256  : 6c4709f115d2f038e471f00c419a7c104a2c9c42d0b9b85e81ae1cde5746facd
签名    : Valid（cs5 + DigiCert 时间戳）
sequence: 7
```

MIT © 2026 白月日和
