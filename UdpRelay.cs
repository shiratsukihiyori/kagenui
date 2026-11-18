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

    public static bool IsRunning => _running;

    public static bool Start(string proxyHost, int proxyPort, Action<string> log, Func<IPAddress, bool> isLocal, out string? error)
    {
        error = null;
        _log = log;
        _isLocal = isLocal;
        try
        {
            _ctrl = new TcpClient { NoDelay = true };
            _ctrl.Connect(proxyHost, proxyPort);
            _ctrlStream = _ctrl.GetStream();

            _ctrlStream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            var greet = ReadExact(_ctrlStream, 2);
            if (greet[0] != 5 || greet[1] != 0) { error = "上游不是 SOCKS5"; return false; }

            _ctrlStream.Write(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
            var head = ReadExact(_ctrlStream, 4);
            if (head[1] != 0) { error = "UDP ASSOCIATE 被拒 (code " + head[1] + ")"; return false; }

            IPAddress bndIp;
            if (head[3] == 1) bndIp = new IPAddress(ReadExact(_ctrlStream, 4));
            else if (head[3] == 4) bndIp = new IPAddress(ReadExact(_ctrlStream, 16));
            else
            {
                var n = ReadExact(_ctrlStream, 1);
                var name = Encoding.ASCII.GetString(ReadExact(_ctrlStream, n[0]));
                var resolved = Dns.GetHostAddresses(name);
                bndIp = resolved.Length > 0 ? resolved[0] : IPAddress.Loopback;
            }
            var pb = ReadExact(_ctrlStream, 2);
            int bndPort = (pb[0] << 8) | pb[1];
            if (bndIp.Equals(IPAddress.Any) || bndIp.Equals(IPAddress.IPv6Any)) bndIp = IPAddress.Loopback;

            _socksRelay = new IPEndPoint(bndIp, bndPort);
            _socks = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _local = new UdpClient(new IPEndPoint(IPAddress.Any, LocalPort));

            _running = true;
            new Thread(SocksReceiveLoop) { IsBackground = true }.Start();
            new Thread(LocalReceiveLoop) { IsBackground = true }.Start();
            log($"UDP 隧道就绪  local=0.0.0.0:{LocalPort}  socks={bndIp}:{bndPort}");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Stop();
            return false;
        }
    }

    public static void Stop()
    {
        _running = false;
        try { _local?.Close(); } catch { }
        try { _socks?.Close(); } catch { }
        try { _ctrlStream?.Close(); } catch { }
        try { _ctrl?.Close(); } catch { }
        _local = null; _socks = null; _ctrlStream = null; _ctrl = null;
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
                    _log?.Invoke("UDP 丢弃非本机来源 " + from);
                    continue;
                }

                var key = from.Address + ":" + from.Port;
                if (!Flows.TryGetValue(key, out var flow)) continue;
                flow.LastSeen = DateTime.UtcNow.Ticks;

                var pkt = BuildSocksUdp(flow.RemoteIp, flow.RemotePort, data);
                _socks!.Send(pkt, pkt.Length, _socksRelay!);
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { if (!_running) return; _log?.Invoke("UDP 本地: " + e.Message); Thread.Sleep(20); }
        }
    }

    private static void SocksReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint? from = null;
                var data = _socks!.Receive(ref from!);
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
                    if (parsed == null) { _log?.Invoke("UDP 丢弃无法解析的域名回复 " + name); continue; }
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
            catch (Exception e) { if (!_running) return; _log?.Invoke("UDP SOCKS: " + e.Message); Thread.Sleep(20); }
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
