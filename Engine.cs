using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kagenui;

/// <summary>释放内嵌的官方 WinDivert（2.2.2，作者 Basil 签名），实现单文件分发。</summary>
public static class Engine
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kagenui", "engine");

    public static string DllPath => Path.Combine(Dir, "WinDivert.dll");
    public static string SysPath => Path.Combine(Dir, "WinDivert64.sys");

    public static bool Ensure(Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Extract("engine.WinDivert.dll", DllPath, log);
            Extract("engine.WinDivert64.sys", SysPath, log);
            return File.Exists(DllPath);
        }
        catch (Exception e)
        {
            log?.Invoke("释放 WinDivert 失败: " + e.Message);
            return false;
        }
    }

    /// <summary>把 WinDivert.dll 预加载进进程，后续 DllImport("WinDivert.dll") 会命中已加载模块。</summary>
    public static bool Preload(Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(DllPath)) return false;
            NativeLibrary.Load(DllPath);
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke("加载 WinDivert.dll 失败: " + e.Message);
            return false;
        }
    }

    private static void Extract(string resource, string target, Action<string>? log)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);
        if (File.Exists(target) && new FileInfo(target).Length == src.Length) return;
        using var dst = File.Create(target);
        src.CopyTo(dst);
        log?.Invoke("已释放 " + Path.GetFileName(target));
    }
}
