using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using SharpCompress.Readers;

namespace PACSify;

public class UpdateFetcher
{
    private static readonly HttpClient Client = new();
    private const string regPath=@"HKEY_LOCAL_MACHINE\Software\CMIH";

    public static void AutoUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            Update("https://us.deadnode.org/cmihupdate/");
            return;
        }
        var url=Registry.GetValue(regPath, "UpdateHref", null) as string;
        var user = Registry.GetValue(regPath, "UpdateUser", null) as string;
        var pass = Registry.GetValue(regPath, "UpdatePass", null) as string;
        if (user is not null && pass is not null)
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        Update(url);
    }

    public static void Update(string url)
    {
        try
        {
            using var tar = ReaderFactory.Open(Client.GetStreamAsync($"{url}?op=get").Result);
            while (tar.MoveToNextEntry())
            {
                if (tar.Entry.IsDirectory) continue;
                using var content = tar.OpenEntryStream();
                if (tar.Entry.Key.EndsWith(".docker"))
                {
                    LoadDocker(content);
                }
                else if (tar.Entry.Key.Equals("pacsify.exe", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Write to temporary file, then replace ourselves somehow:
                    var tmpname = $"{tar.Entry.Key}.tmp";
                    using var tmp = File.Create(tmpname);
                    content.CopyTo(tmp);
                    tmp.Close();
                    try
                    {
                        File.Replace(tmpname, tar.Entry.Key, $"{tar.Entry.Key}.bak");
                    }
                    catch (Exception)
                    {
                        if (!OperatingSystem.IsWindows())
                            throw;
                        if (!MoveFileEx(tmpname, tar.Entry.Key,
                                MoveFileFlags.MOVEFILE_REPLACE_EXISTING | MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                else
                {
                    using var output = File.Create(tar.Entry.Key);
                    content.CopyTo(output);
                }
            }
            using var body = new ReadOnlyMemoryContent(Array.Empty<byte>());
            Client.PostAsync($"{url}?op=success", body);
        }
        catch (Exception e)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                return;
            var data=Encoding.UTF8.GetBytes(e.Message + "\n" + e.StackTrace);
            using var body = new ReadOnlyMemoryContent(data);
            Client.PostAsync($"{url}?op=error",body);
        }
    }

    private static void LoadDocker(Stream image)
    {
        ProcessStartInfo psi = new("docker", "load")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        using Process proc = new()
        {
            StartInfo = psi
        };
        proc.Start();
        image.CopyTo(proc.StandardInput.BaseStream);
        proc.WaitForExit();
    }
    
    public static void Reboot()
    {
        IntPtr tokenHandle = IntPtr.Zero;

        try
        {
            // get process token
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES,
                out tokenHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open process token handle");
            }

            // lookup the shutdown privilege
            TOKEN_PRIVILEGES tokenPrivs = new TOKEN_PRIVILEGES();
            tokenPrivs.PrivilegeCount = 1;
            tokenPrivs.Privileges = new LUID_AND_ATTRIBUTES[1];
            tokenPrivs.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

            if (!LookupPrivilegeValue(null,
                SE_SHUTDOWN_NAME,
                out tokenPrivs.Privileges[0].Luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open lookup shutdown privilege");
            }

            // add the shutdown privilege to the process token
            if (!AdjustTokenPrivileges(tokenHandle,
                false,
                ref tokenPrivs,
                0,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to adjust process token privileges");
            }

            // reboot
            if (!ExitWindowsEx(ExitWindows.Reboot,
                    ShutdownReason.MajorApplication | 
            ShutdownReason.MinorInstallation | 
            ShutdownReason.FlagPlanned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to reboot system");
            }
        }
        finally
        {
            // close the process token
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }
        }
    }

    // everything from here on is from pinvoke.net

    [Flags]
    private enum ExitWindows : uint
    {
        // ONE of the following five:
        LogOff = 0x00,
        ShutDown = 0x01,
        Reboot = 0x02,
        PowerOff = 0x08,
        RestartApps = 0x40,
        // plus AT MOST ONE of the following two:
        Force = 0x04,
        ForceIfHung = 0x10,
    }

    [Flags]
    private enum ShutdownReason : uint
    {
        MajorApplication = 0x00040000,
        MajorHardware = 0x00010000,
        MajorLegacyApi = 0x00070000,
        MajorOperatingSystem = 0x00020000,
        MajorOther = 0x00000000,
        MajorPower = 0x00060000,
        MajorSoftware = 0x00030000,
        MajorSystem = 0x00050000,

        MinorBlueScreen = 0x0000000F,
        MinorCordUnplugged = 0x0000000b,
        MinorDisk = 0x00000007,
        MinorEnvironment = 0x0000000c,
        MinorHardwareDriver = 0x0000000d,
        MinorHotfix = 0x00000011,
        MinorHung = 0x00000005,
        MinorInstallation = 0x00000002,
        MinorMaintenance = 0x00000001,
        MinorMMC = 0x00000019,
        MinorNetworkConnectivity = 0x00000014,
        MinorNetworkCard = 0x00000009,
        MinorOther = 0x00000000,
        MinorOtherDriver = 0x0000000e,
        MinorPowerSupply = 0x0000000a,
        MinorProcessor = 0x00000008,
        MinorReconfig = 0x00000004,
        MinorSecurity = 0x00000013,
        MinorSecurityFix = 0x00000012,
        MinorSecurityFixUninstall = 0x00000018,
        MinorServicePack = 0x00000010,
        MinorServicePackUninstall = 0x00000016,
        MinorTermSrv = 0x00000020,
        MinorUnstable = 0x00000006,
        MinorUpgrade = 0x00000003,
        MinorWMI = 0x00000015,

        FlagUserDefined = 0x40000000,
        FlagPlanned = 0x80000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public UInt32 Attributes;
    }

    private struct TOKEN_PRIVILEGES
    {
        public UInt32 PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    private const UInt32 TOKEN_QUERY = 0x0008;
    private const UInt32 TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const UInt32 SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(ExitWindows uFlags, 
        ShutdownReason dwReason);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, 
        UInt32 DesiredAccess, 
        out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, 
        string lpName, 
        out LUID lpLuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)]bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        UInt32 Zero,
        IntPtr Null1,
        IntPtr Null2);

    [Flags]
    enum MoveFileFlags
    {
        MOVEFILE_REPLACE_EXISTING           = 0x00000001,
        MOVEFILE_COPY_ALLOWED               = 0x00000002,
        MOVEFILE_DELAY_UNTIL_REBOOT         = 0x00000004,
        MOVEFILE_WRITE_THROUGH              = 0x00000008,
        MOVEFILE_CREATE_HARDLINK            = 0x00000010,
        MOVEFILE_FAIL_IF_NOT_TRACKABLE      = 0x00000020
    }
    
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName,
        MoveFileFlags dwFlags);
}