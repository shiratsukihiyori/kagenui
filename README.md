# Kagenui 影縫い

> 影を縫う — 把指定进程的连线，一针一针缝进代理里。


[![by 白月日和](docs/hiyori-badge.svg)](https://www.hiyori.xx.kg/)

把**指定进程**的 TCP／UDP 流量转发进本机 HTTP/SOCKS5 代理。

不建 TUN、不改系统路由、不碰 DNS、其它进程完全不受影响。

## 原理

```
目标进程 connect()
      │  SYN 出站
      ▼
WinDivert 截获（outbound, !loopback, tcp）
      │  查 TCP 表拿 OwningPid
      ├─ 不是目标 → 原样放行
      └─ 是目标   → 改写到 127.0.0.1:38999，重新注入
      ▼
本地中继 accept（对端端口 = 客户端原端口）
      │  读首段字节，嗅探 TLS SNI / HTTP Host
      ▼
CONNECT <域名>:443 → 上游 HTTP 代理
      ▼
双向泵数据；回程包被第二个 WinDivert 句柄改回原目标再注入
```

UDP 走另一条通道（QUIC / DNS 也能过）：

```
目标进程 sendto() → WinDivert 截获（outbound, udp）
      → 记录「客户端端口 → 原目标」→ 改写到本机 38998
      → 封装成 SOCKS5 UDP 报文 → 上游 SOCKS5
      → 回程改回「原目标 → 客户端」→ 注入
```

## 为什么嗅探 SNI

代理（xray / clash）多数按**域名**分流。只拿 IP 去 CONNECT，规则匹配不到域名会走默认出口，
表现就是 HTTP 通、HTTPS 不通。所以中继先读客户端首段字节：

| 协议 | 提取 |
|---|---|
| TLS | ClientHello 的 `server_name` 扩展 |
| HTTP | `Host:` 头 |

## 界面

- **添加…** 打开进程选择器，列出 **Alt+Tab 里可见的窗口进程**（进程名 / PID / 窗口标题）
- 支持多选、按名称或标题筛选、手动输入、勾选后台进程
- 列表支持删除 / 清空
- 启动前可先 **校验** 代理端口连通性

## 构建

需要 .NET 8 SDK（Windows x64）。

```powershell
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

产物：`bin/Release/net8.0-windows/win-x64/publish/kagenui.exe`（约 63 MB，单文件，含运行时与 WinDivert）

## 运行要求

| 项 | 说明 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| 权限 | 管理员（加载 WinDivert 驱动） |
| 上游代理 | HTTP CONNECT（TCP）+ SOCKS5 UDP ASSOCIATE（UDP）；xray / clash 混合端口默认两者都支持 |

## 日志

```
+ 202.89.233.100:443  pid=18164  sport=8949      ← 抓到一条目标进程的连接
  SNI www.bing.com                              ← 从 ClientHello 取到域名
> www.bing.com:443 隧道建立                      ← CONNECT 成功
< www.bing.com:443 存活 2.0s  ↑80B ↓6KB          ← 关闭时的时长与流量
pass  172.217.112.4:443  pid=9020               ← 非目标，原样放行
```

- `↑` 上行（客户端→服务器）、`↓` 下行
- **存活 <1s 且 ↓0B** 的连接 = 对端拒绝或网络不通，连续出现即重连风暴
- `pid=0` = 没查到归属进程，已放行（该连接未进代理）

## 限制

| 项 | 说明 |
|---|---|
| IPv6 | 不处理，直连 |
| ICMP | 不处理 |
| 32 位目标 | 仅 x64 |
| 代理不支持 SOCKS5 UDP | UDP 退化为直连 |
| ECH | 开了加密客户端问候的站点取不到域名，退回按 IP 连接 |

## 第三方组件

内嵌 WinDivert 2.2.2（LGPLv3 / GPLv2 双许可），许可文本见 [third_party/WinDivert-LICENSE.txt](third_party/WinDivert-LICENSE.txt)，
其余说明见 [NOTICE](NOTICE)。

## 许可

MIT © 2026 白月日和
