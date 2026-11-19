# 安全模型

## 威胁模型

kagenui 以管理员权限运行，并在内核层使用 WinDivert 转发指定进程的流量。
一旦程序本体被替换，攻击者即获得管理员代码执行。因此分发链的完整性是首要目标。

## 本地组件

| 组件 | 位置 | 保护 |
|---|---|---|
| 内嵌 WinDivert.dll / .sys | 运行时释放到 `%ProgramData%\kagenui\engine` | 目录 ACL 锁定为 SYSTEM + Administrators；任何普通用户可写即拒绝运行 |
| 文件完整性 | 同上 | 每次加载前比对官方 SHA-256（`c1e060ee…` / `8da08533…`），不符即拒绝 |
| 中继监听 | `0.0.0.0:38999`(TCP) / `38998`(UDP) | 启动时自动加防火墙入站阻断；进程内校验来源必须是本机地址且流表存在 |
| 日志 | `%LOCALAPPDATA%\kagenui\run.log` | 含 SNI 与连接元数据，不含载荷内容 |

中继必须监听 `0.0.0.0`：目标地址在 WinDivert 层被改写成客户端自身 IP，
绑定 `127.0.0.1` 将收不到任何包。局域网可达性由防火墙规则阻断。

## 发布链

```
离线私钥 (Ed25519) → release.json + .sig → Cloudflare KV → release.hiyori.xx.kg
                 ↘ Authenticode (cs5, 自建 CA) → kagenui.exe
```

- 签名在本地完成，服务器上没有任何私钥
- 清单包含 `sequence`（单调递增）与 `expiresAt`（180 天），用于防降级与防冻结
- 客户端应**内置**公钥，不得使用响应中携带的公钥验签
- `/release/key` 仅供带外比对指纹，不可用于验证同一响应的签名

## 报告问题

security.txt: https://release.hiyori.xx.kg/.well-known/security.txt
邮箱：xiaxiqiuiri@gmail.com
