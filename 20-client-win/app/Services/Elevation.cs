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

    public const string RefuseMessage =
        "本程序不能以管理员身份运行。\n\n" +
        "本机的设备密钥绑定在你的普通用户身份上,提权运行会打不开它(症状是「密钥集不存在」)。\n" +
        "请关掉这个窗口,用普通方式(双击图标)重新打开。";
}
