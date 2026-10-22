using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Isafom.Core;

namespace Isafom.Windows;

internal static class ProcessIdentity
{
    internal static string UserSid(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) throw new InstallerException(ErrorCode.Protocol);
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InstallerException(ErrorCode.Protocol);
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
}
