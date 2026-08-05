using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AcSpike;

/// <summary>
/// 只放 P/Invoke 与 token 取证。没有业务逻辑。
/// </summary>
internal static class Native
{
    // ---------- AppContainer profile ----------

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string pszAppContainerName, string pszDisplayName, string pszDescription,
        IntPtr pCapabilities, int dwCapabilityCount, out IntPtr ppSidAppContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeleteAppContainerProfile(string pszAppContainerName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string pszAppContainerName, out IntPtr ppsidAppContainerSid);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr FreeSid(IntPtr pSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool ConvertStringSidToSidW(string StringSid, out IntPtr Sid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ---------- process creation with security capabilities ----------

    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = (IntPtr)0x00020009;

    internal const uint SE_GROUP_ENABLED = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    // ---------- named pipe ----------

    internal const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    internal const uint PIPE_TYPE_BYTE = 0x00000000;
    internal const uint PIPE_WAIT = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
        uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ImpersonateNamedPipeClient(IntPtr hNamedPipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ConnectNamedPipe(IntPtr hNamedPipe, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DisconnectNamedPipe(IntPtr hNamedPipe);

    // ---------- 进程枚举(拿父 PID:D73 的父子校验那一半) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    // ---------- token ----------

    internal const int TokenGroups = 2;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenIsAppContainer = 29;
    internal const int TokenAppContainerSid = 31;

    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x00000010;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenThreadToken(IntPtr ThreadHandle, uint DesiredAccess, bool OpenAsSelf, out IntPtr TokenHandle);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation,
        int TokenInformationLength, out int ReturnLength);

    // ---------- restricted token (用来在提权上下文里造一个「普通用户等效」的 Medium token) ----------

    internal const uint DISABLE_MAX_PRIVILEGE = 0x1;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CreateRestrictedToken(
        IntPtr ExistingTokenHandle, uint Flags,
        uint DisableSidCount, SID_AND_ATTRIBUTES[] SidsToDisable,
        uint DeletePrivilegeCount, IntPtr PrivilegesToDelete,
        uint RestrictedSidCount, IntPtr SidsToRestrict,
        out IntPtr NewTokenHandle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool SetTokenInformation(
        IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength);

    internal const uint LOGON_WITH_PROFILE = 0x00000001;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessWithTokenW(
        IntPtr hToken, uint dwLogonFlags, string lpApplicationName, string lpCommandLine,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>
    /// ★ 文档:当 token 是【调用方自己主令牌的受限版本】时,**不需要** SeAssignPrimaryTokenPrivilege。
    /// CreateRestrictedToken 造出来的正是这种,所以提权管理员身份可以直接用这个 API。
    /// 用它是因为 CreateProcessWithTokenW 在本机一律 0xC0000142(STATUS_DLL_INIT_FAILED)。
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUserW(
        IntPtr hToken, string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    // ---------- token 取证辅助 ----------

    internal sealed class TokenFacts
    {
        public string UserSid;
        public string UserName;
        public bool IsAppContainer;
        public string AppContainerSid;
        public string IntegritySid;
        public string IntegrityLabel;
        public string AdministratorsState;   // absent / enabled / deny-only
        public List<string> CapabilitySids = new();
    }

    internal static TokenFacts DescribeCurrentToken()
    {
        var f = new TokenFacts();
        using var id = WindowsIdentity.GetCurrent();
        f.UserSid = id.User?.Value;
        f.UserName = id.Name;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var hTok))
            throw new InvalidOperationException("OpenProcessToken failed " + Marshal.GetLastWin32Error());
        try
        {
            f.IsAppContainer = QueryUint(hTok, TokenIsAppContainer) != 0;
            f.AppContainerSid = QuerySid(hTok, TokenAppContainerSid);

            var il = QueryLabel(hTok);
            f.IntegritySid = il;
            f.IntegrityLabel = il switch
            {
                "S-1-16-0" => "Untrusted",
                "S-1-16-4096" => "Low",
                "S-1-16-8192" => "Medium",
                "S-1-16-8448" => "Medium Plus",
                "S-1-16-12288" => "High",
                "S-1-16-16384" => "System",
                _ => "?" + il
            };

            f.AdministratorsState = "absent";
            foreach (var (sid, attrs) in QueryGroups(hTok))
            {
                if (sid.StartsWith("S-1-15-3-", StringComparison.Ordinal)) f.CapabilitySids.Add(sid);
                if (sid == "S-1-5-32-544")
                    f.AdministratorsState = (attrs & SE_GROUP_USE_FOR_DENY_ONLY) != 0 ? "deny-only" : "enabled";
            }
        }
        finally { CloseHandle(hTok); }
        return f;
    }

    internal static TokenFacts DescribeThreadToken()
    {
        // 用在 ImpersonateNamedPipeClient 之后:此时线程 token 就是管道对端的 token。
        if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out var hTok))
            throw new InvalidOperationException("OpenThreadToken failed " + Marshal.GetLastWin32Error());
        try
        {
            var f = new TokenFacts();
            using var id = new WindowsIdentity(hTok);
            f.UserSid = id.User?.Value;
            f.UserName = id.Name;
            f.IsAppContainer = QueryUint(hTok, TokenIsAppContainer) != 0;
            f.AppContainerSid = QuerySid(hTok, TokenAppContainerSid);
            var il = QueryLabel(hTok);
            f.IntegritySid = il;
            f.IntegrityLabel = il switch
            {
                "S-1-16-4096" => "Low",
                "S-1-16-8192" => "Medium",
                "S-1-16-12288" => "High",
                _ => "?" + il
            };
            f.AdministratorsState = "absent";
            foreach (var (sid, attrs) in QueryGroups(hTok))
            {
                if (sid.StartsWith("S-1-15-3-", StringComparison.Ordinal)) f.CapabilitySids.Add(sid);
                if (sid == "S-1-5-32-544")
                    f.AdministratorsState = (attrs & SE_GROUP_USE_FOR_DENY_ONLY) != 0 ? "deny-only" : "enabled";
            }
            return f;
        }
        finally { CloseHandle(hTok); }
    }

    private static uint QueryUint(IntPtr hTok, int cls)
    {
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetTokenInformation(hTok, cls, buf, 4, out _)) return 0;
            return (uint)Marshal.ReadInt32(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string QuerySid(IntPtr hTok, int cls)
    {
        GetTokenInformation(hTok, cls, IntPtr.Zero, 0, out int len);
        if (len <= 0) return null;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(hTok, cls, buf, len, out _)) return null;
            var p = Marshal.ReadIntPtr(buf);
            return p == IntPtr.Zero ? null : new SecurityIdentifier(p).Value;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string QueryLabel(IntPtr hTok)
    {
        GetTokenInformation(hTok, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
        if (len <= 0) return null;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(hTok, TokenIntegrityLevel, buf, len, out _)) return null;
            var lbl = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
            return new SecurityIdentifier(lbl.Label.Sid).Value;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IEnumerable<(string Sid, uint Attributes)> QueryGroups(IntPtr hTok)
    {
        var res = new List<(string, uint)>();
        GetTokenInformation(hTok, TokenGroups, IntPtr.Zero, 0, out int len);
        if (len <= 0) return res;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(hTok, TokenGroups, buf, len, out _)) return res;
            int count = Marshal.ReadInt32(buf);
            // TOKEN_GROUPS { DWORD GroupCount; SID_AND_ATTRIBUTES Groups[]; } —— 64 位下数组从 +8 起(对齐)
            int off = IntPtr.Size;
            int stride = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            for (int i = 0; i < count; i++)
            {
                var sa = Marshal.PtrToStructure<SID_AND_ATTRIBUTES>(buf + off + i * stride);
                if (sa.Sid == IntPtr.Zero) continue;
                res.Add((new SecurityIdentifier(sa.Sid).Value, sa.Attributes));
            }
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
