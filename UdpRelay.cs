using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Kagenui;

/// <summary>
/// UDP 隧道：把被拦截的 UDP 数据报通过 SOCKS5 UDP ASSOCIATE 送进上游代理。
/// 上游代理（xray/clash 的混合端口）同时支持 SOCKS5 与 HTTP，所以 QUIC/DNS 都能走。
/// </summary>
internal static class UdpRelay
{
    public const int LocalPort = 38998;
    /// <summary>DNS 专用端口：这条流优先走 TCP（见 DnsTcp）。</summary>
    private const ushort DnsPort = 53;

    public sealed class Flow
    {
        public IPAddress RemoteIp = IPAddress.None;
        public ushort RemotePort;
        public long LastSeen;
    }

    /// <summary>客户端端点 "ip:port" → 原始目标</summary>
    public static readonly ConcurrentDictionary<string, Flow> Flows = new();
    /// <summary>远端 "ip:port" → 可能用到它的客户端集合（同一远端多个客户端时逐个投递）</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Reverse = new();

    private static TcpClient? _ctrl;
    private static NetworkStream? _ctrlStream;
    private static UdpClient? _local;
    private static UdpClient? _socks;
    private static IPEndPoint? _socksRelay;
    private static volatile bool _running;
    private static Action<string>? _log;
    private static Func<IPAddress, bool>? _isLocal;
    private static string? _proxyHost;
    private static int _proxyPort;

    private static readonly object _reassocLock = new();
    private static volatile string? _warnedUnreachable;
    private static volatile string? _lastThrottled;
    private static long _lastThrottledAt;
    private static long _dnsTcpHits;
    private static int _dnsTcpLogged;

    public static bool IsRunning => _running;

    public static bool Start(string proxyHost, int proxyPort, Action<string> log, Func<IPAddress, bool> isLocal, out string? error)
    {
        _log = log;
        _isLocal = isLocal;
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        DnsTcp.Configure(log);
        error = null;
        try
        {
            if (!Associate(out error)) { error ??= "UDP ASSOCIATE 失败"; return false; }

            _local = new UdpClient(new IPEndPoint(IPAddress.Any, LocalPort));
            _running = true;
            new Thread(SocksReceiveLoop) { IsBackground = true }.Start();
            new Thread(LocalReceiveLoop) { IsBackground = true }.Start();
            new Thread(ControlWatchdog) { IsBackground = true }.Start();
            log($"UDP 隧道就绪  local=0.0.0.0:{LocalPort}  socks={_socksRelay}  本地源=0.0.0.0");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Stop();
            return false;
        }
    }

    /// <summary>
    /// 建一条 SOCKS5 控制连接并完成 UDP ASSOCIATE。
    ///
    /// 这里有个必须绑 0.0.0.0 的坑：上游代理不在本机时（比如机房里代理跑在
    /// 172.18.218.118，而目标是 172.19.3.17），它回的 BND.ADDR 就是它自己的地址。
    /// 原来把 SOCKS UDP socket 绑在 127.0.0.1，从 loopback 源地址往可路由地址发包，
    /// Windows 直接返回 WSAENETUNREACH(10051)，包一个都出不去——代理在本机时
    /// BND.ADDR 恰好也是 127.0.0.1，所以这个 bug 一直没暴露。
    /// </summary>
    private static bool Associate(out string? error)
    {
        error = null;
        if (_proxyHost == null) { error = "未记录代理地址"; return false; }

        var ctrl = new TcpClient { NoDelay = true };
        ctrl.Connect(_proxyHost, _proxyPort);
        var stream = ctrl.GetStream();

        stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
        var greet = ReadExact(stream, 2);
        if (greet[0] != 5 || greet[1] != 0) { error = "上游不是 SOCKS5"; try { ctrl.Close(); } catch { } return false; }

        stream.Write(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
        var head = ReadExact(stream, 4);
        if (head[1] != 0) { error = "UDP ASSOCIATE 被拒 (code " + head[1] + ")"; try { ctrl.Close(); } catch { } return false; }

        IPAddress bndIp;
        if (head[3] == 1) bndIp = new IPAddress(ReadExact(stream, 4));
        else if (head[3] == 4) bndIp = new IPAddress(ReadExact(stream, 16));
        else
        {
            var n = ReadExact(stream, 1);
            var name = Encoding.ASCII.GetString(ReadExact(stream, n[0]));
            var resolved = Dns.GetHostAddresses(name);
            bndIp = resolved.Length > 0 ? resolved[0] : IPAddress.Loopback;
        }
        var pb = ReadExact(stream, 2);
        int bndPort = (pb[0] << 8) | pb[1];
        if (bndIp.Equals(IPAddress.Any) || bndIp.Equals(IPAddress.IPv6Any)) bndIp = IPAddress.Loopback;

        var fam = bndIp.AddressFamily;
        var socks = new UdpClient(fam);
        socks.Client.Bind(new IPEndPoint(fam == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

        var oldSocks = _socks;
        var oldCtrl = _ctrl;
        var oldStream = _ctrlStream;

        _socks = socks;
        _socksRelay = new IPEndPoint(bndIp, bndPort);
        _ctrl = ctrl;
        _ctrlStream = stream;
        _warnedUnreachable = null;

        if (oldSocks != null) { try { oldSocks.Close(); } catch { } }
        if (oldStream != null) { try { oldStream.Close(); } catch { } }
        if (oldCtrl != null) { try { oldCtrl.Close(); } catch { } }
        return true;
    }

    /// <summary>
    /// 盯住控制连接：SOCKS5 里上游关掉那条 TCP 就等于 UDP 关联作废（RFC 1928 §7）。
    /// 原来的代码握手完再没人读它，代理一重启 UDP 就永久哑掉，只能手动停/启。
    /// </summary>
    private static void ControlWatchdog()
    {
        var s = _ctrlStream;
        if (s == null) return;
        var buf = new byte[64];
        try { while (_running && s.Read(buf, 0, buf.Length) > 0) { } } catch { }
        if (!_running) return;
        _log?.Invoke("UDP 控制连接被上游关闭，重建关联…");
        Reassociate();
    }

    private static void Reassociate()
    {
        if (!Monitor.TryEnter(_reassocLock)) return;
        try
        {
            for (int attempt = 1; attempt <= 6 && _running; attempt++)
            {
                Thread.Sleep(Math.Min(1000 * attempt, 5000));
                if (!_running) return;
                try
                {
                    if (Associate(out var err))
                    {
                        _log?.Invoke("UDP 关联已重建  socks=" + _socksRelay);
                        new Thread(ControlWatchdog) { IsBackground = true }.Start();
                        return;
                    }
                    _log?.Invoke("UDP 关联重建失败(" + attempt + "/6): " + err);
                }
                catch (Exception e) { _log?.Invoke("UDP 关联重建异常(" + attempt + "/6): " + e.Message); }
            }
            if (_running) _log?.Invoke("UDP 关联重建放弃：上游持续不可用，UDP 暂时走直连");
        }
        finally { Monitor.Exit(_reassocLock); }
    }

    public static void Stop()
    {
        _running = false;
        try { _local?.Close(); } catch { }
        try { _socks?.Close(); } catch { }
        try { _ctrlStream?.Close(); } catch { }
        try { _ctrl?.Close(); } catch { }
        _local = null; _socks = null; _ctrlStream = null; _ctrl = null;
        _warnedUnreachable = null;
        _dnsTcpHits = 0;
    }

    /// <summary>回收过期流（由 Redirector.Janitor 周期调用），防止长时间运行内存只增不减。</summary>
    public static void Purge(TimeSpan maxIdle)
    {
        long cutoff = DateTime.UtcNow.Ticks - maxIdle.Ticks;
        foreach (var kv in Flows)
        {
            if (kv.Value.LastSeen >= cutoff) continue;
            if (!Flows.TryRemove(kv.Key, out var flow)) continue;
            var rkey = flow.RemoteIp + ":" + flow.RemotePort;
            if (Reverse.TryGetValue(rkey, out var set))
            {
                set.TryRemove(kv.Key, out _);
                if (set.IsEmpty) Reverse.TryRemove(rkey, out _);
            }
        }
        DnsTcp.PurgeIdle(120000);
    }

    private static byte[] ReadExact(NetworkStream s, int n)
    {
        var b = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(b, off, n - off);
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
        return b;
    }

    private static void LocalReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint? from = null;
                var data = _local!.Receive(ref from!);
                if (from == null) continue;

                // 只收本机来源：中继在 0.0.0.0 上监听，不校验的话同局域网可以往已知流里塞包
                if (_isLocal != null && !_isLocal(from.Address))
                {
                    WarnThrottled("UDP 丢弃非本机来源 " + from);
                    continue;
                }

                var key = from.Address + ":" + from.Port;
                if (!Flows.TryGetValue(key, out var flow)) continue;
                flow.LastSeen = DateTime.UtcNow.Ticks;

                // DNS 优先走 TCP：上游 SOCKS5 的 UDP 常常是黑洞（节点不支持 UDP、
                // 或规则里 REJECT），而 CONNECT 那条出口是通的。交给线程池，
                // 别让一条 DNS 查询把整个 UDP 中继堵住。
                if (flow.RemotePort == DnsPort && DnsTcp.Enabled && _proxyHost != null)
                {
                    var q = data; var ep = from; var f = flow;
                    ThreadPool.QueueUserWorkItem(_ => DnsOverTcp(f, q, ep));
                    continue;
                }

                ForwardViaSocks(data, flow);
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { if (!_running) return; WarnThrottled("UDP 本地: " + e.Message); Thread.Sleep(20); }
        }
    }

    private static void DnsOverTcp(Flow flow, byte[] query, IPEndPoint client)
    {
        try
        {
            if (DnsTcp.TryQuery(_proxyHost!, _proxyPort, flow.RemoteIp, flow.RemotePort, query, 2500, out var resp))
            {
                Interlocked.Increment(ref _dnsTcpHits);
                if (Interlocked.Exchange(ref _dnsTcpLogged, 1) == 0)
                    _log?.Invoke("DNS 改走 TCP 隧道：UDP:53 不通时由 CONNECT 出口兜底");
                try { _local?.Send(resp, resp.Length, client); } catch { }
                return;
            }
        }
        catch (Exception e) { WarnThrottled("DNS/TCP: " + e.Message); }

        // DNS/TCP 不通就退回 SOCKS5 UDP，行为与改动前一致
        ForwardViaSocks(query, flow);
    }

    /// <summary>把一条报文封装进 SOCKS5 UDP 头送给上游中继。</summary>
    private static void ForwardViaSocks(byte[] payload, Flow flow)
    {
        var socks = _socks;
        var relay = _socksRelay;
        if (socks == null || relay == null) return;

        var pkt = BuildSocksUdp(flow.RemoteIp, flow.RemotePort, payload);
        try { socks.Send(pkt, pkt.Length, relay); }
        catch (ObjectDisposedException) { }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.NetworkUnreachable
                                       || se.SocketErrorCode == SocketError.HostUnreachable
                                       || se.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            // 聚合告警：这类错误每包都会复现，逐包刷屏只会把日志冲爆
            var tag = "UDP 中继不可达 " + relay + "（" + se.SocketErrorCode + "）——上游 UDP 没生效，DNS/QUIC 走不通";
            if (_warnedUnreachable != tag)
            {
                _warnedUnreachable = tag;
                _log?.Invoke(tag);
            }
        }
        catch (SocketException se) { WarnThrottled("UDP 本地: " + se.SocketErrorCode); }
    }

    private static void WarnThrottled(string msg)
    {
        long now = Environment.TickCount64;
        if (_lastThrottled == msg && now - _lastThrottledAt < 5000) return;
        _lastThrottled = msg;
        _lastThrottledAt = now;
        _log?.Invoke(msg);
    }

    private static void SocksReceiveLoop()
    {
        while (_running)
        {
            try
            {
                var socks = _socks;
                if (socks == null) { Thread.Sleep(50); continue; }

                IPEndPoint? from = null;
                var data = socks.Receive(ref from!);
                if (data.Length < 10) continue;

                int pos = 3; // RSV(2) + FRAG(1)
                IPAddress ip;
                int atyp = data[pos++];
                if (atyp == 1) { ip = new IPAddress(data.AsSpan(pos, 4)); pos += 4; }
                else if (atyp == 4) { ip = new IPAddress(data.AsSpan(pos, 16)); pos += 16; }
                else
                {
                    // 域名形式回复：解析失败就丢弃，不要抛异常打断整个循环
                    int n = data[pos++];
                    var name = Encoding.ASCII.GetString(data, pos, n);
                    pos += n;
                    if (!IPAddress.TryParse(name, out var parsed))
                    {
                        try { parsed = Dns.GetHostAddresses(name) is { Length: > 0 } addrs ? addrs[0] : null; } catch { parsed = null; }
                    }
                    if (parsed == null) { WarnThrottled("UDP 丢弃无法解析的域名回复 " + name); continue; }
                    ip = parsed;
                }
                int port = (data[pos] << 8) | data[pos + 1];
                pos += 2;

                if (!Reverse.TryGetValue(ip + ":" + port, out var clients)) continue;
                var payload = data.AsSpan(pos).ToArray();

                foreach (var key in clients.Keys)
                {
                    if (!Flows.TryGetValue(key, out var flow)) continue;
                    flow.LastSeen = DateTime.UtcNow.Ticks;
                    var ep = ParseEndPoint(key);
                    try { _local!.Send(payload, payload.Length, ep); } catch { }
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { if (!_running) return; WarnThrottled("UDP SOCKS: " + e.Message); Thread.Sleep(20); }
        }
    }

    private static IPEndPoint ParseEndPoint(string key)
    {
        int i = key.LastIndexOf(':');
        return new IPEndPoint(IPAddress.Parse(key[..i]), int.Parse(key[(i + 1)..]));
    }

    private static byte[] BuildSocksUdp(IPAddress dst, ushort port, byte[] payload)
    {
        var ipb = dst.GetAddressBytes();
        var pkt = new byte[3 + 1 + ipb.Length + 2 + payload.Length];
        pkt[0] = 0; pkt[1] = 0; pkt[2] = 0;
        pkt[3] = (byte)(ipb.Length == 4 ? 1 : 4);
        Buffer.BlockCopy(ipb, 0, pkt, 4, ipb.Length);
        pkt[4 + ipb.Length] = (byte)(port >> 8);
        pkt[5 + ipb.Length] = (byte)port;
        Buffer.BlockCopy(payload, 0, pkt, 6 + ipb.Length, payload.Length);
        return pkt;
    }

    public static void RecordFlow(IPAddress clientIp, ushort clientPort, IPAddress remoteIp, ushort remotePort)
    {
        var key = clientIp + ":" + clientPort;
        Flows[key] = new Flow { RemoteIp = remoteIp, RemotePort = remotePort, LastSeen = DateTime.UtcNow.Ticks };
        var rkey = remoteIp + ":" + remotePort;
        var set = Reverse.GetOrAdd(rkey, _ => new ConcurrentDictionary<string, byte>());
        set[key] = 0;
    }

    public static bool TryGetFlow(IPAddress clientIp, ushort clientPort, out Flow flow)
        => Flows.TryGetValue(clientIp + ":" + clientPort, out flow!);
}
