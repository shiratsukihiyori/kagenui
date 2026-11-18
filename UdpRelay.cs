using System;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string> Reverse = new();

    private static TcpClient? _ctrl;
    private static NetworkStream? _ctrlStream;
    private static UdpClient? _local;
    private static UdpClient? _socks;
    private static IPEndPoint? _socksRelay;
    private static volatile bool _running;
    private static Action<string>? _log;

    public static bool IsRunning => _running;

    public static bool Start(string proxyHost, int proxyPort, Action<string> log, out string? error)
    {
        error = null;
        _log = log;
        try
        {
            _ctrl = new TcpClient { NoDelay = true };
            _ctrl.Connect(proxyHost, proxyPort);
            _ctrlStream = _ctrl.GetStream();

            // SOCKS5 握手
            _ctrlStream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            var greet = ReadExact(_ctrlStream, 2);
            if (greet[0] != 5 || greet[1] != 0) { error = "上游不是 SOCKS5"; return false; }

            // UDP ASSOCIATE
            _ctrlStream.Write(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
            var head = ReadExact(_ctrlStream, 4);
            if (head[1] != 0) { error = "UDP ASSOCIATE 被拒 (code " + head[1] + ")"; return false; }

            IPAddress bndIp;
            if (head[3] == 1)
            {
                var a = ReadExact(_ctrlStream, 4);
                bndIp = new IPAddress(a);
            }
            else if (head[3] == 4)
            {
                var a = ReadExact(_ctrlStream, 16);
                bndIp = new IPAddress(a);
            }
            else
            {
                var n = ReadExact(_ctrlStream, 1);
                var d = ReadExact(_ctrlStream, n[0]);
                var name = Encoding.ASCII.GetString(d);
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
                if (atyp == 1) { ip = new IPAddress(data[pos..(pos + 4)]); pos += 4; }
                else if (atyp == 4) { ip = new IPAddress(data[pos..(pos + 16)]); pos += 16; }
                else
                {
                    int n = data[pos++];
                    var name = Encoding.ASCII.GetString(data, pos, n);
                    pos += n;
                    ip = IPAddress.Parse(name);
                }
                int port = (data[pos] << 8) | data[pos + 1];
                pos += 2;

                string rkey = ip + ":" + port;
                string? clientKey = Reverse.TryGetValue(rkey, out var ck) ? ck : null;
                if (clientKey == null) continue;
                if (!Flows.TryGetValue(clientKey, out var flow)) continue;
                flow.LastSeen = DateTime.UtcNow.Ticks;

                var payload = data[pos..];
                var ep = ParseEndPoint(clientKey);
                _local!.Send(payload, payload.Length, ep);
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
        pkt[0] = 0; pkt[1] = 0; pkt[2] = 0; // RSV + FRAG
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
        Reverse[remoteIp + ":" + remotePort] = key;
    }

    public static bool TryGetFlow(IPAddress clientIp, ushort clientPort, out Flow flow)
        => Flows.TryGetValue(clientIp + ":" + clientPort, out flow!);
}
