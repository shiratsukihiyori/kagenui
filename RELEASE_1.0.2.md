# Kagenui 影縫い v1.0.2

**启动即弹 UAC —— 因为程序本来就需要管理员权限，旧版没在清单里声明。**

## 问题

旧版 `app.manifest` 是：

```xml
<requestedExecutionLevel level="asInvoker" uiAccess="false" />
```

`asInvoker` 意味着**不请求提权**。后果：

1. 双击运行 → 进程是普通权限 → `WinDivertOpen` 失败 → 界面报"需要管理员权限"
2. 必须右键「以管理员身份运行」才能用
3. 因为不弹 UAC，**根本不会出现发布者名字那一栏**——所以有人会以为"没有证书问题"

## 修复

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

现在双击就会弹 UAC，自动以管理员启动。

## 关于「未知发布者」

程序用的是**自建 CA**（Hiyori Code Signing Root CA），根证书只装在本机。
所以在别人的机器上，UAC 弹窗里会显示「未知发布者」——**这是预期的，不是错误**。

要变成显示「白月日和」，只能买公共 CA 的代码签名证书（OV/EV）。
不想看提示又想确认来源，用哈希或签名校验：

```bash
node verify-exe.mjs kagenui.exe
```

## 校验

```
sha256 : 2494c50ef16b9dbdb57d3f760ccf810803fcf0481bb48b2336df58b7394ed656
签名   : 验证通过 ✅ 来自 白月日和 的签名
```

MIT © 2026 白月日和
