using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Kagenui;

/// <summary>
/// DNS over TCP 兜底通道：把拦截到的 UDP:53 查询包进 RFC 1035 §4.2.2 的
/// 2 字节长度前缀帧，经 HTTP CONNECT 隧道发给同一个 DNS 服务器，再把应答注回客户端。
///
/// 为什么需要它：上游 SOCKS5 的 UDP 很可能是黑洞——节点不支持 UDP、或者规则
/// 里直接 REJECT。那种情况下 UDP ASSOCIATE 会成功、包发得出去、回不来，
/// DNS 就干等到超时，表现就是"关掉系统代理整台机器断网"。CONNECT 这条出口
/// 已经在用（TCP 走的就是它），复用它发 DNS 不用新开任何通道。
///
/// 连续失败 3 次就自我关闭，退回 SOCKS5 UDP，避免在支持 UDP 的机器上白等。
/// </summary>
internal static class DnsTcp
{
    private sealed class Chan
    {
        public TcpClient Client = null!;
        public NetworkStream Stream = null!;
        public readonly object Gate = new();
        public long LastUsed;
    }

    private static readonly ConcurrentDictionary<string, Chan> Chans = new();
    public static volatile bool Enabled = true;

    private static int _failStreak;
    private static Action<string>? _log;

    public static void Configure(Action<string> log) => _log = log;

    /// <summary>每次点启动都重新给一次机会：上一轮跑成什么样不该污染这一轮。</summary>
    public static void Reset()
    {
        Enabled = true;
        _failStreak = 0;
    }

    private static string Key(IPAddress remote, int port) => remote + ":" + port;

    /// <summary>发一条查询、取一条应答。失败返回 false，由调用方回退 SOCKS5 UDP。</summary>
    public static bool TryQuery(string proxyHost, int proxyPort, IPAddress remote, int remotePort,
                                byte[] query, int timeoutMs, out byte[] response)
    {
        response = Array.Empty<byte>();
        if (!Enabled || query.Length == 0 || query.Length > 65535) return false;

        string key = Key(remote, remotePort);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Chan ch;
            try
            {
                ch = Chans.GetOrAdd(key, _ => Connect(proxyHost, proxyPort, remote, remotePort, timeoutMs));
            }
            catch (Exception e)
            {
                NoteFailure("DNS/TCP 建链失败 " + key + ": " + e.Message);
                return false;
            }

            lock (ch.Gate)
            {
                try
                {
                    ch.Stream.ReadTimeout = timeoutMs;
                    ch.Stream.WriteTimeout = timeoutMs;

                    var frame = new byte[2 + query.Length];
                    frame[0] = (byte)(query.Length >> 8);
                    frame[1] = (byte)query.Length;
                    Buffer.BlockCopy(query, 0, frame, 2, query.Length);
                    ch.Stream.Write(frame, 0, frame.Length);
                    ch.Stream.Flush();

                    var hdr = ReadExact(ch.Stream, 2);
                    int len = (hdr[0] << 8) | hdr[1];
                    if (len < 12) throw new IOException("应答长度非法 " + len);
                    var resp = ReadExact(ch.Stream, len);
                    ch.LastUsed = Environment.TickCount64;
                    _failStreak = 0;
                    response = resp;
                    return true;
                }
                catch (Exception e)
                {
                    Close(key, ch);
                    if (attempt == 1) NoteFailure("DNS/TCP " + key + " 失败: " + e.Message);
                }
            }
        }
        return false;
    }

    private static Chan Connect(string proxyHost, int proxyPort, IPAddress remote, int remotePort, int timeoutMs)
    {
        string host = remote.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + remote + "]" : remote.ToString();
        string target = host + ":" + remotePort;

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var task = tcp.ConnectAsync(proxyHost, proxyPort);
            if (!task.Wait(timeoutMs)) throw new IOException("连代理 " + proxyHost + ":" + proxyPort + " 超时");
            task.GetAwaiter().GetResult();

            var ns = tcp.GetStream();
            ns.ReadTimeout = timeoutMs;
            ns.WriteTimeout = timeoutMs;

            var req = Encoding.ASCII.GetBytes(
                "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\nProxy-Connection: keep-alive\r\n\r\n");
            ns.Write(req, 0, req.Length);
            ns.Flush();

            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (ns.Read(one, 0, 1) != 1) throw new IOException("CONNECT 无响应");
                sb.Append((char)one[0]);
                int n = sb.Length;
                if (n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n') break;
                if (n > 8192) throw new IOException("CONNECT 响应头过长");
            }
            string first = sb.ToString().Split('\n')[0].Trim();
            if (first.IndexOf(" 200", StringComparison.Ordinal) < 0)
                throw new IOException("CONNECT " + target + " -> " + first);

            return new Chan { Client = tcp, Stream = ns, LastUsed = Environment.TickCount64 };
        }
        catch
        {
            try { tcp.Close(); } catch { }
            throw;
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

    private static void Close(string key, Chan ch)
    {
        Chans.TryRemove(key, out _);
        try { ch.Stream.Close(); } catch { }
        try { ch.Client.Close(); } catch { }
    }

    private static void NoteFailure(string msg)
    {
        int n = Interlocked.Increment(ref _failStreak);
        if (n >= 3)
        {
            Enabled = false;
            _log?.Invoke(msg + "；DNS/TCP 连错 " + n + " 次，退回 SOCKS5 UDP");
            return;
        }
        _log?.Invoke(msg);
    }

    /// <summary>回收闲置超过 ms 毫秒的隧道，防止代理侧悄悄断连后句柄只增不减。</summary>
    public static void PurgeIdle(int ms)
    {
        long now = Environment.TickCount64;
        foreach (var kv in Chans)
        {
            var ch = kv.Value;
            if (now - ch.LastUsed < ms) continue;
            if (!Monitor.TryEnter(ch.Gate)) continue;
            try
            {
                if (now - ch.LastUsed < ms) continue;
                Close(kv.Key, ch);
            }
            finally { Monitor.Exit(ch.Gate); }
        }
    }
}
