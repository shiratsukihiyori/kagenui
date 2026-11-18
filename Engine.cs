using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kagenui;

/// <summary>释放内嵌的官方 WinDivert（2.2.2，作者 Basil 签名），实现单文件分发。</summary>
public static class Engine
{
    // 官方 reqrypt.org WinDivert-2.2.2-A x64 的 SHA-256，用于校验释放出来的文件没被替换
    private const string DllSha256 = "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2";
    private const string SysSha256 = "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2";

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kagenui", "engine");

    public static string DllPath => Path.Combine(Dir, "WinDivert.dll");
    public static string SysPath => Path.Combine(Dir, "WinDivert64.sys");

    public static bool Ensure(Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Extract("engine.WinDivert.dll", DllPath, DllSha256, log);
            Extract("engine.WinDivert64.sys", SysPath, SysSha256, log);
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

    private static void Extract(string resource, string target, string expectedSha, Action<string>? log)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);

        // 已存在且哈希一致 → 复用；否则重写。只比大小会被人用同大小文件替换（提权）。
        if (File.Exists(target) && Sha256File(target) == expectedSha) return;

        using (var dst = File.Create(target)) src.CopyTo(dst);

        var actual = Sha256File(target);
        if (actual != expectedSha)
            throw new InvalidDataException(Path.GetFileName(target) + " 校验失败，拒绝使用（期望 " + expectedSha[..16] + "，实际 " + actual[..16] + "）");

        log?.Invoke("已释放 " + Path.GetFileName(target));
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
