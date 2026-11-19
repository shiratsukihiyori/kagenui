using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Kagenui;

/// <summary>
/// 释放并加载内嵌的官方 WinDivert 2.2.2。
///
/// 安全要点：
/// 1. 释放目录放在 ProgramData，且 ACL 锁死为 SYSTEM + Administrators 完全控制，
///    不允许普通用户写入 —— 否则提权进程会从用户可写目录加载 DLL（本地提权链）。
/// 2. 每次加载前按官方 SHA-256 校验，只认这一个哈希。
/// 3. 校验通过后立刻加载，缩短"校验 → 加载"窗口。
/// </summary>
public static class Engine
{
    private const string DllSha256 = "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2";
    private const string SysSha256 = "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2";

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "kagenui", "engine");

    public static string DllPath => Path.Combine(Dir, "WinDivert.dll");
    public static string SysPath => Path.Combine(Dir, "WinDivert64.sys");

    public static bool Ensure(Action<string>? log = null)
    {
        try
        {
            HardenDirectory(log);
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

    /// <summary>把 DLL 预加载进进程，后续 DllImport("WinDivert.dll") 命中已加载模块。</summary>
    public static bool Preload(Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(DllPath)) return false;
            // 加载前再核一次哈希，压窄"校验 → 加载"的时间窗
            if (Sha256File(DllPath) != DllSha256)
            {
                log?.Invoke("WinDivert.dll 哈希不符，拒绝加载");
                return false;
            }
            NativeLibrary.Load(DllPath);
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke("加载 WinDivert.dll 失败: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 建目录并把 ACL 收紧：关闭继承，只给 SYSTEM 与 Administrators 完全控制。
    /// 完成后自查——如果任何普通用户组仍有写权限，直接抛错，宁可不跑也不留提权链。
    /// </summary>
    private static void HardenDirectory(Action<string>? log)
    {
        Directory.CreateDirectory(Dir);

        var di = new DirectoryInfo(Dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);                       // 断开继承，丢弃继承来的 ACE

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);

        // 自查：确认没有可写权限落到了普通用户身上
        var check = di.GetAccessControl();
        foreach (FileSystemAccessRule rule in check.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            if (sid.Equals(system) || sid.Equals(admins)) continue;

            bool writes = (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.Write)) != 0;
            if (writes)
                throw new UnauthorizedAccessException("释放目录仍可被 " + sid.Translate(typeof(NTAccount)).Value + " 写入，拒绝使用: " + Dir);
        }

        log?.Invoke("引擎目录已锁定 ACL: " + Dir);
    }

    private static void Extract(string resource, string target, string expectedSha, Action<string>? log)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);

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
