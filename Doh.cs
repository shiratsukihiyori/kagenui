using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Kagenui;

/// <summary>
/// DoH（DNS over HTTPS，RFC 8484）：DNS 的最后一条退路。
///
/// 什么时候用得上：上游代理**拒绝 CONNECT 到 53 端口**（面板限端口、规则 REJECT），
/// 而 CONNECT 443 是通的。这时候 DNS/TCP 那条路走不通，SOCKS5 UDP 又是黑洞，
/// 只剩「把 DNS 报文当成 HTTPS 请求体发给 1.1.1.1」这一条。
///
/// 端点用 IP 直连（https://1.1.1.1/dns-query），不解析域名——
/// 那台机器 DNS 本来就是坏的，拿域名去连端点等于用坏掉的东西修自己。
/// 连接照旧经 HTTP CONNECT 出去，不新开任何通道。
/// </summary>
internal static class Doh
{
    private static readonly string[] Endpoints = { "1.1.1.1", "1.0.0.1" };
    private const int Port = 443;
    private const string Path = "/dns-query";

    private sealed class Chan
    {
        public TcpClient Tcp = null!;
        public SslStream Ssl = null!;
        public readonly object Gate = new();
        public long LastUsed;
    }

    private static readonly ConcurrentDictionary<string, Chan> Chans = new();
    private static volatile bool _enabled = true;
    private static int _fails;
    private static Action<string>? _log;

    public static bool Enabled => _enabled;

    public static void Configure(Action<string> log) => _log = log;

    public static void Reset()
    {
        _enabled = true;
        _fails = 0;
    }

    /// <summary>发一条 DNS 报文，拿一条应答。失败返回 false，由调用方继续往下退。</summary>
    public static bool TryQuery(string proxyHost, int proxyPort, byte[] query, int timeoutMs, out byte[] response)
    {
        response = Array.Empty<byte>();
        if (!_enabled || query.Length == 0) return false;

        foreach (var ep in Endpoints)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Chan ch;
                try
                {
                    ch = Chans.GetOrAdd(ep, key => Connect(key, proxyHost, proxyPort, timeoutMs));
                }
                catch (Exception e)
                {
                    NoteFailure("DoH 建链失败 " + ep + ": " + e.Message);
                    break;
                }

                lock (ch.Gate)
                {
                    try
                    {
                        if (Send(ch, ep, query, timeoutMs, out var resp))
                        {
                            _fails = 0;
                            response = resp;
                            return true;
                        }
                        Close(ep, ch);
                    }
                    catch (Exception e)
                    {
                        Close(ep, ch);
                        if (attempt == 1) NoteFailure("DoH " + ep + " 失败: " + e.Message);
                    }
                }
            }
        }
        return false;
    }

    private static Chan Connect(string ep, string proxyHost, int proxyPort, int timeoutMs)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var task = tcp.ConnectAsync(proxyHost, proxyPort);
            if (!task.Wait(timeoutMs)) throw new IOException("连代理 " + proxyHost + ":" + proxyPort + " 超时");
            task.GetAwaiter().GetResult();

            var ns = tcp.GetStream();
            ns.ReadTimeout = timeoutMs;
            ns.WriteTimeout = timeoutMs;

            string target = ep + ":" + Port;
            var req = Encoding.ASCII.GetBytes(
                "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\nProxy-Connection: keep-alive\r\n\r\n");
            ns.Write(req, 0, req.Length);
            ns.Flush();

            string head = ReadHead(ns);
            string first = head.Length > 0 ? head.Split('\n')[0].Trim() : "(无响应)";
            if (first.IndexOf(" 200", StringComparison.Ordinal) < 0)
                throw new IOException("CONNECT " + target + " -> " + first);

            var ssl = new SslStream(ns, false, (_, _, _, errors) => errors == SslPolicyErrors.None);
            ssl.AuthenticateAsClient(ep);
            return new Chan { Tcp = tcp, Ssl = ssl, LastUsed = Environment.TickCount64 };
        }
        catch
        {
            try { tcp.Close(); } catch { }
            throw;
        }
    }

    private static bool Send(Chan ch, string ep, byte[] query, int timeoutMs, out byte[] response)
    {
        response = Array.Empty<byte>();
        ch.Ssl.ReadTimeout = timeoutMs;
        ch.Ssl.WriteTimeout = timeoutMs;

        string head =
            "POST " + Path + " HTTP/1.1\r\n" +
            "Host: " + ep + "\r\n" +
            "Content-Type: application/dns-message\r\n" +
            "Accept: application/dns-message\r\n" +
            "Content-Length: " + query.Length + "\r\n" +
            "Connection: keep-alive\r\n\r\n";
        var hb = Encoding.ASCII.GetBytes(head);
        ch.Ssl.Write(hb, 0, hb.Length);
        ch.Ssl.Write(query, 0, query.Length);
        ch.Ssl.Flush();

        string header = ReadHead(ch.Ssl);
        string first = header.Length > 0 ? header.Split('\n')[0].Trim() : "";
        if (first.IndexOf(" 200", StringComparison.Ordinal) < 0)
            throw new IOException("DoH " + ep + " -> " + (first.Length > 0 ? first : "(无响应)"));

        int len = -1;
        foreach (var line in header.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(t[15..].Trim(), out len);
                break;
            }
        }
        if (len <= 0 || len > 65535) throw new IOException("DoH 应答长度非法 " + len);

        var body = ReadExact(ch.Ssl, len);
        ch.LastUsed = Environment.TickCount64;
        response = body;
        return true;
    }

    private static string ReadHead(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (s.Read(one, 0, 1) == 1)
        {
            sb.Append((char)one[0]);
            int n = sb.Length;
            if (n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n') break;
            if (n > 8192) throw new IOException("响应头过长");
        }
        return sb.ToString();
    }

    private static byte[] ReadExact(Stream s, int n)
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

    private static void Close(string ep, Chan ch)
    {
        Chans.TryRemove(ep, out _);
        try { ch.Ssl.Close(); } catch { }
        try { ch.Tcp.Close(); } catch { }
    }

    private static void NoteFailure(string msg)
    {
        int n = Interlocked.Increment(ref _fails);
        if (n >= 4)
        {
            _enabled = false;
            _log?.Invoke(msg + "；DoH 连错 " + n + " 次，DNS 只剩 SOCKS5 UDP");
            return;
        }
        _log?.Invoke(msg);
    }

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
