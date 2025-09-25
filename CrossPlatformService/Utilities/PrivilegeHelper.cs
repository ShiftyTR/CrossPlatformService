using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace CrossPlatformService.Utilities;

//// <summary>
//// Platform-specific elevation helpers (admin / root).
//// </summary>
internal static class PrivilegeHelper
{
    public static bool IsElevated()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsWindowsElevated();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return UnixIsRoot();
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool UnixIsRoot()
    {
        try
        {
            // getuid == 0 => root user
            return GetEuid() == 0;
        }
        catch
        {
            return false;
        }
    }

    // POSIX getuid
    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEuid()
    {
        return geteuid();
    }

    public static void EnsureElevated(string operationDescription)
    {
        if (!IsElevated())
            throw new InvalidOperationException($"'{operationDescription}' requires elevated (admin/root) privileges.");
    }
}
