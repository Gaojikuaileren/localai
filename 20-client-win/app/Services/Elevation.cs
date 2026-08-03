// P3c -- 完整性等级护栏(决议 D46)。
//
// 教训来自 P3b 实机上线:TPM/CNG 的用户密钥**绑定创建它的进程完整性等级**。
// 身份是在普通用户(Medium)下铸的,于是提权(High)进程打不开,报「密钥集不存在」——
// 这个坑当时烧掉了一整轮排查。客户端持有设备证书私钥,同样必须始终 Medium。
//
// ★★ 2026-08-03:这道护栏【从来没有生效过】,今天才被实测抓到。
//   旧实现遍历 `WindowsIdentity.GetCurrent().Groups` 找 `S-1-16-12288`。
//   .NET 的 `Groups` 走的是 **TokenGroups**,而完整性等级 SID 不在那里 ——
//   它在 **TokenIntegrityLevel**(TokenInformationClass = 25)里,是独立的一项。
//   于是 IsElevated() 对一个 High 完整性的进程照样返回 false:提权拉起的客户端
//   一声不吭就跑起来了,而文件头和 D46 都写着"会拒绝"。
//   实测证据:客户端进程 token 的完整性 SID = S-1-16-12288,Program.cs 的护栏却没触发,
//   而且它拉起的 Edge 继承了 High、被 Edge 自己的护栏挡回来 —— 用户先看到的是那条报错。
//
//   ⇒ 现在直接问 **TokenIntegrityLevel**,这才是上面那句注释一直声称的做法。
//   ⇒ 顺带一条教训:旧断言只搜到了"代码里有 IsElevated 这个调用",
//     没有任何一条去核**它算得对不对**。结构性断言看不见语义错误。

using System.Runtime.InteropServices;
using System.Security.Principal;

namespace LocalAI.Client.Services;

public static class Elevation
{
    // Mandatory Label RID:0x2000 = Medium(普通)· 0x3000 = High(管理员)· 0x4000 = System
    public const int RidMedium = 0x2000;
    public const int RidHigh = 0x3000;

    const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    /// <summary>
    /// 本进程的完整性等级 RID(0x2000=Medium,0x3000=High,0x4000=System);拿不到返回 null。
    /// ★ 这是**权威**来源:完整性 SID 只存在于 TokenIntegrityLevel,不在 TokenGroups 里。
    /// </summary>
    public static int? IntegrityRid()
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var token = id.Token;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var len);
            if (len <= 0) return null;
            buf = Marshal.AllocHGlobal(len);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out len)) return null;
            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
            var sid = Marshal.ReadIntPtr(buf);
            if (sid == IntPtr.Zero) return null;
            var countPtr = GetSidSubAuthorityCount(sid);
            if (countPtr == IntPtr.Zero) return null;
            int count = Marshal.ReadByte(countPtr);
            if (count <= 0) return null;
            var ridPtr = GetSidSubAuthority(sid, (uint)(count - 1));
            if (ridPtr == IntPtr.Zero) return null;
            return Marshal.ReadInt32(ridPtr);
        }
        catch { return null; }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// 当前进程是否运行在 High 及以上完整性等级(= 会打不开身份密钥)。
    /// ★ 判不出来时**不**阻断:宁可放行也不要让用户开不了应用(这不是安全边界,是防呆)。
    ///   ——但"判不出来"与"判出来是普通用户"是两件事,别混:依据记在 LastProbeNote 里。
    /// </summary>
    public static bool IsElevated()
    {
        var rid = IntegrityRid();
        LastProbeNote = rid is null ? "读不到完整性等级(按普通用户放行)" : $"完整性 RID = 0x{rid:X}";
        return rid is { } r && r >= RidHigh;
    }

    /// <summary>上一次判断的依据。★ 别让"没判出来所以放行"和"确认是普通用户"看起来一样。</summary>
    public static string LastProbeNote { get; private set; } = "(还没判过)";

    // ================================================================ 提权时自己降权重开
    // ★ 用户裁定(2026-08-03):「我们也要是管理员也可以直接用才对」。
    //   对 —— 直接拒绝是把一个我们能自己解决的问题丢回给用户。
    //   但**密钥模型不能动**:TPM/CNG 用户密钥绑定创建时的完整性等级,High 进程打不开 Medium 的键。
    //   ⇒ 正解是:检测到 High 就【用桌面 shell 的令牌把自己重开一遍】,然后安静退出。
    //     用户感觉是"右键以管理员运行也能用",而进程实际落在正确的 Medium 上下文里。
    //
    // ★ 为什么必须用 shell 的令牌,而不是 `explorer.exe <path>`:
    //   后者今天实测【不脱提权】(启动Edge.cmd 那次露的馅)。而 GetShellWindow() 指向的是
    //   用户桌面那个 explorer,它就跑在 Medium 上 —— 复制它的令牌来建进程,拿到的才是真的 Medium。
    //
    // ★ 失败就如实回落到拒绝(RefuseMessage),绝不"假装重开了"然后继续在 High 上跑。

    const uint TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAssignPrimary = 0x0001;
    const uint TokenImpersonate = 0x0004, TokenAdjustDefault = 0x0080, TokenAdjustSessionId = 0x0100;
    const uint MaximumAllowed = 0x02000000;
    const uint ProcessQueryInformation = 0x0400;
    const int SecurityImpersonation = 2, TokenPrimary = 1;
    const uint CreateUnicodeEnvironment = 0x00000400;

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? appName,
        string cmdLine, uint creationFlags, IntPtr env, string? curDir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    /// <summary>
    /// 拿桌面 shell 的令牌把自己重开一遍(Medium 完整性)。成功返回 true —— 调用方应当**立刻退出**。
    /// ★ 失败原因很多都是正常的(没有桌面 shell、被策略挡住),所以失败不是错误,
    ///   只是要如实回落到拒绝,而不是继续在 High 上跑。
    /// </summary>
    public static bool TryRelaunchAtMediumIntegrity(string[] args)
    {
        IntPtr shellProc = IntPtr.Zero, shellTok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) { RelaunchNote = "拿不到本程序路径"; return false; }

            var hwnd = GetShellWindow();
            if (hwnd == IntPtr.Zero) { RelaunchNote = "没有桌面 shell 可借令牌"; return false; }
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) { RelaunchNote = "取不到桌面 shell 的进程"; return false; }

            shellProc = OpenProcess(ProcessQueryInformation, false, pid);
            if (shellProc == IntPtr.Zero) { RelaunchNote = "打不开桌面 shell 进程"; return false; }
            if (!OpenProcessToken(shellProc, TokenDuplicate | TokenQuery, out shellTok))
            { RelaunchNote = "打不开桌面 shell 的令牌"; return false; }
            if (!DuplicateTokenEx(shellTok,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenImpersonate | TokenAdjustDefault | TokenAdjustSessionId,
                    IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup))
            { RelaunchNote = "复制令牌失败"; return false; }

            // 命令行:第一个参数是程序自身(带引号),后面原样带上
            var quoted = "\"" + exe + "\"";
            foreach (var a in args) quoted += " \"" + a.Replace("\"", "\\\"") + "\"";

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessWithTokenW(dup, 0, null, quoted, CreateUnicodeEnvironment, IntPtr.Zero,
                                         Path.GetDirectoryName(exe), ref si, out var pi))
            { RelaunchNote = "用桌面令牌建进程失败(Win32 " + Marshal.GetLastWin32Error() + ")"; return false; }

            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            RelaunchNote = "已用桌面 shell 的令牌以普通用户身份重开";
            return true;
        }
        catch (Exception ex) { RelaunchNote = ex.GetType().Name + ": " + ex.Message; return false; }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (shellTok != IntPtr.Zero) CloseHandle(shellTok);
            if (shellProc != IntPtr.Zero) CloseHandle(shellProc);
        }
    }

    /// <summary>上一次降权重开的结果说明 —— 失败时要让人看得到为什么。</summary>
    public static string RelaunchNote { get; private set; } = "(没试过)";

    public const string RefuseMessage =
        "本程序不能以管理员身份运行。\n\n" +
        "本机的设备密钥绑定在你的普通用户身份上,提权运行会打不开它(症状是「密钥集不存在」)。\n" +
        "请关掉这个窗口,用普通方式(双击图标)重新打开。";
}
