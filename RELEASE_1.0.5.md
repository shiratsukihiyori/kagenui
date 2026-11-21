# Kagenui 影縫い v1.0.5

**修的是 v1.0.4 没修干净的那个 bug：运行中添加进程仍然不生效。**

## 上一版为什么没修好

v1.0.4 把目标列表改成运行期可变，UI 改列表就同步给 Redirector —— 机制对了，但**名字对不上**：

```
UI 存的      chrome.exe      （选择器把 .exe 一起存进 ListBox）
启动时       chrome            （Start() 里做了 n[..^4] 去扩展名）
运行期同步   chrome.exe        （SyncTargets 忘了这一步）
```

于是 `liveNames` 里躺着 `chrome.exe`，而 `IsTarget` 拿到的 `Process.ProcessName` 是 `chrome`——
比较永远失败，`Process.GetProcessesByName("chrome.exe")` 也返回空。

**启动时加的目标能用，运行中加的一律无效**——正好是你遇到的现象。

## 这一版

把去扩展名抽成一个函数，两条路径都走它：

```csharp
private static string NormalizeName(string n)
{
    n = n.Trim();
    return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
}

// Start() 与 SyncTargets() 都调用同一个
```

## 验证（用带 .exe 的名字加，完全复刻 UI 的行为）

```
12:29:07.917  pass  202.89.233.100:443  pid=1348    ← 加入前，curl 放行
12:29:11.387  目标列表已更新: curl                  ← 收到的是 "curl.exe"，规范化为 curl
12:29:13.199  + 202.89.233.100:443  pid=11600      ← 加入后，被接管
12:29:13.239  > www.bing.com:443 隧道建立
12:29:14.546  < www.bing.com:443 存活 1.3s  ↑201B ↓88KB
```

## 校验

```
sha256  : 21a6b535726d415cbaac46cabfd850d8ec0eca1471aec0e57bca32b4d855d804
签名    : Valid（cs5 + DigiCert 时间戳）
sequence: 6
```

MIT © 2026 白月日和
