# Kagenui 影縫い v1.0.3

安全审计后的加固版。客户端三处硬改，服务端同步收紧。

## 客户端

**① 引擎释放目录搬离用户目录**

| | 旧 | 新 |
|---|---|---|
| 路径 | `%LOCALAPPDATA%\kagenui\engine` | `C:\ProgramData\kagenui\engine` |
| ACL | 当前用户可写 | 仅 SYSTEM + Administrators |

提权进程从"用户可写目录"加载 DLL 是一条现成的本地提权链。现在目录建好后会自查
ACL，任何普通用户组仍可写就**直接拒绝运行**，宁可不跑也不留洞。

**② 加载前二次校验**

```csharp
if (Sha256File(DllPath) != DllSha256) → 拒绝加载
NativeLibrary.Load(DllPath);   // 校验完立即加载
```

Ctrl+C 掉"校验通过"和"真正加载"之间那段可以被替换的窗口。

**③ 防火墙入站阻断自动管理**

中继必须监听 `0.0.0.0`（目标地址被改写成客户端自身 IP，绑 Loopback 收不到包），
所以改用防火墙挡入站：

```
启动 → 已加防火墙入站阻断 TCP/38999
        已加防火墙入站阻断 UDP/38998
停止 → 已移除
```

## 服务端

- `GET/HEAD` 之外一律 **405**（原来 POST/PUT/DELETE 都能拿到完整清单）
- 404 **不再回显输入**（原来 `?v=<script>` 会原样吐回来）
- 版本参数严格限形 `/^[0-9A-Za-z.\-]{1,24}$/`
- 只返回**一份**被签名的表示（`manifestRaw` + `signature`），删掉重复的 `manifest` 对象
- 清单加入 `sequence`（单调递增，防降级）
- 新清单**不再内嵌公钥**，改由 `/release/key` 单独提供，并标注 `usage: pin-out-of-band-only`
- 新增 `/.well-known/security.txt`

## 校验

```
sha256 : bfd7e9acc19fa9a0cfc22472ad04ebc4af3fb1d16ae74ff8b1c3c6933a246c54
签名   : Valid（cs5 + DigiCert 时间戳）
sequence: 4
```

MIT © 2026 白月日和
