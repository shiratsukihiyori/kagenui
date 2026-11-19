using System;
using System.Diagnostics;
using System.Text;

namespace Kagenui;

/// <summary>
/// 中继必须监听 0.0.0.0（目标地址被改写成客户端自己的 IP，绑 Loopback 收不到包），
/// 因此用防火墙把入站彻底挡在门外：规则只拦「从别的机器进来」，
/// 本机回环流量不受影响（Windows 防火墙默认不检查 loopback）。
/// </summary>
internal static class Firewall
{
    private const string TcpRule = "kagenui-relay-tcp-in";
    private const string UdpRule = "kagenui-relay-udp-in";

    public static void Ensure(Action<string> log)
    {
        Apply(log, TcpRule, "TCP", Redirector.RelayPort);
        Apply(log, UdpRule, "UDP", UdpRelay.LocalPort);
    }

    public static void Remove(Action<string> log)
    {
        Delete(log, TcpRule);
        Delete(log, UdpRule);
    }

    private static void Apply(Action<string> log, string name, string proto, int port)
    {
        if (Exists(name)) return;

        var (code, output) = Run($"advfirewall firewall add rule name=\"{name}\" dir=in action=block protocol={proto} localport={port} profile=any enable=yes");
        if (code == 0) log($"已加防火墙入站阻断 {proto}/{port}（防止局域网访问中继）");
        else log($"防火墙规则添加失败 {proto}/{port}: {output.Trim()}");
    }

    private static void Delete(Action<string> log, string name)
    {
        if (!Exists(name)) return;
        var (code, _) = Run($"advfirewall firewall delete rule name=\"{name}\"");
        if (code == 0) log("已移除防火墙规则 " + name);
    }

    private static bool Exists(string name)
    {
        var (code, _) = Run($"advfirewall firewall show rule name=\"{name}\"");
        return code == 0;
    }

    private static (int code, string output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (p.HasExited ? p.ExitCode : -1, outText);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }
}
