# Kagenui 影縫い v1.0.4

修一个用起来很别扭的 bug：**运行中新加的目标进程不生效，必须停止再启动。**

## 原因

`Start()` 里把 UI 的进程列表拷成了一个不可变的 `string[] targetNames`。
之后在界面里加进程只是往 ListBox 里塞了一行，Redirector 手里的那份快照没变，
于是新名字永远匹配不上，只能停掉重开。

## 修法

把目标名改成运行期可变的集合，并让三个消费者都读它：

```csharp
private static readonly HashSet<string> liveNames = new(StringComparer.OrdinalIgnoreCase);

// PidWatcher（每 400ms 扫 PID）
lock (pidLock) names = liveNames.ToArray();

// 连接匹配
lock (pidLock) hit = liveNames.Contains(name);

// UI 改动列表 → 立刻同步
public static void SyncTargets(IEnumerable<string> names)
```

界面上的 **添加… / 删除 / 清空** 三个入口都会调用 `SyncTargets`，正在运行也即时生效。

## 验证（A/B 实测）

同一个会话里，先拦一个不存在的进程名，6 秒后把 `curl` 加进目标：

```
12:10:02.181  pass  202.89.233.100:443  pid=12336   ← 加入前，curl 直连放行
12:10:06.330  目标列表已更新: curl                  ← SyncTargets 生效
12:10:07.484  + 202.89.233.100:443  pid=7768       ← 加入后，同一个 curl 被接管
12:10:07.556  > www.bing.com:443 隧道建立
12:10:08.922  < www.bing.com:443 存活 1.4s  ↑201B ↓85KB
```

## 校验

```
sha256  : 7a304037de73f37576c695949dc5873747242a479a07662c2644de3a05643824
签名    : Valid（cs5 + DigiCert 时间戳）
sequence: 5
```

MIT © 2026 白月日和
