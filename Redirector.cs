using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Kagenui;

/// <summary>
/// 进程级 TCP 重定向：用 WinDivert 在内核层改写指定进程的出站连接，
/// 让它连到本地中继，中继再经上游 HTTP 代理 CONNECT 出网。
/// 不建立 TUN、不改系统路由、不碰其它进程。
/// </summary>
internal static class Redirector
{
    public sealed class Options
    {
        public string[] Names = Array.Empty<string>();
        public int Pid;
        public string ProxyHost = "127.0.0.1";
        public int ProxyPort = 7890;
        public string? LaunchPath;
        public string? LogFile;
        public bool Verbose = true;
        public int RelayPort = 38999;
    }

    public static Action<string>? Sink;
    private static void Log(string msg)
    {
        Sink?.Invoke(msg);
    }

    private const int LayerNetwork = 0;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAddr
    {
        public long Timestamp;
        public uint Flags;
        public uint Reserved2;
        public ulong U0, U1, U2, U3, U4, U5, U6, U7;

        public bool Outbound
        {
            get { return ((Flags >> 17) & 1u) != 0; }
            set { if (value) Flags |= 1u << 17; else Flags &= ~(1u << 17); }
        }
        public bool Loopback { get { return ((Flags >> 18) & 1u) != 0; } }
        public bool IPv6 { get { return ((Flags >> 20) & 1u) != 0; } }
        public void ClearChecksums() { Flags &= ~((1u << 21) | (1u << 22) | (1u << 23)); }
        public void SetLoopback(bool v) { if (v) Flags |= 1u << 18; else Flags &= ~(1u << 18); }
    }

    private static class Native
    {
        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref WAddr addr);
        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref WAddr addr);
        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertClose(IntPtr handle);
        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen, ref WAddr addr, ulong flags);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int af, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sorted, int af, int tableClass, uint reserved);
    }

    private sealed class Flow
    {
        public ushort ClientPort;
        public uint ClientIp;
        public uint RemoteIp;
        public ushort RemotePort;
        public long LastSeen;
    }

    private static readonly ConcurrentDictionary<ushort, Flow> flows = new();
    private static readonly HashSet<int> targetPids = new();
    private static readonly HashSet<int> extraPids = new();   // 运行时注入（验证探针）
    private static readonly object pidLock = new();
    private static readonly object logLock = new();
    private static readonly HashSet<string> localAddrs = new(StringComparer.OrdinalIgnoreCase);

    private static bool running;
    private static bool verbose = true;
    private static string[] targetNames = Array.Empty<string>();
    private static int targetPid;
    private static string proxyHost = "127.0.0.1";
    private static int proxyPort = 7890;
    private static int relayPort = 38999;
    private static string? launchPath;
    private static string? logPath;
    private static IntPtr _rh, _fh, _uh, _ufh;
    private static TcpListener? _listener;

    public static bool IsRunning => running;

    public static bool Start(Options o)
    {
        if (running) return true;

        targetNames = (o.Names ?? Array.Empty<string>())
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        targetPid = o.Pid;
        proxyHost = o.ProxyHost;
        proxyPort = o.ProxyPort;
        relayPort = o.RelayPort;
        launchPath = o.LaunchPath;
        logPath = o.LogFile;
        verbose = o.Verbose;

        if (proxyPort <= 0 || (targetNames.Length == 0 && targetPid == 0))
        {
            Log("参数不完整：需要进程名或 PID，以及代理端口");
            return false;
        }

        flows.Clear();
        LoadLocalAddresses();
        running = true;

        Log($"target={(targetNames.Length > 0 ? string.Join(",", targetNames) : "pid:" + targetPid)}  proxy={proxyHost}:{proxyPort}  relay=0.0.0.0:{relayPort}");

        // 先同步把中继端口占住：绑不上就直接失败，避免"界面显示已接管、实际流量全进死端口"
        try
        {
            _listener = new TcpListener(IPAddress.Any, relayPort);
            _listener.Start(256);
        }
        catch (Exception e)
        {
            Log("中继端口 " + relayPort + " 绑定失败：" + e.Message + "（被占用？）");
            running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            return false;
        }
        ThreadLog("中继监听 0.0.0.0:" + relayPort);

        var boundListener = _listener;
        new Thread(() => RelayLoop(boundListener)) { IsBackground = true }.Start();
        new Thread(PidWatcher) { IsBackground = true }.Start();
        new Thread(Janitor) { IsBackground = true }.Start();

        _rh = Native.WinDivertOpen("outbound and !loopback and ip and tcp", LayerNetwork, 0, 0);
        if (_rh == IntPtr.Zero || _rh == new IntPtr(-1))
        {
            Log("WinDivertOpen 失败: " + Marshal.GetLastWin32Error() + "（需要管理员权限）");
            Stop();
            return false;
        }

        _fh = Native.WinDivertOpen("loopback and ip and tcp and tcp.SrcPort == " + relayPort, LayerNetwork, 0, 0);
        if (_fh == IntPtr.Zero || _fh == new IntPtr(-1))
        {
            Log("WinDivertOpen(回程) 失败: " + Marshal.GetLastWin32Error());
            Stop();
            return false;
        }

        new Thread(() => RedirectLoop(_rh)) { IsBackground = true }.Start();
        new Thread(() => ReplyLoop(_fh)) { IsBackground = true }.Start();

        // UDP：QUIC / DNS 也能走代理（SOCKS5 UDP ASSOCIATE）
        if (UdpRelay.Start(proxyHost, proxyPort, ThreadLog, IsLocalAddress, out var udpErr))
        {
            _uh = Native.WinDivertOpen("outbound and !loopback and ip and udp", LayerNetwork, 0, 0);
            _ufh = Native.WinDivertOpen("loopback and ip and udp and udp.SrcPort == " + UdpRelay.LocalPort, LayerNetwork, 0, 0);
            if (_uh != IntPtr.Zero && _uh != new IntPtr(-1) && _ufh != IntPtr.Zero && _ufh != new IntPtr(-1))
            {
                new Thread(() => UdpRedirectLoop(_uh)) { IsBackground = true }.Start();
                new Thread(() => UdpReplyLoop(_ufh)) { IsBackground = true }.Start();
            }
            else ThreadLog("UDP 句柄打开失败: " + Marshal.GetLastWin32Error() + "（UDP 将走直连）");
        }
        else ThreadLog("UDP 隧道不可用（" + udpErr + "），UDP 将走直连");

        if (!string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath))
        {
            Log("1.5 秒后启动目标: " + launchPath);
            var path = launchPath;
            new Thread(() =>
            {
                Thread.Sleep(1500);
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); Log("已启动目标程序"); }
                catch (Exception e) { Log("启动目标失败: " + e.Message); }
            }) { IsBackground = true }.Start();
        }

        Log("运行中 — 只接管上面这些进程的 TCP，其它流量原样直连");
        return true;
    }

    public static void Stop()
    {
        if (!running) return;
        running = false;
        try { if (_listener != null) { _listener.Stop(); _listener = null; } } catch { }
        try { if (_rh != IntPtr.Zero) { Native.WinDivertClose(_rh); _rh = IntPtr.Zero; } } catch { }
        try { if (_fh != IntPtr.Zero) { Native.WinDivertClose(_fh); _fh = IntPtr.Zero; } } catch { }
        try { if (_uh != IntPtr.Zero) { Native.WinDivertClose(_uh); _uh = IntPtr.Zero; } } catch { }
        try { if (_ufh != IntPtr.Zero) { Native.WinDivertClose(_ufh); _ufh = IntPtr.Zero; } } catch { }
        UdpRelay.Stop();
        Log("已停止，流量恢复直连");
    }

    private static void LogFile(string msg)
    {
        if (logPath == null) return;
        lock (logLock)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, new UTF8Encoding(true)); }
            catch { }
        }
    }

    private static void ThreadLog(string msg) { LogFile(msg); Log(msg); }

    private static bool IsLocalAddress(IPAddress addr) =>
        IPAddress.IsLoopback(addr) || localAddrs.Contains(addr.ToString());

    private static void LoadLocalAddresses()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    localAddrs.Add(ua.Address.ToString());
        }
        catch { }
    }

    private static void PidWatcher()
    {
        while (running)
        {
            try
            {
                var set = new HashSet<int>();
                if (targetPid != 0) { try { Process.GetProcessById(targetPid); set.Add(targetPid); } catch { } }
                foreach (var n in targetNames)
                    foreach (var p in Process.GetProcessesByName(n)) set.Add(p.Id);

                lock (pidLock)
                {
                    foreach (int p in extraPids) set.Add(p);
                    if (verbose && !SameSet(targetPids, set))
                        ThreadLog("目标进程 PID: " + string.Join(",", set));
                    targetPids.Clear();
                    foreach (int p in set) targetPids.Add(p);
                }
            }
            catch (Exception e) { ThreadLog("PidWatcher: " + e.Message); }
            Thread.Sleep(400);
        }
    }

    private static bool SameSet(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (int x in b) if (!a.Contains(x)) return false;
        return true;
    }

    /// <summary>运行期把一个 PID 加进接管集合（验证探针用），不通过进程名匹配。</summary>
    public static void AddTargetPid(int pid)
    {
        lock (pidLock) { extraPids.Add(pid); targetPids.Add(pid); }
    }

    private static bool IsTarget(int pid)
    {
        lock (pidLock) { if (targetPids.Contains(pid) || extraPids.Contains(pid)) return true; }
        if (targetPid != 0 && pid == targetPid) return true;
        if (targetNames.Length == 0) return false;
        try
        {
            string name = Process.GetProcessById(pid).ProcessName;
            if (targetNames.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase)))
            {
                lock (pidLock) targetPids.Add(pid);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void Janitor()
    {
        while (running)
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (var kv in flows)
                if (now - kv.Value.LastSeen > TimeSpan.TicksPerMinute * 5)
                    flows.TryRemove(kv.Key, out _);
            UdpRelay.Purge(TimeSpan.FromMinutes(5));   // UDP 流表同样要回收，否则长跑必泄漏
            Thread.Sleep(30000);
        }
    }

    private static void RedirectLoop(IntPtr handle)
    {
        var packet = new byte[65535];
        var addr = new WAddr();
        while (running)
        {
            if (!Native.WinDivertRecv(handle, packet, (uint)packet.Length, out uint len, ref addr))
            {
                if (running) Thread.Sleep(50);
                continue;
            }
            try { ProcessOutbound(packet, len, ref addr, handle); }
            catch (Exception e) { ThreadLog("outbound: " + e.Message); Send(handle, packet, len, ref addr); }
        }
    }

    private static void ProcessOutbound(byte[] p, uint len, ref WAddr addr, IntPtr handle)
    {
        if (addr.IPv6 || len < 40) { Send(handle, p, len, ref addr); return; }
        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || len < ihl + 20) { Send(handle, p, len, ref addr); return; }

        int tcp = ihl;
        ushort sp = ReadU16(p, tcp);
        ushort dp = ReadU16(p, tcp + 2);
        uint sip = ReadU32(p, 12);
        uint dip = ReadU32(p, 16);
        byte flags = p[tcp + 13];
        bool syn = (flags & 0x02) != 0;
        bool ack = (flags & 0x10) != 0;

        if (flows.TryGetValue(sp, out var flow) && flow.RemoteIp == dip && flow.RemotePort == dp)
        {
            flow.LastSeen = DateTime.UtcNow.Ticks;
            Rewrite(p, len, ref addr, sip, sp, sip, (ushort)relayPort, true);
            Send(handle, p, len, ref addr);
            return;
        }

        if (syn && !ack)
        {
            int pid = FindOwnerPid(sip, sp);
            if (pid != 0 && IsTarget(pid))
            {
                flows[sp] = new Flow { ClientPort = sp, ClientIp = sip, RemoteIp = dip, RemotePort = dp, LastSeen = DateTime.UtcNow.Ticks };
                ThreadLog("+ " + IpStr(dip) + ":" + dp + "  pid=" + pid + "  sport=" + sp);
                Rewrite(p, len, ref addr, sip, sp, sip, (ushort)relayPort, true);
                Send(handle, p, len, ref addr);
                return;
            }
            if (verbose) ThreadLog("pass  " + IpStr(dip) + ":" + dp + "  pid=" + pid + "  sport=" + sp);
        }

        Send(handle, p, len, ref addr);
    }

    private static void Rewrite(byte[] p, uint len, ref WAddr addr, uint srcIp, ushort srcPort, uint dstIp, ushort dstPort, bool outbound)
    {
        int ihl = (p[0] & 0x0F) * 4;
        WriteU32(p, 12, srcIp);
        WriteU32(p, 16, dstIp);
        WriteU16(p, ihl, srcPort);
        WriteU16(p, ihl + 2, dstPort);

        addr.Outbound = outbound;
        addr.SetLoopback(true);
        addr.ClearChecksums();
        Native.WinDivertHelperCalcChecksums(p, len, ref addr, 0);
    }

    private static void ReplyLoop(IntPtr handle)
    {
        var packet = new byte[65535];
        var addr = new WAddr();
        while (running)
        {
            if (!Native.WinDivertRecv(handle, packet, (uint)packet.Length, out uint len, ref addr))
            {
                if (running) Thread.Sleep(50);
                continue;
            }
            try
            {
                if (addr.IPv6 || len < 40) continue;
                int ihl = (packet[0] & 0x0F) * 4;
                int tcp = ihl;
                ushort dp = ReadU16(packet, tcp + 2);
                if (!flows.TryGetValue(dp, out var flow)) continue;
                Rewrite(packet, len, ref addr, flow.RemoteIp, flow.RemotePort, flow.ClientIp, dp, false);
                Send(handle, packet, len, ref addr);
            }
            catch (Exception e) { ThreadLog("reply: " + e.Message); }
        }
    }

    private static void Send(IntPtr handle, byte[] p, uint len, ref WAddr addr)
    {
        if (!Native.WinDivertSend(handle, p, len, out _, ref addr))
            ThreadLog("WinDivertSend: " + Marshal.GetLastWin32Error());
    }

    private static void RelayLoop(TcpListener listener)
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    private static void HandleClient(TcpClient client)
    {
        ushort port = 0;
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
            if (!localAddrs.Contains(peer.Address.ToString()) && !IPAddress.IsLoopback(peer.Address))
            {
                ThreadLog("拒绝外部连接 " + peer);
                client.Close();
                return;
            }
            port = (ushort)peer.Port;

            if (!flows.TryGetValue(port, out var flow))
            {
                client.Close();
                return;
            }

            // 先看客户端发出的第一段数据，取出域名（TLS SNI / HTTP Host）。
            // 代理（xray/clash）多数按域名分流，只给 IP 会走错出口甚至直连失败。
            byte[]? buffered = null;
            string? sniffed = null;
            try
            {
                client.ReceiveTimeout = 1500;   // 只为嗅探首包设的临时超时
                var head = new byte[4096];
                int n = client.GetStream().Read(head, 0, head.Length);
                if (n > 0)
                {
                    buffered = new byte[n];
                    Array.Copy(head, buffered, n);
                    sniffed = SniffHost(buffered);
                }
            }
            catch { }
            finally
            {
                // 必须复位！否则 SO_RCVTIMEO 会一直生效，
                // 空闲超过 1.5 秒的连接会在 Pump 里抛超时被误杀（HTTP/2 长连接、视频流全挂）。
                try { client.ReceiveTimeout = 0; } catch { }
            }

            string hostPart = !string.IsNullOrEmpty(sniffed) ? sniffed! : IpStr(flow.RemoteIp);
            string target = hostPart + ":" + flow.RemotePort;
            if (sniffed != null) ThreadLog("  SNI " + sniffed);

            upstream = new TcpClient { NoDelay = true };
            upstream.Connect(proxyHost, proxyPort);

            NetworkStream ns = upstream.GetStream();
            byte[] req = Encoding.ASCII.GetBytes("CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\nProxy-Connection: keep-alive\r\n\r\n");
            ns.Write(req, 0, req.Length);
            ns.Flush();

            string status = ReadHeaders(ns);
            string first = status.Length > 0 ? status.Split('\n')[0].Trim() : "(无响应)";
            if (first.IndexOf(" 200", StringComparison.Ordinal) < 0)
            {
                ThreadLog("x CONNECT " + target + " -> " + first);
                client.Close(); upstream.Close();
                return;
            }

            ThreadLog("> " + target + " 隧道建立");
            if (buffered != null && buffered.Length > 0)
            {
                ns.Write(buffered, 0, buffered.Length);
                ns.Flush();
            }
            NetworkStream cs = client.GetStream();
            long up = 0, down = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Thread a = Pump(cs, ns, client, upstream, n => Interlocked.Add(ref up, n));
            Thread b = Pump(ns, cs, upstream, client, n => Interlocked.Add(ref down, n));
            a.Join(); b.Join();
            sw.Stop();
            ThreadLog("< " + target + " 存活 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s  ↑" + Fmt(up) + " ↓" + Fmt(down));
        }
        catch (Exception e)
        {
            ThreadLog("中继 " + port + ": " + e.Message);
            try { client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
        }
    }

    private static string ReadHeaders(NetworkStream ns)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (ns.Read(one, 0, 1) == 1)
        {
            sb.Append((char)one[0]);
            int n = sb.Length;
            if (n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n') break;
            if (n > 8192) break;
        }
        return sb.ToString();
    }

    private static string Fmt(long n) =>
        n >= 1048576 ? (n / 1048576.0).ToString("F1") + "MB" : n >= 1024 ? (n / 1024.0).ToString("F0") + "KB" : n + "B";

    private static Thread Pump(NetworkStream from, NetworkStream to, TcpClient a, TcpClient b, Action<long>? onBytes = null)
    {
        var t = new Thread(() =>
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0) { to.Write(buf, 0, n); to.Flush(); onBytes?.Invoke(n); }
            }
            catch { }
            finally
            {
                try { a.Close(); } catch { }
                try { b.Close(); } catch { }
            }
        }) { IsBackground = true };
        t.Start();
        return t;
    }

    private static int FindOwnerPid(uint localAddr, ushort localPort)
    {
        // 预算 24ms：SYN 到达时内核表可能还没登记该 socket，短暂重试能显著降低 pid=0 漏判。
        // 单次 1ms、最多 24 次，拿到立刻返回，失败直接放行不阻塞主循环太久。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 0; attempt < 24; attempt++)
        {
            int size = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, TcpTableOwnerPidAll, 0);
            if (size <= 0) { Thread.Sleep(1); continue; }

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.GetExtendedTcpTable(buf, ref size, false, 2, TcpTableOwnerPidAll, 0) == 0)
                {
                    int rows = Marshal.ReadInt32(buf);
                    IntPtr row = IntPtr.Add(buf, 4);
                    for (int i = 0; i < rows; i++, row = IntPtr.Add(row, 24))
                    {
                        uint rawAddr = (uint)Marshal.ReadInt32(row, 4);
                        uint rawPort = (uint)Marshal.ReadInt32(row, 8);
                        uint la = (uint)IPAddress.NetworkToHostOrder((int)rawAddr);
                        ushort lp = (ushort)IPAddress.NetworkToHostOrder((short)(rawPort & 0xFFFF));
                        if (lp == localPort && la == localAddr) return Marshal.ReadInt32(row, 20);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            Thread.Sleep(1);
        }
        return 0;
    }


    /// <summary>从首段字节里提取目标域名：TLS ClientHello 的 SNI，或 HTTP 的 Host 头。</summary>
    private static string? SniffHost(byte[] b)
    {
        if (b.Length > 5 && b[0] == 0x16 && b[5] == 0x01) return ParseSni(b);

        var s = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 2048));
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line.Substring(5).Trim();
                int c = v.IndexOf(':');
                return c > 0 ? v[..c] : v;
            }
            if (line.Length == 0) break;
        }
        return null;
    }

    private static string? ParseSni(byte[] b)
    {
        try
        {
            int pos = 5 + 4 + 2 + 32; // 记录头 + 版本 + 随机数
            int sidLen = b[pos]; pos += 1 + sidLen;
            int csLen = (b[pos] << 8) | b[pos + 1]; pos += 2 + csLen;
            int compLen = b[pos]; pos += 1 + compLen;
            if (pos + 2 > b.Length) return null;
            int extLen = (b[pos] << 8) | b[pos + 1]; pos += 2;
            int end = Math.Min(b.Length, pos + extLen);
            while (pos + 4 <= end)
            {
                int type = (b[pos] << 8) | b[pos + 1];
                int len = (b[pos + 2] << 8) | b[pos + 3];
                pos += 4;
                if (type == 0x0000 && pos + 5 <= end)
                {
                    int nameLen = (b[pos + 3] << 8) | b[pos + 4];
                    if (pos + 5 + nameLen <= end)
                        return Encoding.ASCII.GetString(b, pos + 5, nameLen);
                }
                pos += len;
            }
        }
        catch { }
        return null;
    }


    // ── UDP ────────────────────────────────────────────────────────────
    private static void UdpRedirectLoop(IntPtr handle)
    {
        var packet = new byte[65535];
        var addr = new WAddr();
        while (running)
        {
            if (!Native.WinDivertRecv(handle, packet, (uint)packet.Length, out uint len, ref addr))
            {
                if (running) Thread.Sleep(50);
                continue;
            }
            try { ProcessOutboundUdp(packet, len, ref addr, handle); }
            catch (Exception e) { ThreadLog("udp out: " + e.Message); Send(handle, packet, len, ref addr); }
        }
    }

    private static void ProcessOutboundUdp(byte[] p, uint len, ref WAddr addr, IntPtr handle)
    {
        if (addr.IPv6 || len < 28) { Send(handle, p, len, ref addr); return; }
        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || len < ihl + 8) { Send(handle, p, len, ref addr); return; }

        ushort sp = ReadU16(p, ihl);
        ushort dp = ReadU16(p, ihl + 2);
        uint sip = ReadU32(p, 12);
        uint dip = ReadU32(p, 16);

        if (dp == UdpRelay.LocalPort) { Send(handle, p, len, ref addr); return; }

        if (UdpRelay.TryGetFlow(new IPAddress(BitConverter.GetBytes(sip).Reverse().ToArray()), sp, out var known)
            && known.RemoteIp.Equals(new IPAddress(BitConverter.GetBytes(dip).Reverse().ToArray()))
            && known.RemotePort == dp)
        {
            Rewrite(p, len, ref addr, sip, sp, sip, (ushort)UdpRelay.LocalPort, true);
            Send(handle, p, len, ref addr);
            return;
        }

        int pid = FindOwnerPidUdp(sp);
        if (pid != 0 && IsTarget(pid))
        {
            var clientIp = new IPAddress(BitConverter.GetBytes(sip).Reverse().ToArray());
            var remoteIp = new IPAddress(BitConverter.GetBytes(dip).Reverse().ToArray());
            UdpRelay.RecordFlow(clientIp, sp, remoteIp, dp);
            if (verbose) ThreadLog("U " + remoteIp + ":" + dp + "  pid=" + pid + "  sport=" + sp);
            Rewrite(p, len, ref addr, sip, sp, sip, (ushort)UdpRelay.LocalPort, true);
            Send(handle, p, len, ref addr);
            return;
        }

        Send(handle, p, len, ref addr);
    }

    private static void UdpReplyLoop(IntPtr handle)
    {
        var packet = new byte[65535];
        var addr = new WAddr();
        while (running)
        {
            if (!Native.WinDivertRecv(handle, packet, (uint)packet.Length, out uint len, ref addr))
            {
                if (running) Thread.Sleep(50);
                continue;
            }
            try
            {
                if (addr.IPv6 || len < 28) continue;
                int ihl = (packet[0] & 0x0F) * 4;
                if (len < ihl + 8) continue;
                ushort dp = ReadU16(packet, ihl + 2);
                uint dip = ReadU32(packet, 16);

                var clientIp = new IPAddress(BitConverter.GetBytes(dip).Reverse().ToArray());
                if (!UdpRelay.TryGetFlow(clientIp, dp, out var flow)) continue;

                uint rip = BitConverter.ToUInt32(flow.RemoteIp.GetAddressBytes().Reverse().ToArray(), 0);
                Rewrite(packet, len, ref addr, rip, flow.RemotePort, dip, dp, false);
                Send(handle, packet, len, ref addr);
            }
            catch (Exception e) { ThreadLog("udp reply: " + e.Message); }
        }
    }

    private static int FindOwnerPidUdp(ushort localPort)
    {
        int size = 0;
        Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, UdpTableOwnerPid, 0);
        if (size <= 0) return 0;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetExtendedUdpTable(buf, ref size, false, 2, UdpTableOwnerPid, 0) == 0)
            {
                int rows = Marshal.ReadInt32(buf);
                IntPtr row = IntPtr.Add(buf, 4);
                for (int i = 0; i < rows; i++, row = IntPtr.Add(row, 12))
                {
                    uint rawPort = (uint)Marshal.ReadInt32(row, 4);
                    ushort lp = (ushort)IPAddress.NetworkToHostOrder((short)(rawPort & 0xFFFF));
                    if (lp == localPort) return Marshal.ReadInt32(row, 8);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return 0;
    }

    private static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    private static uint ReadU32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    private static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static string IpStr(uint ip) => new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip }).ToString();

}
